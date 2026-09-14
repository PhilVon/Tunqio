using Microsoft.Data.Sqlite;

namespace Tunqio.Library.Repositories;

/// <summary>
/// Resolves artist, genre and album names to row ids inside one write transaction, creating rows on first sight
/// and caching for the transaction's duration (docs/library-and-data.md, "Upsert ... through an in-memory cache").
/// Name lookups are case-insensitive, matching the schema's NOCASE uniqueness.
/// </summary>
internal sealed class EntityResolver : IDisposable
{
    private readonly Dictionary<string, long> _artists = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _genres = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Title, long? ArtistId, int? Year), long> _albums = new(AlbumKeyComparer.Instance);
    private readonly HashSet<long> _albumsRefreshed = [];

    private readonly SqliteCommand _findArtist;
    private readonly SqliteCommand _insertArtist;
    private readonly SqliteCommand _findGenre;
    private readonly SqliteCommand _insertGenre;
    private readonly SqliteCommand _findAlbum;
    private readonly SqliteCommand _insertAlbum;
    private readonly SqliteCommand _refreshAlbum;

    public EntityResolver(SqliteConnection connection, SqliteTransaction transaction)
    {
        _findArtist = Sql.Command(connection, "SELECT id FROM artist WHERE name = $name COLLATE NOCASE", transaction);
        _findArtist.Add("$name", string.Empty);
        _insertArtist = Sql.Command(connection, "INSERT INTO artist(name, sort_name) VALUES ($name, $sort) RETURNING id", transaction);
        _insertArtist.Add("$name", string.Empty);
        _insertArtist.Add("$sort", string.Empty);
        _findGenre = Sql.Command(connection, "SELECT id FROM genre WHERE name = $name COLLATE NOCASE", transaction);
        _findGenre.Add("$name", string.Empty);
        _insertGenre = Sql.Command(connection, "INSERT INTO genre(name) VALUES ($name) RETURNING id", transaction);
        _insertGenre.Add("$name", string.Empty);
        _findAlbum = Sql.Command(connection, "SELECT id FROM album WHERE title = $title COLLATE NOCASE AND album_artist_id IS $artist AND year IS $year", transaction);
        _findAlbum.Add("$title", string.Empty);
        _findAlbum.Add("$artist", null);
        _findAlbum.Add("$year", null);
        _insertAlbum = Sql.Command(connection, "INSERT INTO album(title, album_artist_id, year, disc_count, art_hash, mbid) VALUES ($title, $artist, $year, $discs, $art, $mbid) RETURNING id", transaction);
        _insertAlbum.Add("$title", string.Empty);
        _insertAlbum.Add("$artist", null);
        _insertAlbum.Add("$year", null);
        _insertAlbum.Add("$discs", null);
        _insertAlbum.Add("$art", null);
        _insertAlbum.Add("$mbid", null);
        // Art is not refreshed here: it was COALESCEd, which kept the first hash an album ever got for its whole
        // life (T-207). SqliteTrackRepository derives it from the album's tracks at the end of every batch instead.
        _refreshAlbum = Sql.Command(connection, "UPDATE album SET disc_count = COALESCE($discs, disc_count), mbid = COALESCE($mbid, mbid) WHERE id = $id", transaction);
        _refreshAlbum.Add("$discs", null);
        _refreshAlbum.Add("$mbid", null);
        _refreshAlbum.Add("$id", 0L);
    }

    public async Task<long> ArtistAsync(string name, CancellationToken ct)
    {
        name = name.Trim();
        if (_artists.TryGetValue(name, out long id))
        {
            return id;
        }

        _findArtist.Set("$name", name);
        object? found = await _findArtist.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (found is long existing)
        {
            id = existing;
        }
        else
        {
            _insertArtist.Set("$name", name);
            _insertArtist.Set("$sort", SortName(name));
            id = (long)(await _insertArtist.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        _artists[name] = id;
        return id;
    }

    public async Task<long> GenreAsync(string name, CancellationToken ct)
    {
        name = name.Trim();
        if (_genres.TryGetValue(name, out long id))
        {
            return id;
        }

        _findGenre.Set("$name", name);
        object? found = await _findGenre.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (found is long existing)
        {
            id = existing;
        }
        else
        {
            _insertGenre.Set("$name", name);
            id = (long)(await _insertGenre.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
        }

        _genres[name] = id;
        return id;
    }

    /// <summary>
    /// The album identified by (title, album artist, year) per "Album identity"; created on first sight. On the
    /// first sight of an existing album in this transaction, fills in disc count, art and MusicBrainz id it lacked.
    /// </summary>
    public async Task<long> AlbumAsync(string title, long? albumArtistId, int? year, int? discCount, string? artHash, string? mbid, CancellationToken ct)
    {
        title = title.Trim();
        (string, long?, int?) key = (title, albumArtistId, year);
        if (!_albums.TryGetValue(key, out long id))
        {
            _findAlbum.Set("$title", title);
            _findAlbum.Set("$artist", albumArtistId);
            _findAlbum.Set("$year", year);
            object? found = await _findAlbum.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (found is long existing)
            {
                id = existing;
            }
            else
            {
                _insertAlbum.Set("$title", title);
                _insertAlbum.Set("$artist", albumArtistId);
                _insertAlbum.Set("$year", year);
                _insertAlbum.Set("$discs", discCount);
                _insertAlbum.Set("$art", artHash);
                _insertAlbum.Set("$mbid", mbid);
                id = (long)(await _insertAlbum.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
                _albumsRefreshed.Add(id);
            }

            _albums[key] = id;
        }

        if ((discCount is not null || mbid is not null) && _albumsRefreshed.Add(id))
        {
            _refreshAlbum.Set("$discs", discCount);
            _refreshAlbum.Set("$mbid", mbid);
            _refreshAlbum.Set("$id", id);
            await _refreshAlbum.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return id;
    }

    /// <summary>"The Beatles" sorts as "Beatles, The" (docs/library-and-data.md, <c>artist.sort_name</c>).</summary>
    public static string SortName(string name)
    {
        foreach (string article in new[] { "The ", "A ", "An " })
        {
            if (name.Length > article.Length && name.StartsWith(article, StringComparison.OrdinalIgnoreCase))
            {
                return name[article.Length..] + ", " + name[..(article.Length - 1)];
            }
        }

        return name;
    }

    public void Dispose()
    {
        _findArtist.Dispose();
        _insertArtist.Dispose();
        _findGenre.Dispose();
        _insertGenre.Dispose();
        _findAlbum.Dispose();
        _insertAlbum.Dispose();
        _refreshAlbum.Dispose();
    }

    private sealed class AlbumKeyComparer : IEqualityComparer<(string Title, long? ArtistId, int? Year)>
    {
        public static readonly AlbumKeyComparer Instance = new();

        public bool Equals((string Title, long? ArtistId, int? Year) x, (string Title, long? ArtistId, int? Year) y) =>
            string.Equals(x.Title, y.Title, StringComparison.OrdinalIgnoreCase) && x.ArtistId == y.ArtistId && x.Year == y.Year;

        public int GetHashCode((string Title, long? ArtistId, int? Year) key) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Title), key.ArtistId, key.Year);
    }
}
