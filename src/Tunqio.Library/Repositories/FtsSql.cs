namespace Tunqio.Library.Repositories;

/// <summary>
/// The SQL that derives a track's four indexed values (<c>track_fts</c>: title, artists, album, album_artist)
/// from its row. Shared by the maintenance in <see cref="SqliteTrackRepository"/> (per id, before and after a
/// write) and the rebuild in <see cref="SqliteSearchService"/> (every row at once), so the index can never hold
/// a value the row-level derivation would not produce.
/// </summary>
internal static class FtsSql
{
    /// <summary>The four indexed values, in <c>track_fts</c> column order, for the track aliased <c>t</c>.</summary>
    public const string Columns = """
        t.title,
        COALESCE((SELECT group_concat(a.name, ', ' ORDER BY ta.position, a.id) FROM track_artist ta JOIN artist a ON a.id = ta.artist_id WHERE ta.track_id = t.id AND ta.role = 'artist'), ''),
        COALESCE(al.title, ''), COALESCE(aa.name, '')
        """;

    public const string From = """
         FROM track t
         LEFT JOIN album al ON al.id = t.album_id
         LEFT JOIN artist aa ON aa.id = al.album_artist_id
        """;

    /// <summary>The values indexed for one track (<c>$id</c>).</summary>
    public const string Row = "SELECT " + Columns + From + " WHERE t.id = $id";

    public const string Delete = "INSERT INTO track_fts(track_fts, rowid, title, artists, album, album_artist) VALUES ('delete', $id, $title, $artists, $album, $album_artist)";

    public const string Insert = "INSERT INTO track_fts(rowid, title, artists, album, album_artist) VALUES ($id, $title, $artists, $album, $album_artist)";

    /// <summary>Empties a contentless table (the only bulk delete it supports).</summary>
    public const string DeleteAll = "INSERT INTO track_fts(track_fts) VALUES ('delete-all')";

    /// <summary>Indexes every track row in one statement.</summary>
    public const string InsertAll = "INSERT INTO track_fts(rowid, title, artists, album, album_artist) SELECT t.id, " + Columns + From;
}
