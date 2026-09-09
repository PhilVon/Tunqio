namespace Tunqio.Core.Library;

/// <summary>
/// The scanner (E3-S5; docs/library-and-data.md "Scanner"): walks the enabled library folders, reads the tags
/// of new and changed files, writes them through <see cref="ITrackRepository.UpsertBatchAsync"/> in batches of
/// one transaction each, and marks files that have gone <c>missing</c>. One scan runs at a time; a second
/// request while one is running is refused with <see cref="InvalidOperationException"/>. A cancelled scan
/// leaves the database consistent (every batch fully applied or not at all) and comes back as a report with
/// <see cref="ScanOutcome.Cancelled"/> rather than an exception.
/// </summary>
public interface ILibraryScanner
{
    /// <summary>True while a scan is running.</summary>
    bool IsScanning { get; }

    /// <summary>
    /// Scans the requested folders (every enabled folder by default). <paramref name="progress"/> is reported
    /// at most a few times a second plus once at the end with the final counts.
    /// </summary>
    Task<ScanReport> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Raised after every scan, whatever its outcome and whoever asked for it (Settings, launch, the watcher),
    /// with the report; views refresh from here when the report changed anything. Raised on a pool thread.
    /// </summary>
    event EventHandler<ScanReport>? ScanCompleted;
}

/// <summary>What to scan.</summary>
/// <param name="FolderIds">Library folders to scan; <c>null</c> means every enabled folder. A disabled folder is skipped even when named.</param>
/// <param name="ForceReread">Read the tags of every file again instead of only files whose size or modification time changed (Settings > Library > Rescan).</param>
/// <param name="Paths">
/// Only these paths, under the one folder in <paramref name="FolderIds"/> (the watcher's request, E3-S6). A
/// path that is a directory is walked; a file is diffed with its directory (so the compilation rule still sees
/// the folder); a path no longer on disk has its rows marked missing, a whole subtree of them when it was a
/// directory. Nothing outside the paths is touched and the folder's last-scan record is left alone.
/// </param>
public sealed record ScanRequest(IReadOnlyList<long>? FolderIds = null, bool ForceReread = false, IReadOnlyList<string>? Paths = null)
{
    public static ScanRequest All { get; } = new();

    public static ScanRequest Folder(long folderId) => new([folderId]);

    /// <summary>A scan of <paramref name="paths"/> under <paramref name="folderId"/> only.</summary>
    public static ScanRequest Targeted(long folderId, IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new ScanRequest([folderId], Paths: paths);
    }

    /// <summary>True when <see cref="Paths"/> narrows the scan to part of one folder.</summary>
    public bool IsTargeted => Paths is { Count: > 0 };
}

/// <summary>Where a scan is.</summary>
public enum ScanPhase
{
    /// <summary>Walking the folder and comparing against the database.</summary>
    Enumerating,

    /// <summary>Reading tags and writing rows.</summary>
    Reading,

    /// <summary>Flagging files that were not seen.</summary>
    MarkingMissing,

    /// <summary>The report is final.</summary>
    Finished,
}

/// <summary>
/// A progress sample (sidebar footer and Settings > Library). Counts are cumulative across the folders of one
/// scan. <see cref="Processed"/> counts files whose tags were read (or re-read) and includes <see cref="Failed"/>;
/// <see cref="Seen"/> counts every supported file enumerated, unchanged ones included.
/// </summary>
public sealed record ScanProgress(
    ScanPhase Phase,
    int Seen,
    int Processed,
    int Added,
    int Updated,
    int Unchanged,
    int Failed,
    int SlowPath,
    string? CurrentPath);

/// <summary>How a scan ended.</summary>
public enum ScanOutcome
{
    Completed,

    /// <summary>The token was cancelled; batches already committed stay, the rest was never written.</summary>
    Cancelled,

    /// <summary>The scan stopped on an error the report's <see cref="ScanReport.Error"/> describes (a database failure, not a bad file: bad files are <see cref="ScanReport.Failures"/>).</summary>
    Failed,
}

/// <summary>One file the tag reader could not read; it is in the library with file-name metadata.</summary>
public sealed record ScanFailure(string Path, TagReadOutcome Outcome, string? Error);

/// <summary>What happened to one folder.</summary>
/// <param name="Offline">The folder's root did not exist (network share or removable drive away); its tracks were marked missing and nothing was read.</param>
public sealed record ScanFolderReport(long FolderId, string Path, bool Offline, int Seen, int Added, int Updated, int Unchanged, int Failed, int Missing, int Restored);

/// <summary>The outcome of one <see cref="ILibraryScanner.ScanAsync"/>.</summary>
/// <param name="Missing">Tracks newly flagged missing (absent from disk and not flagged before).</param>
/// <param name="Restored">Tracks whose flag was cleared because the file is back unchanged (a changed file is re-read and counted as <see cref="Updated"/> instead).</param>
/// <param name="SlowPath">Files whose duration came from the decode probe because the tag carried none.</param>
public sealed record ScanReport(
    ScanOutcome Outcome,
    TimeSpan Elapsed,
    int Seen,
    int Processed,
    int Added,
    int Updated,
    int Unchanged,
    int Failed,
    int Missing,
    int Restored,
    int SlowPath,
    IReadOnlyList<ScanFailure> Failures,
    IReadOnlyList<ScanFolderReport> Folders,
    string? Error = null);

/// <summary>
/// The scanner's slow path for a file whose tag carries no duration (docs/library-and-data.md, ReadTags stage
/// step 3): opens a decode stream and measures it. The host binds this to the native engine; without a probe
/// the track keeps <c>duration_ms = 0</c> until the engine opens it.
/// </summary>
public interface IDurationProbe
{
    /// <summary>The duration in milliseconds, or <c>null</c> when the file cannot be decoded.</summary>
    Task<int?> ProbeAsync(string path, CancellationToken ct = default);
}

/// <summary>Art hashes for one scanned file, as the art cache assigns them (<c>track.art_hash</c> and <c>album.art_hash</c>).</summary>
public sealed record ArtHashes(string? TrackArtHash, string? AlbumArtHash)
{
    public static ArtHashes None { get; } = new(null, null);
}

/// <summary>
/// The scanner's ExtractArt stage (E3-S7 implements it over the hashed on-disk cache): given the embedded
/// picture the tag reader found, or nothing, and the file's path for the folder-image fallback, stores the
/// image and returns the hashes to record. Must never throw for a bad image: return <see cref="ArtHashes.None"/>.
/// </summary>
public interface IArtCache
{
    Task<ArtHashes> StoreAsync(EmbeddedPicture? picture, string audioPath, CancellationToken ct = default);
}

/// <summary>What the scanner's Diff stage compares a file on disk against: the stamp the row was written with.</summary>
public sealed record TrackFileStamp(long Id, string Path, long FileSize, long FileMtime, bool Missing);
