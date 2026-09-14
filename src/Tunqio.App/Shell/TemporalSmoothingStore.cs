using Tunqio.Core;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Shell;

/// <summary>
/// Reads <c>viz.temporalSmoothing</c>, <c>viz.temporalAttackMs</c> and <c>viz.temporalDecayMs</c> (T-184) into the
/// envelope the renderer is given, the same shape as <see cref="QualityPolicyStore"/>: the rule for "what does an odd
/// value in the file mean" in one table rather than in each place that reads it.
/// </summary>
/// <remarks>
/// A time constant outside the page's range is clamped into it, and one that is not a number is the default. Neither
/// is worth refusing a launch over. The switch off is <see cref="TemporalSmoothing.Off"/> whatever the two numbers
/// say, and the numbers are kept, so turning it on again gives back the rise and fall a person had chosen.
/// </remarks>
public static class TemporalSmoothingStore
{
    /// <summary>The longest rise the page offers, in milliseconds. Half a beat of a slow song; past it a bar arrives
    /// after the beat it was drawing has gone.</summary>
    public const float MaxAttackMs = 250f;

    /// <summary>The longest fall the page offers, in milliseconds.</summary>
    public const float MaxDecayMs = 2000f;

    /// <summary>Whether the envelope is switched on.</summary>
    public static bool ReadEnabled(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.GetValue(SettingsKeys.VizTemporalSmoothing, SettingsKeys.Defaults.VizTemporalSmoothing);
    }

    /// <summary>The stored rise time constant, in the page's range.</summary>
    public static float ReadAttackMs(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return InRange(
            settings.GetValue(SettingsKeys.VizTemporalAttackMs, SettingsKeys.Defaults.VizTemporalAttackMs),
            MaxAttackMs,
            SettingsKeys.Defaults.VizTemporalAttackMs);
    }

    /// <summary>The stored fall time constant, in the page's range.</summary>
    public static float ReadDecayMs(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return InRange(
            settings.GetValue(SettingsKeys.VizTemporalDecayMs, SettingsKeys.Defaults.VizTemporalDecayMs),
            MaxDecayMs,
            SettingsKeys.Defaults.VizTemporalDecayMs);
    }

    /// <summary>What the renderer should be given: <see cref="TemporalSmoothing.Off"/> while the switch is off.</summary>
    public static TemporalSmoothing Read(ISettingsStore settings) =>
        ReadEnabled(settings) ? new TemporalSmoothing(ReadAttackMs(settings), ReadDecayMs(settings)) : TemporalSmoothing.Off;

    /// <summary>
    /// Gives the attached renderer what the settings say, and returns it. Nothing happens while no renderer is attached:
    /// the window calls this again when it attaches one, because the renderer does not remember it across a detach.
    /// </summary>
    public static TemporalSmoothing Apply(IVisualizationHost host, ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(host);
        TemporalSmoothing smoothing = Read(settings);
        if (host.IsAttached)
        {
            host.SetTemporalSmoothing(smoothing);
        }

        return smoothing;
    }

    private static float InRange(float value, float max, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, max) : fallback;
}
