using System.Globalization;

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
}
