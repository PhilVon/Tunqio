using System.Data.Common;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.Library.Database;

namespace Tunqio.Library.Repositories;

/// <summary>
/// <see cref="ITrackRepository"/> over <see cref="LibraryDatabase"/>. Reads take a pooled connection each;
/// writes take the writer lease and one transaction. The contentless <c>track_fts</c> table is kept in step
/// inside that transaction: its rows can only be deleted by re-supplying the indexed values, so every write
/// re-derives them from the row with <see cref="FtsSql.Row"/> before and after the change. The batch upsert does
/// all of its FTS writes after the last track write: a track statement that follows an FTS insert in the same
/// transaction makes FTS5 flush its pending terms (measured: 500 interleaved upserts cost 280 ms on the 100k
/// database against 10 ms with the FTS writes gathered at the end).
/// </summary>
public sealed class SqliteTrackRepository : ITrackRepository
{
    private const string UpsertSql = """
        INSERT INTO track(folder_id, path, file_size, file_mtime, title, album_id, track_no, disc_no, year, duration_ms,
                          bitrate_kbps, sample_rate, channels, bit_depth, codec, composer, comment,
                          rg_track_gain, rg_track_peak, rg_album_gain, rg_album_peak, art_hash, mbid, added_at, missing)
        VALUES ($folder, $path, $size, $mtime, $title, $album, $track_no, $disc_no, $year, $duration,
                $bitrate, $sample_rate, $channels, $bit_depth, $codec, $composer, $comment,
                $rg_tg, $rg_tp, $rg_ag, $rg_ap, $art, $mbid, $added, 0)
        ON CONFLICT(path) DO UPDATE SET
            folder_id = excluded.folder_id, file_size = excluded.file_size, file_mtime = excluded.file_mtime,
            title = excluded.title, album_id = excluded.album_id, track_no = excluded.track_no, disc_no = excluded.disc_no,
            year = excluded.year, duration_ms = excluded.duration_ms, bitrate_kbps = excluded.bitrate_kbps,
            sample_rate = excluded.sample_rate, channels = excluded.channels, bit_depth = excluded.bit_depth,
            codec = excluded.codec, composer = excluded.composer, comment = excluded.comment,
            rg_track_gain = excluded.rg_track_gain, rg_track_peak = excluded.rg_track_peak,
            rg_album_gain = excluded.rg_album_gain, rg_album_peak = excluded.rg_album_peak,
            art_hash = excluded.art_hash, mbid = excluded.mbid, missing = 0, missing_since = NULL
        RETURNING id
        """;

    private readonly LibraryDatabase _db;
    private readonly TimeProvider _clock;
    private readonly StringPool _pool = new();

    public SqliteTrackRepository(LibraryDatabase db, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<TrackDto?> GetAsync(long id, CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, TrackQueryBuilder.Select + TrackQueryBuilder.From + " WHERE t.id = $id");
        command.Add("$id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? TrackRowMapper.Read(reader, _pool) : null;
    }

    public async Task<TrackDto?> GetByPathAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        // Exact match on the unique path column: the scanner writes the path it walked, so a lookup for a file
        // the user opened finds the row only when it is the same file, which is what "already in the library"
        // has to mean here.
        await using SqliteCommand command = Sql.Command(connection, TrackQueryBuilder.Select + TrackQueryBuilder.From + " WHERE t.path = $path");
        command.Add("$path", path);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? TrackRowMapper.Read(reader, _pool) : null;
    }

