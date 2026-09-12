using Tunqio.Core;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Shell;

/// <summary>
/// Reads and writes <c>viz.quality</c> (E4-S7), the same shape as <see cref="ThemePolicy"/> and for the same
/// reason: "auto unless someone said otherwise" should be a table you can read rather than something you find
/// out by editing a settings file and watching a picture.
/// </summary>
/// <remarks>
/// Anything unrecognised is <see cref="QualityPolicy.Auto"/>, the documented default, and not an error. A
/// quality policy is not worth refusing to start over, and auto is the answer that works everywhere - it is
/// the one that copes with the machine it turns out to be running on.
/// </remarks>
public static class QualityPolicyStore
{
    /// <summary>Parses <c>viz.quality</c>; anything unrecognised is the documented default.</summary>
    public static QualityPolicy Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => QualityPolicy.Low,
        "medium" => QualityPolicy.Medium,
        "high" => QualityPolicy.High,
        _ => QualityPolicy.Auto,
    };

    /// <summary>The value <c>viz.quality</c> stores for <paramref name="policy"/>.</summary>
    public static string Name(QualityPolicy policy) => policy switch
    {
        QualityPolicy.Low => "low",
        QualityPolicy.Medium => "medium",
        QualityPolicy.High => "high",
        _ => "auto",
    };

    /// <summary>Reads the stored policy.</summary>
    public static QualityPolicy Read(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Parse(settings.GetValue<string?>(SettingsKeys.VizQuality, null));
    }

    /// <summary>Writes the policy back (the caller flushes).</summary>
    public static void Write(ISettingsStore settings, QualityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SetValue(SettingsKeys.VizQuality, Name(policy));
    }
}
