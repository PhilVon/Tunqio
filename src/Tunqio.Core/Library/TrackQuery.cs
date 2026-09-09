namespace Tunqio.Core.Library;

/// <summary>Sort orders of the Tracks view (docs/ui-screens-and-flows.md: "Sort by any column").</summary>
public enum TrackSort
{
    /// <summary>Title, case-insensitive.</summary>
    Title,

    /// <summary>First credited artist, then album order.</summary>
    Artist,

    /// <summary>Album title, then disc and track number.</summary>
    Album,

    Duration,
    Year,

    /// <summary>When the track entered the library (the "Recently added" view, descending).</summary>
    Added,

    /// <summary>The "Recently played" view (descending; never-played tracks last when ascending).</summary>
    LastPlayed,

    /// <summary>The "Most played" view (descending), ties broken by most recent play.</summary>
    PlayCount,

    Rating,
    Codec,
}

/// <summary>
/// Keyset cursor: the sort keys and id of the last row of the previous page (docs/library-and-data.md:
/// <c>WHERE (sort_key, id) &gt; (?, ?) LIMIT n</c>). Obtain one with <see cref="TrackQuery.CursorAfter"/> or
/// <see cref="AlbumQuery.CursorAfter"/>. Keys are <see cref="string"/> or <see cref="long"/> values, one per
/// sort column, with nulls already replaced by the sentinel the SQL uses (<see cref="SortKeys"/>).
/// </summary>
public sealed record PageCursor(IReadOnlyList<object> Keys, long Id);

/// <summary>
/// Sort-key conventions shared by the query builder (SQL side) and the tests' oracle (LINQ side): every key
/// column is coalesced so a null never breaks a row-value comparison, and text keys compare case-insensitively
/// in SQLite's NOCASE sense (ASCII case folding only, then byte order).
/// </summary>
public static class SortKeys
{
    /// <summary>Sorts after every real string (U+FFFF); used for absent album titles and artist names.</summary>
    public const string NoText = "￿";

    /// <summary>Sorts after every real timestamp; used for never-played tracks.</summary>
    public const long NoTime = long.MaxValue;

    /// <summary>Sorts after every real year.</summary>
    public const long NoYear = int.MaxValue;

    /// <summary>Sorts before every real rating (0..100).</summary>
    public const long NoRating = -1;

    /// <summary>SQLite's NOCASE collation: fold A-Z only, then compare bytes (ordinal on UTF-16 is equivalent for BMP text).</summary>
    public static readonly StringComparer NoCase = new NoCaseComparer();

    private sealed class NoCaseComparer : StringComparer
    {
        public override int Compare(string? x, string? y) => string.CompareOrdinal(Fold(x), Fold(y));

        public override bool Equals(string? x, string? y) => Compare(x, y) == 0;

        public override int GetHashCode(string obj) => Fold(obj)!.GetHashCode(StringComparison.Ordinal);

        private static string? Fold(string? s)
        {
            if (s is null)
            {
                return null;
            }

            return string.Create(s.Length, s, static (span, source) =>
            {
                for (int i = 0; i < source.Length; i++)
                {
                    char c = source[i];
                    span[i] = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
                }
            });
        }
    }
}

