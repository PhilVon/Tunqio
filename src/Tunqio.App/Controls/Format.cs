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
}
