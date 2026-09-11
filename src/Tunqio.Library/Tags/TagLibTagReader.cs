using Microsoft.Extensions.Logging;
using TagLib;
using Tunqio.Core;
using Tunqio.Core.Library;
using TagFile = TagLib.File;

namespace Tunqio.Library.Tags;

/// <summary>
/// <see cref="ITagReader"/> over TagLibSharp (ADR-005). Each read runs on the thread pool and is abandoned
/// after <see cref="TagReaderOptions.Timeout"/>: TagLibSharp is synchronous and cannot be interrupted, so a
/// file that hangs the parser costs one pool thread until the parser gives up, not the whole scan. Every
/// exception from the parser (corrupt, unsupported, I/O, or a plain bug thrown from a property getter) is
/// caught inside the pooled task and turned into a <see cref="TagReadOutcome"/>; the file is then recorded
/// from its name and extension (<see cref="FileNameMetadata"/>, <see cref="AudioFormats.CodecForExtension"/>).
/// <para>
/// The compilation rule is not applied here because it needs the whole folder: see <see cref="CompilationRule"/>.
/// Gapless data (encoder delay and padding) is not extracted: nothing in the schema stores it, and the native
/// core reads it from the stream when it opens the file.
/// </para>
/// </summary>
public sealed class TagLibTagReader : ITagReader
{
    private readonly Func<TagReaderOptions> _options;
    private readonly Func<string, TagFile> _open;
    private readonly ILogger _logger;

    /// <summary>Reads the splitting toggle from settings at every call so a changed setting applies to the next scan.</summary>
    public TagLibTagReader(ISettingsStore settings, ILogger<TagLibTagReader>? logger = null)
        : this(() => new TagReaderOptions(SplitArtists: settings.GetValue(SettingsKeys.LibrarySplitArtists, SettingsKeys.Defaults.LibrarySplitArtists)), logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
    }

    public TagLibTagReader(TagReaderOptions options, ILogger? logger = null)
        : this(() => options, logger)
    {
        ArgumentNullException.ThrowIfNull(options);
    }

    internal TagLibTagReader(Func<TagReaderOptions> options, ILogger? logger, Func<string, TagFile>? open = null)
    {
        _options = options;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        _open = open ?? (path => TagFile.Create(path, ReadStyle.Average | ReadStyle.PictureLazy));
    }

    public async Task<TagReadResult> ReadAsync(string path, long folderId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ct.ThrowIfCancellationRequested();
        TagReaderOptions options = _options();

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return Fallback(path, folderId, 0, 0, TagReadOutcome.Failed, "file not found");
        }

        long size = info.Length;
        long mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();

        Task<TagReadResult> work = Task.Run(() => ReadIsolated(path, folderId, size, mtime, options), ct);
        try
        {
            return await work.WaitAsync(options.EffectiveTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The parser is still running; let it finish on its own and observe whatever it throws.
            _ = work.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            _logger.LogWarning("Tag read of {Path} exceeded {Timeout} and was abandoned", path, options.EffectiveTimeout);
            return Fallback(path, folderId, size, mtime, TagReadOutcome.TimedOut, $"tag read exceeded {options.EffectiveTimeout.TotalSeconds:0.#} s");
        }
    }

    /// <summary>Runs on the pool: everything that touches TagLibSharp happens here, inside one try.</summary>
    private TagReadResult ReadIsolated(string path, long folderId, long size, long mtime, TagReaderOptions options)
    {
        try
        {
            if (HasDamagedId3v2Header(path))
            {
                // TagLibSharp does not report this case: it finds an empty tag and then mis-locates the first
                // audio frame, so its audio properties are wrong too. Leave the duration for the scanner's slow path.
                _logger.LogWarning("Tag read of {Path}: damaged ID3v2 header; using file-name metadata", path);
                return Fallback(path, folderId, size, mtime, TagReadOutcome.CorruptTags, "damaged ID3v2 header");
            }

            using TagFile file = _open(path);
            return Convert(file, path, folderId, size, mtime, options);
        }
        catch (CorruptFileException e)
        {
            return Failed(path, folderId, size, mtime, TagReadOutcome.CorruptTags, e);
        }
        catch (UnsupportedFormatException e)
        {
            return Failed(path, folderId, size, mtime, TagReadOutcome.Unsupported, e);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Failed(path, folderId, size, mtime, TagReadOutcome.Failed, e);
        }
    }

