using System.Text;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;

namespace Tunqio.Library.Repositories;

/// <summary>
/// Translates a <see cref="TrackQuery"/> into SQL: one SELECT shape, filters as predicates, the sort as a list
/// of key expressions, and the page as a row-value keyset predicate on those keys plus <c>t.id</c>
/// (docs/library-and-data.md, "Repository layer"). The key expressions coalesce nulls to the sentinels in
/// <see cref="SortKeys"/> so <see cref="TrackQuery.SortKeysOf"/> reproduces them from a <see cref="TrackDto"/>.
/// </summary>
internal static class TrackQueryBuilder
{
    /// <summary>Artist credits as <c>id U+001F name</c> records joined by U+001E, in credit order (<see cref="TrackRowMapper"/> splits them).</summary>
    private const string ArtistsColumn =
        "(SELECT group_concat(a.id || char(31) || a.name, char(30) ORDER BY ta.position, a.id) FROM track_artist ta JOIN artist a ON a.id = ta.artist_id WHERE ta.track_id = t.id AND ta.role = 'artist')";

    private const string FirstArtist =
        "(SELECT a.name FROM track_artist ta JOIN artist a ON a.id = ta.artist_id WHERE ta.track_id = t.id AND ta.role = 'artist' ORDER BY ta.position, a.id LIMIT 1)";

    /// <summary>The SELECT list <see cref="TrackRowMapper"/> reads, in ordinal order.</summary>
    public const string Select =
        "SELECT t.id, t.folder_id, t.path, t.title, " + ArtistsColumn + " AS artists,"
        + " t.album_id, al.title AS album_title, aa.name AS album_artist,"
        + " t.track_no, t.disc_no, t.year, t.duration_ms, t.codec, t.bitrate_kbps, t.sample_rate, t.channels, t.bit_depth,"
        + " t.file_size, t.file_mtime, t.composer, t.comment,"
        + " t.rg_track_gain, t.rg_track_peak, t.rg_album_gain, t.rg_album_peak,"
        + " COALESCE(t.art_hash, al.art_hash) AS art_hash, t.mbid, t.added_at, t.rating, t.play_count, t.last_played_at, t.missing";

    public const string From =
        " FROM track t LEFT JOIN album al ON al.id = t.album_id LEFT JOIN artist aa ON aa.id = al.album_artist_id";

    private const string IdExpression = "t.id";

    /// <summary>The sort-key expressions for <paramref name="sort"/>, matching <see cref="TrackQuery.SortKeysOf"/> one for one.</summary>
    public static IReadOnlyList<string> SortKeyExpressions(TrackSort sort) => sort switch
    {
        TrackSort.Title => ["t.title COLLATE NOCASE"],
        TrackSort.Artist => ["COALESCE(" + FirstArtist + ", " + Sql.NoText + ") COLLATE NOCASE", "COALESCE(al.title, " + Sql.NoText + ") COLLATE NOCASE", "COALESCE(t.disc_no, 0)", "COALESCE(t.track_no, 0)"],
        TrackSort.Album => ["COALESCE(al.title, " + Sql.NoText + ") COLLATE NOCASE", "COALESCE(t.disc_no, 0)", "COALESCE(t.track_no, 0)"],
        TrackSort.Duration => ["t.duration_ms"],
        TrackSort.Year => ["COALESCE(t.year, " + Sql.NoYear + ")"],
        TrackSort.Added => ["t.added_at"],
        TrackSort.LastPlayed => ["COALESCE(t.last_played_at, " + Sql.NoTime + ")"],
        TrackSort.PlayCount => ["t.play_count", "COALESCE(t.last_played_at, " + Sql.NoTime + ")"],
        TrackSort.Rating => ["COALESCE(t.rating, " + Sql.NoRating + ")"],
        TrackSort.Codec => ["t.codec COLLATE NOCASE"],
        _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "unknown sort"),
    };

    /// <summary>A page query: SELECT, filters, keyset, order, LIMIT.</summary>
    public static SqliteCommand Page(SqliteConnection connection, TrackQuery query)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(query.PageSize, 1);
        SqliteCommand command = Sql.Command(connection, string.Empty);
        var sql = new StringBuilder(Select).Append(From).Append(" WHERE 1 = 1");
        AppendFilters(sql, command, query);
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

    /// <summary><c>SELECT COUNT(*)</c> over the query's filters.</summary>
    public static SqliteCommand Count(SqliteConnection connection, TrackQuery query)
    {
        SqliteCommand command = Sql.Command(connection, string.Empty);
        var sql = new StringBuilder("SELECT COUNT(*)").Append(From).Append(" WHERE 1 = 1");
        AppendFilters(sql, command, query);
        command.CommandText = sql.ToString();
        return command;
    }

    private static void AppendFilters(StringBuilder sql, SqliteCommand command, TrackQuery query)
    {
        if (!query.IncludeMissing)
        {
            sql.Append(" AND t.missing = 0");
        }

        if (query.AlbumId is { } album)
        {
            sql.Append(" AND t.album_id = $album");
            command.Add("$album", album);
        }

        if (query.ArtistId is { } artist)
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM track_artist fa WHERE fa.track_id = t.id AND fa.artist_id = $artist)");
            command.Add("$artist", artist);
        }

        if (query.GenreId is { } genre)
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM track_genre fg WHERE fg.track_id = t.id AND fg.genre_id = $genre)");
            command.Add("$genre", genre);
        }

        if (query.FolderId is { } folder)
        {
            sql.Append(" AND t.folder_id = $folder");
            command.Add("$folder", folder);
        }

        if (!string.IsNullOrEmpty(query.Text))
        {
            sql.Append(" AND (t.title LIKE $text ESCAPE '\\' OR al.title LIKE $text ESCAPE '\\'")
               .Append(" OR EXISTS (SELECT 1 FROM track_artist ft JOIN artist fta ON fta.id = ft.artist_id WHERE ft.track_id = t.id AND fta.name LIKE $text ESCAPE '\\'))");
            command.Add("$text", Sql.Like(query.Text));
        }
    }
}

