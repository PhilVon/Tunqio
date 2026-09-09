using System.Data.Common;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.Library.Database;

namespace Tunqio.Library.Repositories;

/// <summary>
/// <see cref="ISearchService"/> over the contentless trigram <c>track_fts</c> table (docs/library-and-data.md,
/// "FTS maintenance"). The text is split on whitespace; a term of three or more characters is a trigram MATCH
/// phrase (substring, case-insensitive), a shorter one is a <c>LIKE</c> the trigram index cannot answer, and
/// a query with no long term runs on <c>LIKE</c> alone so the first keystrokes still produce results.
/// <para>
/// Each group runs as three layers: a candidate set (the FTS or LIKE matches, present tracks only, cut at
/// <see cref="TrackCandidates"/> or <see cref="GroupCandidates"/> rows), a ranking over those ids on cheap
/// columns (a title or name that starts with the text first, then alphabetical), and the full row select for
/// the winners only. Measured on the 100k database: a three-character term matches 41k tracks; ranking every
/// match with <c>bm25()</c> costs 47 ms p95, the capped set under 4 ms. Albums come from tracks whose album or
/// album-artist column matched (<c>{album album_artist}: ...</c>) with per-album aggregates computed for the
/// winners only, never from the Albums grid's whole-table aggregate join (42 ms on 100k). Artists are the
/// names containing every term (the <c>artists</c> column is a joined list, so a column match would leak
/// co-credits) among the artists of the tracks whose artist columns matched.
/// </para>
/// </summary>
public sealed class SqliteSearchService : ISearchService
{
    /// <summary>Track matches read before ranking; a noisier query than this is refined by typing more.</summary>
    public const int TrackCandidates = 1000;

    /// <summary>Track rows read behind the album and artist groups, and album rows in the LIKE path.</summary>
    public const int GroupCandidates = 2000;

    private const string GroupCandidatesText = "2000";

    private const string Escape = " ESCAPE '\\'";
    private const string TrackRank = "(t.title LIKE $prefix" + Escape + ") DESC, t.title COLLATE NOCASE, t.id";
    private const string AlbumRank = "(al.title LIKE $prefix" + Escape + ") DESC, al.title COLLATE NOCASE, al.id";
    private const string ArtistRank = "(a.name LIKE $prefix" + Escape + ") DESC, a.sort_name COLLATE NOCASE, a.id";

    /// <summary>The Albums grid's row shape (<see cref="SqliteAlbumRepository.Read"/> ordinals) with the aggregates correlated per album.</summary>
    private const string AlbumSelect = """
        SELECT al.id, al.title, al.album_artist_id, aa.name AS album_artist, al.year, al.disc_count, al.art_hash,
               (SELECT COUNT(*) FROM track t WHERE t.album_id = al.id AND t.missing = 0),
               (SELECT COALESCE(SUM(t.duration_ms), 0) FROM track t WHERE t.album_id = al.id AND t.missing = 0),
               (SELECT COALESCE(MAX(t.added_at), 0) FROM track t WHERE t.album_id = al.id AND t.missing = 0),
               (SELECT MAX(t.last_played_at) FROM track t WHERE t.album_id = al.id AND t.missing = 0)
         FROM album al
         LEFT JOIN artist aa ON aa.id = al.album_artist_id
        """;

    private readonly LibraryDatabase _db;
    private readonly StringPool _pool = new();