    private TagReadResult Failed(string path, long folderId, long size, long mtime, TagReadOutcome outcome, Exception e)
    {
        _logger.LogWarning(e, "Tag read of {Path} failed ({Outcome}); using file-name metadata", path, outcome);
        return Fallback(path, folderId, size, mtime, outcome, $"{e.GetType().Name}: {e.Message}");
    }

    private static TagReadResult Fallback(string path, long folderId, long size, long mtime, TagReadOutcome outcome, string error)
    {
        FileNameMetadata name = FileNameMetadata.FromPath(path);
        var track = new ScannedTrack(
            Path: path,
            FolderId: folderId,
            FileSize: size,
            FileMtime: mtime,
            Codec: AudioFormats.CodecForExtension(path) ?? Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
            DurationMs: 0,
            Title: name.Title,
            Artists: [],
            AlbumTitle: name.AlbumTitle,
            TrackNo: name.TrackNo,
            DiscNo: name.DiscNo);
        return new TagReadResult(track, outcome, error);
    }

    private static TagReadResult Convert(TagFile file, string path, long folderId, long size, long mtime, TagReaderOptions options)
    {
        Tag tag = file.Tag;
        Properties? properties = file.Properties;
        FileNameMetadata name = FileNameMetadata.FromPath(path);

        string? title = TagValues.Clean(tag.Title);
        bool hasTags = title is not null;
        IReadOnlyList<string> artists = TagValues.Artists(tag.Performers, options.SplitArtists);
        string? albumTitle = TagValues.Clean(tag.Album);
        IReadOnlyList<string> albumArtists = TagValues.Distinct(tag.AlbumArtists);
        string? albumArtist = albumArtists.Count > 0 ? albumArtists[0] : null;
        IReadOnlyList<string> genres = TagValues.Genres(tag.Genres);
        string? composer = TagValues.Distinct(tag.Composers) is { Count: > 0 } composers ? string.Join("; ", composers) : null;

        int? sampleRate = Positive(properties?.AudioSampleRate);
        int? channels = Positive(properties?.AudioChannels);
        int? bitDepth = Positive(properties?.BitsPerSample);
        int durationMs = properties is null ? 0 : (int)Math.Round(properties.Duration.TotalMilliseconds);
        // Opus (and some others) carry no nominal bitrate; the file's average is close enough for a column.
        int? bitrate = Positive(properties?.AudioBitrate) ?? (durationMs > 0 ? Positive((int)Math.Min(size * 8 / durationMs, int.MaxValue)) : null);

        ReplayGainTags? replayGain = null;
        double? trackGain = Finite(tag.ReplayGainTrackGain);
        double? albumGain = Finite(tag.ReplayGainAlbumGain);
        if (trackGain is not null || albumGain is not null)
        {
            replayGain = new ReplayGainTags(trackGain, Finite(tag.ReplayGainTrackPeak), albumGain, Finite(tag.ReplayGainAlbumPeak));
        }

        var track = new ScannedTrack(
            Path: path,
            FolderId: folderId,
            FileSize: size,
            FileMtime: mtime,
            Codec: Codec(file, properties, path),
            DurationMs: Math.Max(0, durationMs),
            Title: title ?? name.Title,
            Artists: artists,
            AlbumTitle: albumTitle ?? name.AlbumTitle,
            AlbumArtist: albumArtist,
            Year: Positive(tag.Year),
            TrackNo: Positive(tag.Track) ?? (hasTags ? null : name.TrackNo),
            DiscNo: Positive(tag.Disc) ?? name.DiscNo,
            DiscCount: Positive(tag.DiscCount),
            Genres: genres.Count > 0 ? genres : null,
            Composer: composer,
            Comment: TagValues.Clean(tag.Comment),
            BitrateKbps: bitrate,
            SampleRate: sampleRate,
            Channels: channels,
            BitDepth: bitDepth,
            ReplayGain: replayGain,
            Mbid: TagValues.Clean(tag.MusicBrainzTrackId),
            AlbumMbid: TagValues.Clean(tag.MusicBrainzReleaseId));

        return new TagReadResult(track, hasTags ? TagReadOutcome.Read : TagReadOutcome.NoTags, hasTags ? null : "no title tag", Picture(tag));
    }