/// <summary>Maps a row of <see cref="TrackQueryBuilder.Select"/> to a <see cref="TrackDto"/>.</summary>
internal static class TrackRowMapper
{
    public static TrackDto Read(SqliteDataReader r)
    {
        double? rgTrackGain = r.Double(21);
        double? rgTrackPeak = r.Double(22);
        double? rgAlbumGain = r.Double(23);
        double? rgAlbumPeak = r.Double(24);
        ReplayGainTags? replayGain = rgTrackGain is null && rgTrackPeak is null && rgAlbumGain is null && rgAlbumPeak is null
            ? null
            : new ReplayGainTags(rgTrackGain, rgTrackPeak, rgAlbumGain, rgAlbumPeak);

        return new TrackDto(
            Id: r.GetInt64(0),
            FolderId: r.GetInt64(1),
            Path: r.GetString(2),
            Title: r.GetString(3),
            Artists: ParseArtists(r.Text(4)),
            AlbumId: r.Long(5),
            AlbumTitle: r.Text(6),
            AlbumArtist: r.Text(7),
            TrackNo: r.Int(8),
            DiscNo: r.Int(9),
            Year: r.Int(10),
            DurationMs: r.GetInt32(11),
            Codec: r.GetString(12),
            BitrateKbps: r.Int(13),
            SampleRate: r.Int(14),
            Channels: r.Int(15),
            BitDepth: r.Int(16),
            FileSize: r.GetInt64(17),
            FileMtime: r.GetInt64(18),
            Composer: r.Text(19),
            Comment: r.Text(20),
            ReplayGain: replayGain,
            ArtHash: r.Text(25),
            Mbid: r.Text(26),
            AddedAt: r.GetInt64(27),
            Rating: r.Int(28),
            PlayCount: r.GetInt32(29),
            LastPlayedAt: r.Long(30),
            Missing: r.GetInt64(31) != 0);
    }

    private static ArtistRef[] ParseArtists(string? packed)
    {
        if (string.IsNullOrEmpty(packed))
        {
            return [];
        }

        string[] records = packed.Split('');
        var artists = new ArtistRef[records.Length];
        for (int i = 0; i < records.Length; i++)
        {
            int split = records[i].IndexOf('', StringComparison.Ordinal);
            artists[i] = new ArtistRef(long.Parse(records[i].AsSpan(0, split), System.Globalization.CultureInfo.InvariantCulture), records[i][(split + 1)..]);
        }

        return artists;
    }
}