    public SqliteSearchService(LibraryDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<SearchResults> SearchAsync(string text, SearchLimits limits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(limits);
        SearchTerms terms = SearchTerms.Parse(text);
        if (terms.IsEmpty)
        {
            return SearchResults.Empty(text);
        }

        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        (IReadOnlyList<TrackDto> tracks, bool moreTracks) = await TracksAsync(connection, terms, limits.Tracks, ct).ConfigureAwait(false);
        (IReadOnlyList<AlbumDto> albums, bool moreAlbums) = await AlbumsAsync(connection, terms, limits.Albums, ct).ConfigureAwait(false);
        (IReadOnlyList<ArtistDto> artists, bool moreArtists) = await ArtistsAsync(connection, terms, limits.Artists, ct).ConfigureAwait(false);
        return new SearchResults(text, tracks, moreTracks, albums, moreAlbums, artists, moreArtists);
    }

    public async Task<int> RebuildIndexAsync(CancellationToken ct = default)
    {
        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using DbTransaction dbTransaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var transaction = (SqliteTransaction)dbTransaction;
        await using (SqliteCommand clear = Sql.Command(connection, FtsSql.DeleteAll, transaction))
        {
            await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        int indexed;
        await using (SqliteCommand fill = Sql.Command(connection, FtsSql.InsertAll, transaction))
        {
            indexed = await fill.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return indexed;
    }

    private async Task<(IReadOnlyList<TrackDto> Rows, bool More)> TracksAsync(SqliteConnection connection, SearchTerms terms, int limit, CancellationToken ct)
    {
        if (limit <= 0)
        {
            return ([], false);
        }

        await using SqliteCommand command = Sql.Command(connection, string.Empty);
        var candidates = new StringBuilder();
        if (terms.UsesFts)
        {
            candidates.Append("SELECT t.id FROM track_fts f JOIN track t ON t.id = f.rowid LEFT JOIN album al ON al.id = t.album_id LEFT JOIN artist aa ON aa.id = al.album_artist_id")
                      .Append(" WHERE f.track_fts MATCH $match AND t.missing = 0");
            command.Add("$match", terms.Match());
            AppendTermFilters(candidates, command, terms.Short, TrackTermPredicate);
        }
        else
        {
            candidates.Append("SELECT t.id FROM track t LEFT JOIN album al ON al.id = t.album_id LEFT JOIN artist aa ON aa.id = al.album_artist_id WHERE t.missing = 0");
            AppendTermFilters(candidates, command, terms.All, TrackTermPredicate);
        }

        candidates.Append(" LIMIT ").Append(TrackCandidates);
        string ranked = "SELECT c.id FROM (" + candidates + ") c JOIN track t ON t.id = c.id ORDER BY " + TrackRank + " LIMIT $n";
        command.CommandText = TrackQueryBuilder.Select + TrackQueryBuilder.From + " WHERE t.id IN (" + ranked + ") ORDER BY " + TrackRank;
        command.Add("$prefix", Sql.StartsWith(terms.Text));
        command.Add("$n", limit + 1);

        var rows = new List<TrackDto>(limit + 1);
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(TrackRowMapper.Read(reader, _pool));
            }
        }

        return Cut(rows, limit);
    }

    private static async Task<(IReadOnlyList<AlbumDto> Rows, bool More)> AlbumsAsync(SqliteConnection connection, SearchTerms terms, int limit, CancellationToken ct)
    {
        if (limit <= 0)
        {
            return ([], false);
        }

        await using SqliteCommand command = Sql.Command(connection, string.Empty);
        var candidates = new StringBuilder();
        if (terms.UsesFts)
        {
            // The cap is on track rows read, not on distinct albums: asking for 2000 distinct albums reads
            // 16 tracks for each of them (measured 12 ms against 1 ms).
            candidates.Append("SELECT DISTINCT r.id FROM (SELECT t.album_id AS id FROM track_fts f JOIN track t ON t.id = f.rowid JOIN album al ON al.id = t.album_id LEFT JOIN artist aa ON aa.id = al.album_artist_id")
                      .Append(" WHERE f.track_fts MATCH $match AND t.missing = 0");
            command.Add("$match", terms.Match("album album_artist"));
            AppendTermFilters(candidates, command, terms.Short, AlbumTermPredicate);
            candidates.Append(" LIMIT ").Append(GroupCandidates).Append(") r");
        }
        else
        {
            candidates.Append("SELECT al.id FROM album al LEFT JOIN artist aa ON aa.id = al.album_artist_id")
                      .Append(" WHERE EXISTS (SELECT 1 FROM track t WHERE t.album_id = al.id AND t.missing = 0)");
            AppendTermFilters(candidates, command, terms.All, AlbumTermPredicate);
            candidates.Append(" LIMIT ").Append(GroupCandidates);
        }

        string ranked = "SELECT c.id FROM (" + candidates + ") c JOIN album al ON al.id = c.id ORDER BY " + AlbumRank + " LIMIT $n";
        command.CommandText = AlbumSelect + " WHERE al.id IN (" + ranked + ") ORDER BY " + AlbumRank;
        command.Add("$prefix", Sql.StartsWith(terms.Text));
        command.Add("$n", limit + 1);

        var rows = new List<AlbumDto>(limit + 1);
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(SqliteAlbumRepository.Read(reader));
            }
        }

