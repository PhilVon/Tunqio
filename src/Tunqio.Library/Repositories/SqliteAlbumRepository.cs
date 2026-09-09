using System.Text;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.Library.Database;

namespace Tunqio.Library.Repositories;

/// <summary>
/// <see cref="IAlbumRepository"/>: the Albums grid (keyset-paged like tracks) and the detail page. Albums with
/// no present track are hidden, so a folder that went offline takes its albums with it until it returns.
/// </summary>
public sealed class SqliteAlbumRepository : IAlbumRepository
{
    /// <summary>Album row plus per-album aggregates over present tracks; the mapper reads these ordinals.</summary>
    private const string Select = """
        SELECT al.id, al.title, al.album_artist_id, aa.name AS album_artist, al.year, al.disc_count, al.art_hash,
               s.track_count, s.total_ms, s.added_at, s.last_played_at
        """;

    private const string From = """
         FROM album al
         LEFT JOIN artist aa ON aa.id = al.album_artist_id
         JOIN (SELECT album_id, COUNT(*) AS track_count, SUM(duration_ms) AS total_ms, MAX(added_at) AS added_at, MAX(last_played_at) AS last_played_at
               FROM track WHERE missing = 0 AND album_id IS NOT NULL GROUP BY album_id) s ON s.album_id = al.id
        """;

    private const string IdExpression = "al.id";

    private readonly LibraryDatabase _db;
    private readonly ITrackRepository _tracks;

