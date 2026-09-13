namespace Tunqio.Core.Library;

/// <summary>An artist credit on a track, in credit order.</summary>
public sealed record ArtistRef(long Id, string Name);

/// <summary>
/// One row of the Tracks view and the unit the play queue resolves ids to. Never a live database object
/// (docs/library-and-data.md, "Repository layer"). Timestamps are Unix milliseconds UTC as stored.
/// </summary>
public sealed record TrackDto(
    long Id,
    long FolderId,
    string Path,
    string Title,
    IReadOnlyList<ArtistRef> Artists,
    long? AlbumId,
    string? AlbumTitle,
    string? AlbumArtist,
    int? TrackNo,
    int? DiscNo,
    int? Year,
    int DurationMs,
    string Codec,
    int? BitrateKbps,
    int? SampleRate,
    int? Channels,
    int? BitDepth,
    long FileSize,
    long FileMtime,
    string? Composer,
    string? Comment,
    ReplayGainTags? ReplayGain,
    string? ArtHash,
    string? Mbid,
    long AddedAt,
    int? Rating,
    int PlayCount,
    long? LastPlayedAt,
    bool Missing)
{
    /// <summary>Credited artists joined for display ("A, B").</summary>
    public string ArtistNames => string.Join(", ", Artists.Select(a => a.Name));

    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);
}

/// <summary>ReplayGain values as tagged (dB gain, linear peak); album values are optional.</summary>
public sealed record ReplayGainTags(double? TrackGainDb, double? TrackPeak, double? AlbumGainDb, double? AlbumPeak);

/// <summary>An Albums-grid tile and the header of the album detail page.</summary>
public sealed record AlbumDto(
    long Id,
    string Title,
    long? AlbumArtistId,
    string? AlbumArtist,
    int? Year,
    int? DiscCount,
    string? ArtHash,
    int TrackCount,
    long TotalDurationMs,
    long AddedAt,
    long? LastPlayedAt);

/// <summary>
/// What the Albums grid's chips can filter by: the decades (1990, 2000, ...) of albums with present tracks and
/// the codecs (<see cref="AudioFormats"/> vocabulary) of present tracks, both ascending.
/// </summary>
public sealed record AlbumFacets(IReadOnlyList<int> Decades, IReadOnlyList<string> Codecs)
{
    public static AlbumFacets Empty { get; } = new([], []);
}

/// <summary>Album detail: tracks in disc/track order (the UI groups by <see cref="TrackDto.DiscNo"/>).</summary>
public sealed record AlbumDetailDto(AlbumDto Album, IReadOnlyList<TrackDto> Tracks, IReadOnlyList<string> Genres);

/// <summary>A row of the Playlists list (E6-S1). Times are Unix milliseconds UTC; the totals count every item, a track twice counting twice.</summary>
public sealed record PlaylistDto(
    long Id,
    string Name,
    long CreatedAt,
    long ModifiedAt,
    bool Pinned,
    int TrackCount,
    long TotalDurationMs);

/// <summary>A playlist and its items in playlist order; an item's position is its index in <see cref="Tracks"/>.</summary>
public sealed record PlaylistDetailDto(PlaylistDto Playlist, IReadOnlyList<TrackDto> Tracks);

/// <summary>An Artists-list row.</summary>
public sealed record ArtistDto(long Id, string Name, string SortName, string? Mbid, int AlbumCount, int TrackCount, string? ArtHash);

/// <summary>Artist detail: albums where they are the album artist, and albums they merely appear on.</summary>
public sealed record ArtistDetailDto(ArtistDto Artist, IReadOnlyList<AlbumDto> Albums, IReadOnlyList<AlbumDto> AppearsOn);

/// <summary>A tag-cloud entry sized by <see cref="TrackCount"/>.</summary>
public sealed record GenreDto(long Id, string Name, int TrackCount);

/// <summary>A watched library folder (docs/library-and-data.md, <c>library_folder</c>).</summary>
public sealed record LibraryFolderDto(long Id, string Path, bool Enabled, long? LastScanAt, string? LastScanStatus);