/// <summary>
/// A plain description of a Tracks list: one sort, optional filters, and a keyset page. Translated to SQL by the
/// library's query builder; every filter and sort combination is exercised by <c>TrackRepositoryTests</c>.
/// </summary>
/// <param name="Sort">Column to order by.</param>
/// <param name="Descending">Reverse the whole order (ties still break on id, in the same direction).</param>
/// <param name="AlbumId">Only tracks of this album.</param>
/// <param name="ArtistId">Only tracks crediting this artist in any role.</param>
/// <param name="GenreId">Only tracks tagged with this genre.</param>
/// <param name="FolderId">Only tracks under this library folder.</param>
/// <param name="Text">Case-insensitive substring of title, album title or any credited artist (chip filters; search proper is FTS).</param>
/// <param name="IncludeMissing">Include tracks whose file was absent at the last scan (hidden by default).</param>
/// <param name="PlayedOnly">Only tracks with a completed play. The Recently and Most played views need it: a never-played track's last-played key is the "after everything" sentinel, so it would lead a descending list.</param>
/// <param name="After">Resume after this row (keyset paging); <c>null</c> starts at the top.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="Take">Hard cap on rows across all pages (the Recent/Most played views use 500); <c>null</c> for no cap.</param>
public sealed record TrackQuery(
    TrackSort Sort = TrackSort.Title,
    bool Descending = false,
    long? AlbumId = null,
    long? ArtistId = null,
    long? GenreId = null,
    long? FolderId = null,
    string? Text = null,
    bool IncludeMissing = false,
    bool PlayedOnly = false,
    PageCursor? After = null,
    int PageSize = 200,
    int? Take = null)
{
    /// <summary>The cursor that continues this query after <paramref name="last"/>.</summary>
    public PageCursor CursorAfter(TrackDto last)
    {
        ArgumentNullException.ThrowIfNull(last);
        return new PageCursor(SortKeysOf(last), last.Id);
    }

    /// <summary>
    /// The values the query builder orders by for <see cref="Sort"/>, computed from a row exactly as the SQL
    /// does (see <see cref="SortKeys"/>). Shared with the tests' oracle so both sides agree by construction.
    /// </summary>
    public object[] SortKeysOf(TrackDto track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return Sort switch
        {
            TrackSort.Title => [track.Title],
            TrackSort.Artist => [track.Artists.Count == 0 ? SortKeys.NoText : track.Artists[0].Name, track.AlbumTitle ?? SortKeys.NoText, (long)(track.DiscNo ?? 0), (long)(track.TrackNo ?? 0)],
            TrackSort.Album => [track.AlbumTitle ?? SortKeys.NoText, (long)(track.DiscNo ?? 0), (long)(track.TrackNo ?? 0)],
            TrackSort.Duration => [(long)track.DurationMs],
            TrackSort.Year => [track.Year ?? SortKeys.NoYear],
            TrackSort.Added => [track.AddedAt],
            TrackSort.LastPlayed => [track.LastPlayedAt ?? SortKeys.NoTime],
            TrackSort.PlayCount => [(long)track.PlayCount, track.LastPlayedAt ?? SortKeys.NoTime],
            TrackSort.Rating => [track.Rating ?? SortKeys.NoRating],
            TrackSort.Codec => [track.Codec],
            _ => throw new InvalidOperationException($"unknown sort {Sort}"),
        };
    }
}

/// <summary>Sort orders of the Albums grid (docs/ui-screens-and-flows.md: title, artist, year, added, played).</summary>
public enum AlbumSort
{
    Title,
    Artist,
    Year,
    Added,
    Played,
}

/// <summary>Albums grid query: sort, chip filters and a keyset page.</summary>
/// <param name="ArtistId">Albums by this album artist.</param>
/// <param name="GenreId">Albums with at least one track in this genre.</param>
/// <param name="Decade">Albums whose year falls in this decade (e.g. 1990).</param>
/// <param name="Codec">Albums with at least one track in this format ("flac", "mp3", ...).</param>
/// <param name="Text">Case-insensitive substring of title or album artist.</param>
public sealed record AlbumQuery(
    AlbumSort Sort = AlbumSort.Title,
    bool Descending = false,
    long? ArtistId = null,
    long? GenreId = null,
    int? Decade = null,
    string? Codec = null,
    string? Text = null,
    PageCursor? After = null,
    int PageSize = 100)
{
    public PageCursor CursorAfter(AlbumDto last)
    {
        ArgumentNullException.ThrowIfNull(last);
        return new PageCursor(SortKeysOf(last), last.Id);
    }

    public object[] SortKeysOf(AlbumDto album)
    {
        ArgumentNullException.ThrowIfNull(album);
        return Sort switch
        {
            AlbumSort.Title => [album.Title],
            AlbumSort.Artist => [album.AlbumArtist ?? SortKeys.NoText, album.Title],
            AlbumSort.Year => [album.Year ?? SortKeys.NoYear, album.Title],
            AlbumSort.Added => [album.AddedAt],
            AlbumSort.Played => [album.LastPlayedAt ?? SortKeys.NoTime],
            _ => throw new InvalidOperationException($"unknown sort {Sort}"),
        };
    }
}

/// <summary>Artists list query: optional name filter and a keyset page ordered by sort name.</summary>
public sealed record ArtistQuery(string? Text = null, PageCursor? After = null, int PageSize = 200)
{
    public static PageCursor CursorAfter(ArtistDto last)
    {
        ArgumentNullException.ThrowIfNull(last);
        return new PageCursor([last.SortName], last.Id);
    }
}