    /// <summary>Front cover preferred, else the first picture; only that one's bytes are pulled from the lazy tag.</summary>
    private static EmbeddedPicture? Picture(Tag tag)
    {
        IPicture[] pictures = tag.Pictures;
        if (pictures is null || pictures.Length == 0)
        {
            return null;
        }

        IPicture chosen = pictures.FirstOrDefault(p => p.Type == PictureType.FrontCover) ?? pictures[0];
        byte[]? data = chosen.Data?.Data;
        return data is null || data.Length == 0 ? null : new EmbeddedPicture(data, TagValues.Clean(chosen.MimeType));
    }

    /// <summary>
    /// The container decides for most formats; MP4 and Ogg can hold several codecs, so their stream entry is
    /// inspected. ASF (WMA) describes its stream with the same WAVEFORMATEX type as RIFF, which is why the file
    /// type is checked before the codec list.
    /// </summary>
    private static string Codec(TagFile file, Properties? properties, string path)
    {
        switch (file)
        {
            case TagLib.Mpeg.AudioFile:
                return "mp3";
            case TagLib.Flac.File:
                return "flac";
            case TagLib.Riff.File:
                return "wav";
            case TagLib.Aiff.File:
                return "aiff";
            case TagLib.Asf.File:
                return "wma";
            case TagLib.WavPack.File:
                return "wavpack";
            case TagLib.Ape.File:
                return "ape";
            case TagLib.Mpeg4.File:
                return properties?.Codecs.OfType<TagLib.Mpeg4.IsoAudioSampleEntry>().Any(e => string.Equals(e.BoxType.ToString(), "alac", StringComparison.OrdinalIgnoreCase)) == true ? "alac" : "aac";
            case TagLib.Ogg.File:
                foreach (ICodec codec in properties?.Codecs ?? [])
                {
                    switch (codec)
                    {
                        case TagLib.Ogg.Codecs.Opus:
                            return "opus";
                        case TagLib.Ogg.Codecs.Vorbis:
                            return "vorbis";
                        case TagLib.Flac.StreamHeader:
                            return "flac";
                        default:
                            continue;
                    }
                }

                break;
            default:
                break;
        }

        return AudioFormats.CodecForExtension(path) ?? Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
    }

    /// <summary>
    /// An "ID3" marker followed by a header no ID3v2 version could have written: version or revision 0xFF, or a
    /// size byte with its top bit set (the size is sync-safe, seven bits per byte).
    /// </summary>
    private static bool HasDamagedId3v2Header(string path)
    {
        Span<byte> header = stackalloc byte[10];
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 16);
        if (stream.Read(header) < header.Length || !header[..3].SequenceEqual("ID3"u8))
        {
            return false;
        }

        return header[3] == 0xFF || header[4] == 0xFF || header[6] >= 0x80 || header[7] >= 0x80 || header[8] >= 0x80 || header[9] >= 0x80;
    }

    private static int? Positive(int? value) => value > 0 ? value : null;

    private static int? Positive(uint value) => value is > 0 and <= int.MaxValue ? (int)value : null;

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
