using System.Globalization;
using Tunqio.Core.Library;

namespace Tunqio.App.Controls;

/// <summary>Cell formatting for the library views' <c>x:Bind</c> function bindings (allocation-light, culture-invariant).</summary>
public static class Format
{
    private static readonly string[] Stars = ["", "★", "★★", "★★★", "★★★★", "★★★★★"];

    public static string TrackNo(int? trackNo) => trackNo is { } n ? n.ToString(CultureInfo.InvariantCulture) : string.Empty;

    public static string Year(int? year) => year is { } y ? y.ToString(CultureInfo.InvariantCulture) : string.Empty;

    public static string Plays(int playCount) => playCount == 0 ? string.Empty : playCount.ToString(CultureInfo.InvariantCulture);

    /// <summary><c>m:ss</c>, or <c>h:mm:ss</c> from an hour.</summary>
    public static string Duration(int durationMs)
    {
        var t = TimeSpan.FromMilliseconds(durationMs);
        return t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}");
    }

    /// <summary>A 0–100 rating as 0–5 stars.</summary>
    public static string Rating(int? rating) => rating is { } r ? Stars[Math.Clamp((r + 10) / 20, 0, 5)] : string.Empty;

    public static string Upper(string? value) => value?.ToUpperInvariant() ?? string.Empty;

    public static string OrEmpty(string? value) => value ?? string.Empty;

    /// <summary>The tile's automation name (docs/ui-screens-and-flows.md, "Accessibility contract"): "Album X by Y, 2001".</summary>
    public static string AlbumName(string? title, string? artist, int? year)
    {
        string name = "Album " + (title ?? "unknown");
        if (!string.IsNullOrEmpty(artist))
        {
            name += " by " + artist;
        }

        return year is { } y ? name + ", " + y.ToString(CultureInfo.InvariantCulture) : name;
    }

    /// <summary>"1 track", "12 tracks".</summary>
    public static string Tracks(int count) => count == 1 ? "1 track" : count.ToString(CultureInfo.InvariantCulture) + " tracks";

    public static string Albums(int count) => count == 1 ? "1 album" : count.ToString(CultureInfo.InvariantCulture) + " albums";

    /// <summary>A total for headers: "45 s", "48 min", "1 h 12 min".</summary>
    public static string LongDuration(long durationMs)
    {
        var t = TimeSpan.FromMilliseconds(durationMs);
        if (t.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours} h {t.Minutes} min");
        }

        return t.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{t.Seconds} s");
    }

    /// <summary>"1990s".</summary>
    public static string Decade(int decade) => decade.ToString(CultureInfo.InvariantCulture) + "s";

    /// <summary>
    /// Now Playing's format badge (docs/ui-screens-and-flows.md, "Shell / Now Playing": <c>FLAC 24/96</c>).
    /// A lossless track reads as depth and rate, a lossy one as its bit rate, and either falls back to the bare
    /// codec name when the scanner could not tell — a badge that says <c>MP3</c> is honest, one that says
    /// <c>MP3 0 kbps</c> is not.
    /// </summary>
    public static string Badge(string? codec, int? bitDepth, int? sampleRate, int? bitrateKbps)
    {
        if (string.IsNullOrWhiteSpace(codec))
        {
            return string.Empty;
        }

        string name = codec.ToUpperInvariant();
        if (!AudioFormats.IsLossless(codec))
        {
            return bitrateKbps is { } kbps && kbps > 0
                ? string.Create(CultureInfo.InvariantCulture, $"{name} {kbps} kbps")
                : name;
        }

        if (KiloHertz(sampleRate) is not { } rate)
        {
            return name;
        }

        return bitDepth is { } bits && bits > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{name} {bits}/{rate}")
            : name + " " + rate + " kHz";
    }

    /// <summary>A sample rate in kHz with no trailing zero: 48000 gives "48", 44100 gives "44.1".</summary>
    private static string? KiloHertz(int? sampleRate)
    {
        if (sampleRate is not { } hz || hz <= 0)
        {
            return null;
        }

        return hz % 1000 == 0
            ? (hz / 1000).ToString(CultureInfo.InvariantCulture)
            : (hz / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);
    }
}
