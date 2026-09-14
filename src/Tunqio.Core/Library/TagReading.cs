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

/// <summary>
/// An embedded picture as found in the tag; the art cache hashes and resizes it, and the tag editor writes one
/// back (T-113). <see cref="Kind"/> is what the tag says the picture is; the scanner and the editor both show the
/// front cover, else the first picture (<see cref="Cover"/>).
/// </summary>
public sealed record EmbeddedPicture(ReadOnlyMemory<byte> Bytes, string? MimeType, PictureKind Kind = PictureKind.FrontCover)
{
    /// <summary>The picture Tunqio shows for a file: its front cover, else its first picture, else <c>null</c>.</summary>
    public static EmbeddedPicture? Cover(IReadOnlyList<EmbeddedPicture>? pictures) =>
        pictures is null || pictures.Count == 0 ? null : pictures.FirstOrDefault(p => p.Kind == PictureKind.FrontCover) ?? pictures[0];

    /// <summary>Same kind and the same bytes; the MIME type is what the container was told and is not compared.</summary>
    public bool SameAs(EmbeddedPicture other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Kind == other.Kind && Bytes.Span.SequenceEqual(other.Bytes.Span);
    }

    /// <summary>The same pictures in the same order (<see cref="SameAs"/>); null and empty are the same absence.</summary>
    public static bool SameSet(IReadOnlyList<EmbeddedPicture>? a, IReadOnlyList<EmbeddedPicture>? b)
    {
        IReadOnlyList<EmbeddedPicture> left = a ?? [];
        IReadOnlyList<EmbeddedPicture> right = b ?? [];
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!left[i].SameAs(right[i]))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// What a picture is for, as the ID3v2 APIC type code (which FLAC's METADATA_BLOCK_PICTURE and every tagging
/// library reuse), so a file's pictures can be read and written back exactly as they were.
/// </summary>
public enum PictureKind
{
    Other = 0,
    FileIcon = 1,
    OtherFileIcon = 2,
    FrontCover = 3,
    BackCover = 4,
    LeafletPage = 5,
    Media = 6,
    LeadArtist = 7,
    Artist = 8,
    Conductor = 9,
    Band = 10,
    Composer = 11,
    Lyricist = 12,
    RecordingLocation = 13,
    DuringRecording = 14,
    DuringPerformance = 15,
    MovieScreenCapture = 16,
    ColouredFish = 17,
    Illustration = 18,
    BandLogo = 19,
    PublisherLogo = 20,
}

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