    public SqliteAlbumRepository(LibraryDatabase db, ITrackRepository tracks)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tracks);
        _db = db;
        _tracks = tracks;
    }

    /// <summary>Sort-key expressions matching <see cref="AlbumQuery.SortKeysOf"/>.</summary>
    internal static IReadOnlyList<string> SortKeyExpressions(AlbumSort sort) => sort switch
    {
        AlbumSort.Title => ["al.title COLLATE NOCASE"],
        AlbumSort.Artist => ["COALESCE(aa.name, " + Sql.NoText + ") COLLATE NOCASE", "al.title COLLATE NOCASE"],
        AlbumSort.Year => ["COALESCE(al.year, " + Sql.NoYear + ")", "al.title COLLATE NOCASE"],
        AlbumSort.Added => ["s.added_at"],
        AlbumSort.Played => ["COALESCE(s.last_played_at, " + Sql.NoTime + ")"],
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "unknown sort"),
    };

    public async Task<AlbumDto?> GetAsync(long id, CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, Select + From + " WHERE al.id = $id");
        command.Add("$id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<AlbumDetailDto?> GetDetailAsync(long id, CancellationToken ct = default)
    {
        AlbumDto? album = await GetAsync(id, ct).ConfigureAwait(false);
        if (album is null)
        {
            return null;
        }

        var tracks = new List<TrackDto>(album.TrackCount);
        await foreach (TrackDto track in _tracks.StreamAsync(new TrackQuery(TrackSort.Album, AlbumId: id, PageSize: 500), ct).ConfigureAwait(false))
        {
            tracks.Add(track);
        }

        var genres = new List<string>();
        await using (SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false))
        {
            await using SqliteCommand command = Sql.Command(connection, """
                SELECT DISTINCT g.name FROM genre g
                JOIN track_genre tg ON tg.genre_id = g.id
                JOIN track t ON t.id = tg.track_id
                WHERE t.album_id = $id AND t.missing = 0
                ORDER BY g.name COLLATE NOCASE
                """);
            command.Add("$id", id);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                genres.Add(reader.GetString(0));
            }
        }

        return new AlbumDetailDto(album, tracks, genres);
    }

    public async Task<IReadOnlyList<AlbumDto>> ListAsync(AlbumQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Page(connection, query, extraFilter: null);
        return await ReadAllAsync(command, query.PageSize, ct).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(AlbumQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, string.Empty);
        var sql = new StringBuilder("SELECT COUNT(*)").Append(From).Append(" WHERE 1 = 1");
        AppendFilters(sql, command, query);
        command.CommandText = sql.ToString();
        return checked((int)(long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!);
    }

    public async Task<AlbumFacets> ListFacetsAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        var decades = new List<int>();
        await using (SqliteCommand command = Sql.Command(connection, """
            SELECT DISTINCT (al.year / 10) * 10 FROM album al
            WHERE al.year IS NOT NULL AND EXISTS (SELECT 1 FROM track t WHERE t.album_id = al.id AND t.missing = 0)
            ORDER BY 1
            """))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                decades.Add(reader.GetInt32(0));
            }
        }

        var codecs = new List<string>();
        await using (SqliteCommand command = Sql.Command(connection, "SELECT DISTINCT codec FROM track WHERE missing = 0 ORDER BY codec"))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                codecs.Add(reader.GetString(0));
            }
        }

        return new AlbumFacets(decades, codecs);
    }

    /// <summary>Albums the artist is credited on without being the album artist ("Appears on").</summary>
    internal async Task<IReadOnlyList<AlbumDto>> AppearsOnAsync(long artistId, CancellationToken ct)
    {
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        const string appearsOn = " AND (al.album_artist_id IS NULL OR al.album_artist_id <> $appears)"
            + " AND EXISTS (SELECT 1 FROM track t JOIN track_artist ta ON ta.track_id = t.id WHERE t.album_id = al.id AND t.missing = 0 AND ta.artist_id = $appears)";
        await using SqliteCommand command = Page(connection, new AlbumQuery(AlbumSort.Year, PageSize: 1000), appearsOn);
        command.Add("$appears", artistId);
        return await ReadAllAsync(command, 16, ct).ConfigureAwait(false);
    }

    internal static SqliteCommand Page(SqliteConnection connection, AlbumQuery query, string? extraFilter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(query.PageSize, 1);
        SqliteCommand command = Sql.Command(connection, string.Empty);
        var sql = new StringBuilder(Select).Append(From).Append(" WHERE 1 = 1");
        AppendFilters(sql, command, query);
        sql.Append(extraFilter);
        IReadOnlyList<string> keys = SortKeyExpressions(query.Sort);
        if (query.After is not null)
        {
            Sql.AppendKeyset(sql, command, keys, IdExpression, query.After, query.Descending);
        }

        sql.Append(Sql.OrderBy(keys, IdExpression, query.Descending)).Append(" LIMIT $limit");
        command.Add("$limit", query.PageSize);
        command.CommandText = sql.ToString();
        return command;
    }

    private static void AppendFilters(StringBuilder sql, SqliteCommand command, AlbumQuery query)
    {
        if (query.ArtistId is { } artist)
        {
            sql.Append(" AND al.album_artist_id = $artist");
            command.Add("$artist", artist);
        }

        if (query.GenreId is { } genre)
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM track t JOIN track_genre tg ON tg.track_id = t.id WHERE t.album_id = al.id AND t.missing = 0 AND tg.genre_id = $genre)");
            command.Add("$genre", genre);
        }

        if (query.Decade is { } decade)
        {
            sql.Append(" AND al.year >= $decade AND al.year < $decade + 10");
            command.Add("$decade", decade - (decade % 10));
        }

        if (!string.IsNullOrEmpty(query.Codec))
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM track t WHERE t.album_id = al.id AND t.missing = 0 AND t.codec = $codec COLLATE NOCASE)");
            command.Add("$codec", query.Codec);
        }

        if (!string.IsNullOrEmpty(query.Text))
        {
            sql.Append(" AND (al.title LIKE $text ESCAPE '\\' OR aa.name LIKE $text ESCAPE '\\')");
            command.Add("$text", Sql.Like(query.Text));
        }
    }

    private static async Task<IReadOnlyList<AlbumDto>> ReadAllAsync(SqliteCommand command, int capacity, CancellationToken ct)
    {
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<AlbumDto>(capacity);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    private static AlbumDto Read(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        Title: r.GetString(1),
        AlbumArtistId: r.Long(2),
        AlbumArtist: r.Text(3),
        Year: r.Int(4),
        DiscCount: r.Int(5),
        ArtHash: r.Text(6),
        TrackCount: r.GetInt32(7),
        TotalDurationMs: r.GetInt64(8),
        AddedAt: r.GetInt64(9),
        LastPlayedAt: r.Long(10));
}
