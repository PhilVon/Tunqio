namespace Tunqio.Core.Library;

/// <summary>
/// Reads one audio file's tags and audio properties into a <see cref="ScannedTrack"/> (E3-S4; the scanner's
/// ReadTags stage, docs/library-and-data.md "Scanner"). A reader never throws for a bad file: every failure
/// comes back as a <see cref="TagReadResult"/> whose track carries file-name metadata so the file still plays.
/// Only cancellation propagates.
/// </summary>
public interface ITagReader
{
    /// <param name="path">Absolute path of the file.</param>
    /// <param name="folderId">The <c>library_folder</c> the file was found under.</param>
    Task<TagReadResult> ReadAsync(string path, long folderId, CancellationToken ct = default);
}

/// <summary>How a file's metadata was obtained.</summary>
public enum TagReadOutcome
{
    /// <summary>Tags and properties came from the file.</summary>
    Read,

    /// <summary>The file opened but carried no usable tag; title and numbers come from the file name.</summary>
    NoTags,

    /// <summary>The tag is damaged; metadata comes from the file name.</summary>
    CorruptTags,

    /// <summary>The container is one the tag library cannot parse; metadata comes from the file name.</summary>
    Unsupported,

    /// <summary>The read did not finish inside <see cref="TagReaderOptions.Timeout"/> and was abandoned.</summary>
    TimedOut,

    /// <summary>Any other failure (I/O error, a bug in the tag library); metadata comes from the file name.</summary>
    Failed,
}

/// <summary>
/// What <see cref="ITagReader.ReadAsync"/> returns. <see cref="Track"/> is always present; <see cref="Outcome"/>
/// says whether it came from tags or from the file name, and <see cref="Error"/> carries the reason for the
/// scan report when it did not.
/// </summary>
/// <param name="Picture">The embedded front cover (or first picture) for the ExtractArt stage, so it need not reopen the file.</param>
public sealed record TagReadResult(ScannedTrack Track, TagReadOutcome Outcome, string? Error = null, EmbeddedPicture? Picture = null)
{
    /// <summary>True when the title (and numbers) were derived from the file name rather than a tag.</summary>
    public bool UsedFileNameMetadata => Outcome != TagReadOutcome.Read;

    /// <summary>True for outcomes the scan report lists as failures (an untagged file is not one).</summary>
    public bool IsFailure => Outcome is not (TagReadOutcome.Read or TagReadOutcome.NoTags);
}

/// <summary>An embedded picture as found in the tag; the art cache hashes and resizes it.</summary>
public sealed record EmbeddedPicture(ReadOnlyMemory<byte> Bytes, string? MimeType);

/// <summary>Reader behaviour (docs/library-and-data.md "Artist splitting"; timeout from the "ReadTags" stage).</summary>
/// <param name="SplitArtists">
/// Split a single artist value on <c>;</c>, <c>/</c>, <c>,</c>, <c>feat.</c> and <c>ft.</c> when the file has no
/// multi-value artist tag (<see cref="SettingsKeys.LibrarySplitArtists"/>).
/// </param>
/// <param name="Timeout">Per-file budget after which the read is abandoned and file-name metadata used; <c>null</c> means <see cref="DefaultTimeout"/>.</param>
public sealed record TagReaderOptions(bool SplitArtists = SettingsKeys.Defaults.LibrarySplitArtists, TimeSpan? Timeout = null)
{
    /// <summary>The documented per-file budget.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public TimeSpan EffectiveTimeout => Timeout ?? DefaultTimeout;
}