    public async Task<IReadOnlyList<TrackDto>> GetByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return [];
        }

        var byId = new Dictionary<long, TrackDto>(ids.Count);
        await using (SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            const int chunk = 500; // SQLite's default parameter limit is far higher, but keep statements short
            for (int start = 0; start < ids.Count; start += chunk)
            {
                await using SqliteCommand command = Sql.Command(connection, string.Empty);
                var names = new string[Math.Min(chunk, ids.Count - start)];
                for (int i = 0; i < names.Length; i++)
                {
                    names[i] = "$i" + i.ToString(CultureInfo.InvariantCulture);
                    command.Add(names[i], ids[start + i]);
                }

                command.CommandText = TrackQueryBuilder.Select + TrackQueryBuilder.From + " WHERE t.id IN (" + string.Join(", ", names) + ")";
                await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    TrackDto track = TrackRowMapper.Read(reader, _pool);
                    byId[track.Id] = track;
                }
            }
        }

        var ordered = new List<TrackDto>(ids.Count);
        foreach (long id in ids)
        {
            if (byId.TryGetValue(id, out TrackDto? track))
            {
                ordered.Add(track);
            }
        }

        return ordered;
    }

    public async Task<IReadOnlyList<TrackDto>> ListAsync(TrackQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = TrackQueryBuilder.Page(connection, query);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var page = new List<TrackDto>(query.PageSize);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            page.Add(TrackRowMapper.Read(reader, _pool));
        }

        return page;
    }

    public async IAsyncEnumerable<TrackDto> StreamAsync(TrackQuery query, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        int remaining = query.Take ?? int.MaxValue;
        TrackQuery page = query;
        while (remaining > 0)
        {
            IReadOnlyList<TrackDto> rows = await ListAsync(page with { PageSize = Math.Min(query.PageSize, remaining) }, ct).ConfigureAwait(false);
            foreach (TrackDto row in rows)
            {
                yield return row;
            }

            remaining -= rows.Count;
            if (rows.Count < Math.Min(query.PageSize, remaining + rows.Count))
            {
                yield break;
            }

            page = page with { After = query.CursorAfter(rows[^1]) };
        }
    }

    public async Task<int> CountAsync(TrackQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = TrackQueryBuilder.Count(connection, query);
        return checked((int)(long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!);
    }

    public async Task UpsertBatchAsync(IReadOnlyList<ScannedTrack> tracks, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            return;
        }

        if (tracks.Select(t => t.Path).Distinct(StringComparer.Ordinal).Count() != tracks.Count)
        {
            tracks = tracks.GroupBy(t => t.Path, StringComparer.Ordinal).Select(g => g.Last()).ToArray(); // last report of a path wins
        }

        long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using DbTransaction dbTransaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var transaction = (SqliteTransaction)dbTransaction;
        using var resolver = new EntityResolver(connection, transaction);
        await using var fts = new FtsMaintainer(connection, transaction);
        await using SqliteCommand findByPath = Sql.Command(connection, "SELECT id FROM track WHERE path = $path", transaction);
        findByPath.Add("$path", string.Empty);
        await using SqliteCommand upsert = Sql.Command(connection, UpsertSql, transaction);
        foreach (string name in new[] { "$folder", "$path", "$size", "$mtime", "$title", "$album", "$track_no", "$disc_no", "$year", "$duration", "$bitrate", "$sample_rate", "$channels", "$bit_depth", "$codec", "$composer", "$comment", "$rg_tg", "$rg_tp", "$rg_ag", "$rg_ap", "$art", "$mbid", "$added" })
        {
            upsert.Add(name, null);
        }

        await using var links = new LinkWriter(connection, transaction);
        var stale = new List<FtsRow>();
        var written = new List<long>(tracks.Count);

        foreach (ScannedTrack track in tracks)
        {
            ct.ThrowIfCancellationRequested();
            findByPath.Set("$path", track.Path);
            if (await findByPath.ExecuteScalarAsync(ct).ConfigureAwait(false) is long existing
                && await fts.CaptureAsync(existing, ct).ConfigureAwait(false) is { } current)
            {
                stale.Add(current);
            }

            long? albumId = null;
            if (!string.IsNullOrWhiteSpace(track.AlbumTitle))
            {
                string? albumArtist = track.AlbumArtist ?? track.Artists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
                long? albumArtistId = string.IsNullOrWhiteSpace(albumArtist) ? null : await resolver.ArtistAsync(albumArtist, ct).ConfigureAwait(false);
                albumId = await resolver.AlbumAsync(track.AlbumTitle, albumArtistId, track.Year, track.DiscCount, track.AlbumArtHash, track.AlbumMbid, ct).ConfigureAwait(false);
            }

            upsert.Set("$folder", track.FolderId);
            upsert.Set("$path", track.Path);
            upsert.Set("$size", track.FileSize);
            upsert.Set("$mtime", track.FileMtime);
            upsert.Set("$title", string.IsNullOrWhiteSpace(track.Title) ? System.IO.Path.GetFileNameWithoutExtension(track.Path) : track.Title);
            upsert.Set("$album", albumId);
            upsert.Set("$track_no", track.TrackNo);
            upsert.Set("$disc_no", track.DiscNo);
            upsert.Set("$year", track.Year);
            upsert.Set("$duration", track.DurationMs);
            upsert.Set("$bitrate", track.BitrateKbps);
            upsert.Set("$sample_rate", track.SampleRate);
            upsert.Set("$channels", track.Channels);
            upsert.Set("$bit_depth", track.BitDepth);
            upsert.Set("$codec", track.Codec);
            upsert.Set("$composer", track.Composer);
            upsert.Set("$comment", track.Comment);
            upsert.Set("$rg_tg", track.ReplayGain?.TrackGainDb);
            upsert.Set("$rg_tp", track.ReplayGain?.TrackPeak);
            upsert.Set("$rg_ag", track.ReplayGain?.AlbumGainDb);
            upsert.Set("$rg_ap", track.ReplayGain?.AlbumPeak);
            upsert.Set("$art", track.TrackArtHash);
            upsert.Set("$mbid", track.Mbid);
            upsert.Set("$added", now);
            long id = (long)(await upsert.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

            await links.ReplaceArtistsAsync(id, track.Artists, resolver, ct).ConfigureAwait(false);
            await links.ReplaceGenresAsync(id, track.Genres ?? [], resolver, ct).ConfigureAwait(false);
            written.Add(id);
        }

        foreach (FtsRow row in stale)
        {
            await fts.RemoveAsync(row, ct).ConfigureAwait(false);
        }

        foreach (long id in written)
        {
            await fts.AddAsync(id, ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrackFileStamp>> SnapshotAsync(long folderId, CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, "SELECT id, path, file_size, file_mtime, missing FROM track WHERE folder_id = $folder");
        command.Add("$folder", folderId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var stamps = new List<TrackFileStamp>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            stamps.Add(new TrackFileStamp(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4) != 0));
        }

        return stamps;
    }

    public async Task MarkMissingAsync(IReadOnlyList<long> ids, bool missing, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        // The first marking stamps missing_since; later scans that still miss the file keep the stamp (Purge missing counts from it).
        await using SqliteCommand update = Sql.Command(connection, missing
            ? "UPDATE track SET missing = 1, missing_since = COALESCE(missing_since, $now) WHERE id = $id"
            : "UPDATE track SET missing = 0, missing_since = NULL WHERE id = $id", (SqliteTransaction)transaction);
        if (missing)
        {
            update.Add("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds());
        }

        update.Add("$id", 0L);
        foreach (long id in ids)
        {
            update.Set("$id", id);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<int> CountMissingAsync(long missingBefore, CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, "SELECT COUNT(*) FROM track WHERE " + PurgeWhere);
        command.Add("$before", missingBefore);
        return checked((int)(long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!);
    }

    public async Task<int> PurgeMissingAsync(long missingBefore, CancellationToken ct = default)
    {
        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        // The contentless FTS rows do not cascade; drop them by hand before the tracks go.
        await using (SqliteCommand fts = Sql.Command(connection,
            "INSERT INTO track_fts(track_fts, rowid, title, artists, album, album_artist) SELECT 'delete', t.id, " + FtsSql.Columns + FtsSql.From + " WHERE t." + PurgeWhere,
            (SqliteTransaction)transaction))
        {
            fts.Add("$before", missingBefore);
            await fts.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int deleted;
        await using (SqliteCommand delete = Sql.Command(connection, "DELETE FROM track WHERE " + PurgeWhere, (SqliteTransaction)transaction))
        {
            delete.Add("$before", missingBefore);
            deleted = await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>The rows Purge missing takes: flagged, and flagged since before <c>$before</c> (a row flagged before v2 carries the upgrade's stamp).</summary>
    private const string PurgeWhere = "missing = 1 AND missing_since IS NOT NULL AND missing_since < $before";

    public async Task UpdateTagsAsync(long id, TagEdit edit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (edit.Rating is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(edit), edit.Rating, "rating is 0..100");
        }

        if (edit.IsEmpty)
        {
            return;
        }

        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using DbTransaction dbTransaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var transaction = (SqliteTransaction)dbTransaction;

        TrackDto current;
        await using (SqliteCommand load = Sql.Command(connection, TrackQueryBuilder.Select + TrackQueryBuilder.From + " WHERE t.id = $id", transaction))
        {
            load.Add("$id", id);
            await using SqliteDataReader reader = await load.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new KeyNotFoundException($"Track {id} does not exist.");
            }

            current = TrackRowMapper.Read(reader, _pool);
        }

        using var resolver = new EntityResolver(connection, transaction);
        await using var fts = new FtsMaintainer(connection, transaction);
        await using var links = new LinkWriter(connection, transaction);
        if (await fts.CaptureAsync(id, ct).ConfigureAwait(false) is { } indexed)
        {
            await fts.RemoveAsync(indexed, ct).ConfigureAwait(false);
        }

        IReadOnlyList<string> artists = edit.Artists ?? current.Artists.Select(a => a.Name).ToArray();
        long? albumId = current.AlbumId;
        if (edit.AlbumTitle is not null || edit.AlbumArtist is not null || edit.Year is not null)
        {
            string? albumTitle = edit.AlbumTitle ?? current.AlbumTitle;
            if (string.IsNullOrWhiteSpace(albumTitle))
            {
                albumId = null;
            }
            else
            {
                string? albumArtist = edit.AlbumArtist ?? current.AlbumArtist ?? (artists.Count > 0 ? artists[0] : null);
                long? albumArtistId = string.IsNullOrWhiteSpace(albumArtist) ? null : await resolver.ArtistAsync(albumArtist, ct).ConfigureAwait(false);
                albumId = await resolver.AlbumAsync(albumTitle, albumArtistId, edit.Year ?? current.Year, null, null, null, ct).ConfigureAwait(false);
            }
        }

        await using (SqliteCommand update = Sql.Command(connection, """
            UPDATE track SET title = $title, album_id = $album, track_no = $track_no, disc_no = $disc_no, year = $year,
                             composer = $composer, comment = $comment, rating = $rating
            WHERE id = $id
            """, transaction))
        {
            update.Add("$title", edit.Title ?? current.Title);
            update.Add("$album", albumId);
            update.Add("$track_no", edit.TrackNo ?? current.TrackNo);
            update.Add("$disc_no", edit.DiscNo ?? current.DiscNo);
            update.Add("$year", edit.Year ?? current.Year);
            update.Add("$composer", edit.Composer ?? current.Composer);
            update.Add("$comment", edit.Comment ?? current.Comment);
            update.Add("$rating", edit.Rating ?? current.Rating);
            update.Add("$id", id);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (edit.Artists is not null)
        {
            await links.ReplaceArtistsAsync(id, edit.Artists, resolver, ct).ConfigureAwait(false);
        }

        if (edit.Genres is not null)
        {
            await links.ReplaceGenresAsync(id, edit.Genres, resolver, ct).ConfigureAwait(false);
        }

        await fts.AddAsync(id, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The values indexed for one track, as <see cref="FtsSql.Row"/> derives them.</summary>
    private sealed record FtsRow(long Id, string Title, string Artists, string Album, string AlbumArtist);

    /// <summary>Re-derives a track's FTS values from its row and issues the contentless delete / insert.</summary>
    private sealed class FtsMaintainer : IAsyncDisposable
    {
        private readonly SqliteCommand _row;
        private readonly SqliteCommand _delete;
        private readonly SqliteCommand _insert;

        public FtsMaintainer(SqliteConnection connection, SqliteTransaction transaction)
        {
            _row = Sql.Command(connection, FtsSql.Row, transaction);
            _row.Add("$id", 0L);
            _delete = Prepare(connection, FtsSql.Delete, transaction);
            _insert = Prepare(connection, FtsSql.Insert, transaction);
        }

        /// <summary>What the index currently holds for the track (read before the row changes), or null if the track does not exist.</summary>
        public async Task<FtsRow?> CaptureAsync(long id, CancellationToken ct)
        {
            _row.Set("$id", id);
            await using SqliteDataReader reader = await _row.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false)
                ? new FtsRow(id, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
                : null;
        }

        public Task RemoveAsync(FtsRow row, CancellationToken ct) => WriteAsync(_delete, row, ct);

        public async Task AddAsync(long id, CancellationToken ct)
        {
            if (await CaptureAsync(id, ct).ConfigureAwait(false) is { } row)
            {
                await WriteAsync(_insert, row, ct).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _row.DisposeAsync().ConfigureAwait(false);
            await _delete.DisposeAsync().ConfigureAwait(false);
            await _insert.DisposeAsync().ConfigureAwait(false);
        }

        private static SqliteCommand Prepare(SqliteConnection connection, string sql, SqliteTransaction transaction)
        {
            SqliteCommand command = Sql.Command(connection, sql, transaction);
            command.Add("$id", 0L);
            command.Add("$title", string.Empty);
            command.Add("$artists", string.Empty);
            command.Add("$album", string.Empty);
            command.Add("$album_artist", string.Empty);
            return command;
        }

        private static async Task WriteAsync(SqliteCommand command, FtsRow row, CancellationToken ct)
        {
            command.Set("$id", row.Id);
            command.Set("$title", row.Title);
            command.Set("$artists", row.Artists);
            command.Set("$album", row.Album);
            command.Set("$album_artist", row.AlbumArtist);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Replaces a track's artist credits and genre links.</summary>
    private sealed class LinkWriter : IAsyncDisposable
    {
        private readonly SqliteCommand _deleteArtists;
        private readonly SqliteCommand _insertArtist;
        private readonly SqliteCommand _deleteGenres;
        private readonly SqliteCommand _insertGenre;

        public LinkWriter(SqliteConnection connection, SqliteTransaction transaction)
        {
            _deleteArtists = Sql.Command(connection, "DELETE FROM track_artist WHERE track_id = $t", transaction);
            _deleteArtists.Add("$t", 0L);
            _insertArtist = Sql.Command(connection, "INSERT OR IGNORE INTO track_artist(track_id, artist_id, role, position) VALUES ($t, $a, 'artist', $p)", transaction);
            _insertArtist.Add("$t", 0L);
            _insertArtist.Add("$a", 0L);
            _insertArtist.Add("$p", 0L);
            _deleteGenres = Sql.Command(connection, "DELETE FROM track_genre WHERE track_id = $t", transaction);
            _deleteGenres.Add("$t", 0L);
            _insertGenre = Sql.Command(connection, "INSERT OR IGNORE INTO track_genre(track_id, genre_id) VALUES ($t, $g)", transaction);
            _insertGenre.Add("$t", 0L);
            _insertGenre.Add("$g", 0L);
        }

        public async Task ReplaceArtistsAsync(long trackId, IReadOnlyList<string> artists, EntityResolver resolver, CancellationToken ct)
        {
            _deleteArtists.Set("$t", trackId);
            await _deleteArtists.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            int position = 0;
            foreach (string name in artists.Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                _insertArtist.Set("$t", trackId);
                _insertArtist.Set("$a", await resolver.ArtistAsync(name, ct).ConfigureAwait(false));
                _insertArtist.Set("$p", (long)position++);
                await _insertArtist.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        public async Task ReplaceGenresAsync(long trackId, IReadOnlyList<string> genres, EntityResolver resolver, CancellationToken ct)
        {
            _deleteGenres.Set("$t", trackId);
            await _deleteGenres.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            foreach (string name in genres.Where(g => !string.IsNullOrWhiteSpace(g)))
            {
                _insertGenre.Set("$t", trackId);
                _insertGenre.Set("$g", await resolver.GenreAsync(name, ct).ConfigureAwait(false));
                await _insertGenre.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _deleteArtists.DisposeAsync().ConfigureAwait(false);
            await _insertArtist.DisposeAsync().ConfigureAwait(false);
            await _deleteGenres.DisposeAsync().ConfigureAwait(false);
            await _insertGenre.DisposeAsync().ConfigureAwait(false);
        }
    }
}
