namespace Tunqio.Core.Library;

/// <summary>How many rows each group of a search returns (docs/ui-screens-and-flows.md, "Search": top 20 tracks, 10 albums, 10 artists). A group with a limit of 0 is not searched.</summary>
public sealed record SearchLimits(int Tracks = 20, int Albums = 10, int Artists = 10)
{
    public static SearchLimits Default { get; } = new();
}

/// <summary>
/// The three result groups for one query, each cut at its limit; the <c>More*</c> flags say whether a larger
/// limit would show more ("show all" per group). Never a live database object.
/// </summary>
public sealed record SearchResults(
    string Text,
    IReadOnlyList<TrackDto> Tracks,
    bool MoreTracks,
    IReadOnlyList<AlbumDto> Albums,
    bool MoreAlbums,
    IReadOnlyList<ArtistDto> Artists,
    bool MoreArtists)
{
    public static SearchResults Empty(string text) => new(text, [], false, [], false, [], false);

    public bool IsEmpty => Tracks.Count == 0 && Albums.Count == 0 && Artists.Count == 0;
}

/// <summary>
/// As-you-type search over the library (docs/library-and-data.md, "FTS maintenance"; E3-S9). Every whitespace
/// separated term of the text must appear, case-insensitively, as a substring: for a track in its title, album,
/// album artist or a credited artist (any term in any of them); for an album in its title or album artist; for an
/// artist in the name. Blank text returns <see cref="SearchResults.Empty"/> without touching the database.
/// </summary>
public interface ISearchService
{
    Task<SearchResults> SearchAsync(string text, SearchLimits limits, CancellationToken ct = default);

    /// <summary>
    /// Settings › Library › Rebuild search index: drops every indexed row and re-derives the index from the track
    /// table, for a database whose index has drifted. Returns the number of tracks indexed.
    /// </summary>
    Task<int> RebuildIndexAsync(CancellationToken ct = default);
}
