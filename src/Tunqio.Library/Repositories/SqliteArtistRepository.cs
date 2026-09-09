using System.Text;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.Library.Database;

namespace Tunqio.Library.Repositories;

/// <summary>
/// <see cref="IArtistRepository"/>: artists with at least one present track credit or one album with present
/// tracks, ordered by sort name ("Beatles, The"), keyset-paged.
/// </summary>
public sealed class SqliteArtistRepository : IArtistRepository
{
    private const string Select = """
        SELECT a.id, a.name, a.sort_name, a.mbid,
               (SELECT COUNT(*) FROM album al WHERE al.album_artist_id = a.id
                  AND EXISTS (SELECT 1 FROM track t WHERE t.album_id = al.id AND t.missing = 0)) AS album_count,
               (SELECT COUNT(DISTINCT ta.track_id) FROM track_artist ta JOIN track t ON t.id = ta.track_id WHERE ta.artist_id = a.id AND t.missing = 0) AS track_count,
               (SELECT al.art_hash FROM album al WHERE al.album_artist_id = a.id AND al.art_hash IS NOT NULL ORDER BY al.year, al.id LIMIT 1) AS art_hash
        """;

    private const string From = """
         FROM artist a
         WHERE (EXISTS (SELECT 1 FROM track_artist ta JOIN track t ON t.id = ta.track_id WHERE ta.artist_id = a.id AND t.missing = 0)
             OR EXISTS (SELECT 1 FROM album al JOIN track t ON t.album_id = al.id WHERE al.album_artist_id = a.id AND t.missing = 0))
        """;

    private static readonly string[] Keys = ["a.sort_name COLLATE NOCASE"];

    private readonly LibraryDatabase _db;
    private readonly SqliteAlbumRepository _albums;

    public SqliteArtistRepository(LibraryDatabase db, SqliteAlbumRepository albums)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(albums);
        _db = db;
        _albums = albums;
    }

    public async Task<ArtistDto?> GetAsync(long id, CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, Select + From + " AND a.id = $id");
        command.Add("$id", id);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<ArtistDetailDto?> GetDetailAsync(long id, CancellationToken ct = default)
    {
        ArtistDto? artist = await GetAsync(id, ct).ConfigureAwait(false);
        if (artist is null)
        {
            return null;
        }

        var albums = new List<AlbumDto>(artist.AlbumCount);
        var query = new AlbumQuery(AlbumSort.Year, ArtistId: id, PageSize: 200);
        while (true)
        {
            IReadOnlyList<AlbumDto> page = await _albums.ListAsync(query, ct).ConfigureAwait(false);
            albums.AddRange(page);
            if (page.Count < query.PageSize)
            {
                break;
            }

            query = query with { After = query.CursorAfter(page[^1]) };
        }

        IReadOnlyList<AlbumDto> appearsOn = await _albums.AppearsOnAsync(id, ct).ConfigureAwait(false);
        return new ArtistDetailDto(artist, albums, appearsOn);
    }

    public async Task<IReadOnlyList<ArtistDto>> ListAsync(ArtistQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.PageSize, 1);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, string.Empty);
        var sql = new StringBuilder(Select).Append(From);
        AppendText(sql, command, query.Text);
        if (query.After is not null)
        {
            Sql.AppendKeyset(sql, command, Keys, "a.id", query.After, descending: false);
        }

        sql.Append(Sql.OrderBy(Keys, "a.id", descending: false)).Append(" LIMIT $limit");
        command.Add("$limit", query.PageSize);
        command.CommandText = sql.ToString();

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<ArtistDto>(query.PageSize);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    public async Task<int> CountAsync(ArtistQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, string.Empty);
        var sql = new StringBuilder("SELECT COUNT(*)").Append(From);
        AppendText(sql, command, query.Text);
        command.CommandText = sql.ToString();
        return checked((int)(long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))!);
    }

    private static void AppendText(StringBuilder sql, SqliteCommand command, string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            sql.Append(" AND a.name LIKE $text ESCAPE '\\'");
            command.Add("$text", Sql.Like(text));
        }
    }

    private static ArtistDto Read(SqliteDataReader r) => new(
        Id: r.GetInt64(0),
        Name: r.GetString(1),
        SortName: r.GetString(2),
        Mbid: r.Text(3),
        AlbumCount: r.GetInt32(4),
        TrackCount: r.GetInt32(5),
        ArtHash: r.Text(6));
}
