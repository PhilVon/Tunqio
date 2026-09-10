using System.Collections.Frozen;

namespace Tunqio.Core.Library;

/// <summary>
/// The supported file extensions (docs/product-scope.md "Formats") and the codec identifiers stored in
/// <c>track.codec</c>. The identifiers are the vocabulary the UI shows and filters on:
/// <c>mp3 flac aac alac vorbis opus wav aiff wma wavpack ape mpc</c>. An extension only implies a container, so
/// the tag reader refines <c>.m4a</c> into <c>aac</c>/<c>alac</c> and <c>.ogg</c> into <c>vorbis</c>/<c>opus</c>
/// from the stream itself; <see cref="CodecForExtension"/> is the fallback when the file cannot be parsed.
/// </summary>
public static class AudioFormats
{
    private static readonly FrozenDictionary<string, string> CodecByExtension = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "mp3",
        [".flac"] = "flac",
        [".m4a"] = "aac",
        [".mp4"] = "aac",
        [".aac"] = "aac",
        [".ogg"] = "vorbis",
        [".oga"] = "vorbis",
        [".opus"] = "opus",
        [".wav"] = "wav",
        [".aif"] = "aiff",
        [".aiff"] = "aiff",
        [".aifc"] = "aiff",
        [".wma"] = "wma",
        [".wv"] = "wavpack",
        [".ape"] = "ape",
        [".mpc"] = "mpc",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The codec identifiers that carry the samples intact. It is a property of the codec and not of the
    /// container, which is why it is keyed on the refined identifier: <c>.m4a</c> is <c>alac</c> or <c>aac</c>
    /// and the extension cannot say which.
    /// </summary>
    private static readonly FrozenSet<string> LosslessCodecs =
        new[] { "flac", "alac", "wav", "aiff", "wavpack", "ape" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Extensions the scanner enumerates, lower case with the leading dot.</summary>
    public static IReadOnlyCollection<string> Extensions => CodecByExtension.Keys;

    /// <summary>
    /// True for a codec that stores the samples exactly. What it is for is display — a lossless format is read
    /// as depth and rate ("FLAC 24/96") and a lossy one as a bit rate ("MP3 320 kbps"), because a bit depth is
    /// not a fact about a lossy stream and a bit rate says nothing useful about a lossless one.
    /// </summary>
    public static bool IsLossless(string? codec) => codec is not null && LosslessCodecs.Contains(codec);

    /// <summary>True when the path's extension is in <see cref="Extensions"/>.</summary>
    public static bool IsSupported(string path) => CodecByExtension.ContainsKey(Path.GetExtension(path));

    /// <summary>Span form for the scanner's directory walk, which sees file names without allocating them.</summary>
    public static bool IsSupported(ReadOnlySpan<char> path)
    {
        ReadOnlySpan<char> extension = Path.GetExtension(path);
        return extension.Length > 1 && CodecByExtension.ContainsKey(extension.ToString());
    }

    /// <summary>The codec an extension implies, or <c>null</c> for an unsupported extension.</summary>
    public static string? CodecForExtension(string pathOrExtension) =>
        CodecByExtension.TryGetValue(Path.GetExtension(pathOrExtension), out string? codec) ? codec : null; // GetExtension(".mp3") is ".mp3"
}
