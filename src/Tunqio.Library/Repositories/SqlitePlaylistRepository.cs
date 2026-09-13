using System.Data.Common;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.Library.Database;

namespace Tunqio.Library.Repositories;

/// <summary>
/// <see cref="IPlaylistRepository"/> over <c>playlist</c> and <c>playlist_item</c> (E6-S1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a change rewrites the items rather than shifting positions.</b> <c>playlist_item</c>'s key is
/// <c>(playlist_id, position)</c>, so moving one item by updating positions in place collides with the row already at
/// the target on every step, and SQLite checks the key per row, not at the end of the statement. Removing and moving
/// therefore read the ordered track ids, change the list, and write it back in one transaction under the writer
/// lease. A playlist is hundreds of rows, not the 100k the track table is sized for, so this is cheap, and it keeps
/// positions contiguous by construction.
/// </para>
/// </remarks>
public sealed class SqlitePlaylistRepository : IPlaylistRepository
{
    private readonly LibraryDatabase _db;
    private readonly ITrackRepository _tracks;
    private readonly TimeProvider _clock;

    /// <param name="db">The library database.</param>
    /// <param name="tracks">Resolves a playlist's track ids to rows for the detail.</param>
    /// <param name="clock">Stamps <c>created_at</c> and <c>modified_at</c>.</param>
    public SqlitePlaylistRepository(LibraryDatabase db, ITrackRepository tracks, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tracks);
        _db = db;
        _tracks = tracks;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<PlaylistDto>> ListAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, SelectPlaylists + " GROUP BY p.id ORDER BY p.pinned DESC, p.name COLLATE NOCASE, p.id");
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<PlaylistDto>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    public async Task<PlaylistDetailDto?> GetDetailAsync(long id, CancellationToken ct = default)
    {
        PlaylistDto? playlist;
        List<long> ids;
        await using (SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            playlist = await GetAsync(connection, null, id, ct).ConfigureAwait(false);
            if (playlist is null)
            {
                return null;
            }

            ids = await ItemsAsync(connection, null, id, ct).ConfigureAwait(false);
        }

        IReadOnlyList<TrackDto> tracks = await _tracks.GetByIdsAsync(ids, ct).ConfigureAwait(false);
        return new PlaylistDetailDto(playlist, tracks);
    }

    public async Task<PlaylistDto> CreateAsync(string name, CancellationToken ct = default)
    {
        string trimmed = Name(name);
        long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, """
            INSERT INTO playlist(name, created_at, modified_at) VALUES ($name, $now, $now)
            RETURNING id, name, created_at, modified_at, pinned
            """);
        command.Add("$name", trimmed);
        command.Add("$now", now);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return new PlaylistDto(reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4) != 0, 0, 0);
    }

    public async Task RenameAsync(long id, string name, CancellationToken ct = default)
    {
        string trimmed = Name(name);
        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, "UPDATE playlist SET name = $name, modified_at = $now WHERE id = $id");
        command.Add("$name", trimmed);
        command.Add("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds());
        command.Add("$id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        // playlist_item cascades.
        await using SqliteCommand command = Sql.Command(connection, "DELETE FROM playlist WHERE id = $id");
        command.Add("$id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task AddTracksAsync(long id, IReadOnlyList<long> trackIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        if (trackIds.Count == 0)
        {
            return;
        }

        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var tx = (SqliteTransaction)transaction;
        if (await GetAsync(connection, tx, id, ct).ConfigureAwait(false) is null)
        {
            return;
        }

        List<long> items = await ItemsAsync(connection, tx, id, ct).ConfigureAwait(false);
        int position = items.Count;
        await using (SqliteCommand insert = Sql.Command(connection, """
            INSERT INTO playlist_item(playlist_id, position, track_id)
            SELECT $playlist, $position, id FROM track WHERE id = $track
            """, tx))
        {
            insert.Add("$playlist", id);
            insert.Add("$position", 0L);
            insert.Add("$track", 0L);
            foreach (long trackId in trackIds)
            {
                insert.Set("$position", (long)position);
                insert.Set("$track", trackId);
                if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
                {
                    position++; // an id the library does not have inserted nothing and takes no position
                }
            }
        }

        await TouchAsync(connection, tx, id, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public Task RemoveAtAsync(long id, IReadOnlyList<int> positions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(positions);
        return RewriteAsync(id, items =>
        {
            foreach (int position in positions)
            {
                ArgumentOutOfRangeException.ThrowIfNegative(position, nameof(positions));
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(position, items.Count, nameof(positions));
            }

            // Highest first, so an earlier removal does not move the positions still to be removed.
            foreach (int position in positions.Distinct().OrderDescending())
            {
                items.RemoveAt(position);
            }
        }, ct);
    }

    public Task MoveAsync(long id, int fromPosition, int toPosition, CancellationToken ct = default) => RewriteAsync(id, items =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fromPosition);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fromPosition, items.Count);
        ArgumentOutOfRangeException.ThrowIfNegative(toPosition);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(toPosition, items.Count);
        long moved = items[fromPosition];
        items.RemoveAt(fromPosition);
        items.Insert(toPosition, moved);
    }, ct);

    public Task ReplaceTracksAsync(long id, IReadOnlyList<long> trackIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        return RewriteAsync(id, items =>
        {
            items.Clear();
            items.AddRange(trackIds);
        }, ct);
    }

    private const string SelectPlaylists = """
        SELECT p.id, p.name, p.created_at, p.modified_at, p.pinned, COUNT(t.id), COALESCE(SUM(t.duration_ms), 0)
        FROM playlist p
        LEFT JOIN playlist_item i ON i.playlist_id = p.id
        LEFT JOIN track t ON t.id = i.track_id
        """;

    private static string Name(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Trim();
    }

    private static PlaylistDto Read(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4) != 0, r.GetInt32(5), r.GetInt64(6));

    private static async Task<PlaylistDto?> GetAsync(SqliteConnection connection, SqliteTransaction? tx, long id, CancellationToken ct)
    {
        await using SqliteCommand command = Sql.Command(connection, SelectPlaylists + " WHERE p.id = $id GROUP BY p.id", tx);
        command.Add("$id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static async Task<List<long>> ItemsAsync(SqliteConnection connection, SqliteTransaction? tx, long id, CancellationToken ct)
    {
        await using SqliteCommand command = Sql.Command(connection, "SELECT track_id FROM playlist_item WHERE playlist_id = $id ORDER BY position", tx);
        command.Add("$id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var ids = new List<long>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private async Task TouchAsync(SqliteConnection connection, SqliteTransaction tx, long id, CancellationToken ct)
    {
        await using SqliteCommand touch = Sql.Command(connection, "UPDATE playlist SET modified_at = $now WHERE id = $id", tx);
        touch.Add("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds());
        touch.Add("$id", id);
        await touch.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the items, lets <paramref name="change"/> edit the list, and writes it back in one transaction.</summary>
    private async Task RewriteAsync(long id, Action<List<long>> change, CancellationToken ct)
    {
        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var tx = (SqliteTransaction)transaction;
        if (await GetAsync(connection, tx, id, ct).ConfigureAwait(false) is null)
        {
            return;
        }

        List<long> items = await ItemsAsync(connection, tx, id, ct).ConfigureAwait(false);
        change(items);

        await using (SqliteCommand clear = Sql.Command(connection, "DELETE FROM playlist_item WHERE playlist_id = $id", tx))
        {
            clear.Add("$id", id);
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Selected from track rather than inserted as values: a replacement written by undo can name a track that has
        // left the library since the edit it undoes, and it takes no position instead of failing the whole write.
        await using (SqliteCommand insert = Sql.Command(connection, """
            INSERT INTO playlist_item(playlist_id, position, track_id)
            SELECT $playlist, $position, id FROM track WHERE id = $track
            """, tx))
        {
            insert.Add("$playlist", id);
            insert.Add("$position", 0L);
            insert.Add("$track", 0L);
            long position = 0;
            foreach (long trackId in items)
            {
                insert.Set("$position", position);
                insert.Set("$track", trackId);
                if (await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
                {
                    position++;
                }
            }
        }

        await TouchAsync(connection, tx, id, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}