        return Cut(rows, limit);
    }

    private static async Task<(IReadOnlyList<ArtistDto> Rows, bool More)> ArtistsAsync(SqliteConnection connection, SearchTerms terms, int limit, CancellationToken ct)
    {
        if (limit <= 0)
        {
            return ([], false);
        }

        await using SqliteCommand command = Sql.Command(connection, string.Empty);
        var ranked = new StringBuilder("SELECT a.id");
        if (terms.UsesFts)
        {
            // Artists credited on, or fronting the album of, a present track whose artists or album-artist
            // column matched; the name filter below keeps a co-credit from leaking. Presence is implied by
            // the join, which matters: the Artists list's own presence test walks the album table per artist.
            const string matched = "(SELECT rowid FROM track_fts WHERE track_fts MATCH $match LIMIT " + GroupCandidatesText + ") f JOIN track t ON t.id = f.rowid";
            ranked.Append(" FROM artist a WHERE a.id IN (SELECT ta.artist_id FROM ").Append(matched).Append(" JOIN track_artist ta ON ta.track_id = t.id WHERE t.missing = 0")
                  .Append(" UNION SELECT al.album_artist_id FROM ").Append(matched).Append(" JOIN album al ON al.id = t.album_id WHERE t.missing = 0 AND al.album_artist_id IS NOT NULL)");
            command.Add("$match", terms.Match("artists album_artist"));
        }
        else
        {
            ranked.Append(SqliteArtistRepository.From);
        }

        AppendTermFilters(ranked, command, terms.All, ArtistTermPredicate);
        ranked.Append(" ORDER BY ").Append(ArtistRank).Append(" LIMIT $n");
        command.CommandText = SqliteArtistRepository.Select + SqliteArtistRepository.From + " AND a.id IN (" + ranked + ") ORDER BY " + ArtistRank;
        command.Add("$prefix", Sql.StartsWith(terms.Text));
        command.Add("$n", limit + 1);

        var rows = new List<ArtistDto>(limit + 1);
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(SqliteArtistRepository.Read(reader));
            }
        }

        return Cut(rows, limit);
    }

    /// <summary>One <c>LIKE</c> predicate per term, each bound to its own <c>$s&lt;i&gt;</c> parameter.</summary>
    private static void AppendTermFilters(StringBuilder sql, SqliteCommand command, IReadOnlyList<string> terms, Func<string, string> predicate)
    {
        for (int i = 0; i < terms.Count; i++)
        {
            string name = "$s" + i.ToString(CultureInfo.InvariantCulture);
            sql.Append(" AND ").Append(predicate(name));
            command.Add(name, Sql.Like(terms[i]));
        }
    }

    private static string TrackTermPredicate(string p) =>
        "(t.title LIKE " + p + Escape + " OR al.title LIKE " + p + Escape + " OR aa.name LIKE " + p + Escape
        + " OR EXISTS (SELECT 1 FROM track_artist ft JOIN artist fta ON fta.id = ft.artist_id WHERE ft.track_id = t.id AND fta.name LIKE " + p + Escape + "))";

    private static string AlbumTermPredicate(string p) => "(al.title LIKE " + p + Escape + " OR aa.name LIKE " + p + Escape + ")";

    private static string ArtistTermPredicate(string p) => "a.name LIKE " + p + Escape;

    private static (IReadOnlyList<T> Rows, bool More) Cut<T>(List<T> rows, int limit)
    {
        if (rows.Count <= limit)
        {
            return (rows, false);
        }

        rows.RemoveAt(rows.Count - 1);
        return (rows, true);
    }
}

/// <summary>A search text split into the terms the trigram index can match and the ones only <c>LIKE</c> can.</summary>
internal sealed class SearchTerms
{
    /// <summary>The trigram tokenizer answers nothing for a query phrase shorter than three characters.</summary>
    public const int MinFtsLength = 3;

    private SearchTerms(string text, IReadOnlyList<string> all, IReadOnlyList<string> longTerms, IReadOnlyList<string> shortTerms)
    {
        Text = text;
        All = all;
        Long = longTerms;
        Short = shortTerms;
    }

    /// <summary>The terms joined by single spaces: what the ranking's "starts with" compares against.</summary>
    public string Text { get; }

    public IReadOnlyList<string> All { get; }

    /// <summary>Terms of <see cref="MinFtsLength"/> or more characters (code points): the FTS phrases.</summary>
    public IReadOnlyList<string> Long { get; }

    /// <summary>Shorter terms, applied as <c>LIKE</c> filters beside the FTS match or on their own.</summary>
    public IReadOnlyList<string> Short { get; }

    public bool IsEmpty => All.Count == 0;

    public bool UsesFts => Long.Count > 0;

    public static SearchTerms Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string[] all = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var longTerms = new List<string>();
        var shortTerms = new List<string>();
        foreach (string term in all)
        {
            (term.EnumerateRunes().Count() >= MinFtsLength ? longTerms : shortTerms).Add(term);
        }

        return new SearchTerms(string.Join(' ', all), all, longTerms, shortTerms);
    }

    /// <summary>
    /// The FTS5 query: every long term as a quoted phrase (a quote doubled), implicitly AND-ed, optionally
    /// restricted to the given columns (<c>{album album_artist}: (...)</c>). Nothing else the user typed reaches
    /// the FTS parser, so its operators (<c>AND</c>, <c>NOT</c>, <c>*</c>, <c>^</c>, parentheses) are literal text.
    /// </summary>
    public string Match(string? columns = null)
    {
        var query = new StringBuilder();
        if (columns is not null)
        {
            query.Append('{').Append(columns).Append("}: ("); // a colspec binds only the next phrase; the parentheses make it cover every term
        }

        for (int i = 0; i < Long.Count; i++)
        {
            if (i > 0)
            {
                query.Append(' ');
            }

            query.Append('"').Append(Long[i].Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }

        if (columns is not null)
        {
            query.Append(')');
        }

        return query.ToString();
    }
}
