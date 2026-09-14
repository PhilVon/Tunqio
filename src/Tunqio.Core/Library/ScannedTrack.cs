namespace Tunqio.Core.Library;

/// <summary>
/// What the tag reader produces for one file and what <see cref="ITrackRepository.UpsertBatchAsync"/> consumes.
/// Names are already normalised by the reader (artist splitting, compilation detection, folder pseudo-albums:
/// docs/library-and-data.md "Artist splitting" and "Album identity"); the repository resolves them to rows.
/// </summary>
/// <param name="Path">Absolute path; the upsert key.</param>
/// <param name="FolderId">The <c>library_folder</c> the file was found under.</param>
/// <param name="AlbumArtist">Album artist tag; <c>null</c> falls back to the first of <paramref name="Artists"/>.</param>
/// <param name="AlbumTitle"><c>null</c> leaves the track without an album (the reader normally substitutes the folder name).</param>
public sealed record ScannedTrack(
    string Path,
    long FolderId,
    long FileSize,
    long FileMtime,
    string Codec,
    int DurationMs,
    string Title,
    IReadOnlyList<string> Artists,
    string? AlbumTitle = null,
    string? AlbumArtist = null,
    int? Year = null,
    int? TrackNo = null,
    int? DiscNo = null,
    int? DiscCount = null,
    IReadOnlyList<string>? Genres = null,
    string? Composer = null,
    string? Comment = null,
    int? BitrateKbps = null,
    int? SampleRate = null,
    int? Channels = null,
    int? BitDepth = null,
    ReplayGainTags? ReplayGain = null,
    string? AlbumArtHash = null,
    string? TrackArtHash = null,
    string? Mbid = null,
    string? AlbumMbid = null);

/// <summary>
/// A partial tag edit: every property left <c>null</c> is unchanged. Written to the file by
/// <see cref="ITagWriter"/>; the database follows by a targeted rescan of the written paths (<see cref="ITagEditor"/>,
/// E3-S10), which is the one write path for tags. <see cref="Rating"/> is user data that lives in the database only.
/// </summary>
public sealed record TagEdit(
    string? Title = null,
    IReadOnlyList<string>? Artists = null,
    string? AlbumTitle = null,
    string? AlbumArtist = null,
    int? Year = null,
    int? TrackNo = null,
    int? DiscNo = null,
    IReadOnlyList<string>? Genres = null,
    string? Composer = null,
    string? Comment = null,
    int? Rating = null)
{
    public bool IsEmpty => Title is null && Artists is null && AlbumTitle is null && AlbumArtist is null && Year is null
        && TrackNo is null && DiscNo is null && Genres is null && Composer is null && Comment is null && Rating is null;
}
