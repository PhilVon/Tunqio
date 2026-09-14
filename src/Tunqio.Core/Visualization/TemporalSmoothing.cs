namespace Tunqio.Core.Visualization;

/// <summary>
/// The renderer's attack and decay envelope on the analysis frame (T-184, <c>mp_renderer_set_temporal_smoothing</c>).
/// </summary>
/// <remarks>
/// Each time constant is how long a change takes to cover 63% of its distance, in milliseconds: a rise over
/// <paramref name="AttackMs"/>, a fall over <paramref name="DecayMs"/>. Zero follows that direction at once, and both at
/// zero is <see cref="Off"/>, which draws the frame the analysis published unchanged. Exponential in time rather than
/// in frames, so the same values mean the same motion on a 60 Hz and a 144 Hz display.
/// </remarks>
/// <param name="AttackMs">Rise time constant in milliseconds; 0 follows a rise at once.</param>
/// <param name="DecayMs">Fall time constant in milliseconds; 0 follows a fall at once.</param>
public readonly record struct TemporalSmoothing(float AttackMs, float DecayMs)
{
    /// <summary>No envelope: the picture is drawn from the analysis frame as published.</summary>
    public static TemporalSmoothing Off => default;

    /// <summary>True when neither direction is eased.</summary>
    public bool IsOff => AttackMs <= 0f && DecayMs <= 0f;
}
