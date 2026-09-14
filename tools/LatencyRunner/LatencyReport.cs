using System.Globalization;
using System.Text.Json;

namespace Tunqio.LatencyRunner;

/// <summary>
/// The distribution of one measured quantity. Three percentiles are what AC-130 asks for; the count, the mean
/// and the two extremes are here because a percentile with no n beside it is a number nobody can argue with,
/// and this project has already retired two single-sample budgets for exactly that.
/// </summary>
internal sealed record Distribution(int Count, double Min, double P50, double P95, double P99, double Max, double Mean)
{
    public static Distribution Of(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return new Distribution(0, 0, 0, 0, 0, 0, 0);
        }

        double[] sorted = [.. values];
        Array.Sort(sorted);
        return new Distribution(
            sorted.Length,
            Round(sorted[0]),
            Round(Percentile(sorted, 0.50)),
            Round(Percentile(sorted, 0.95)),
            Round(Percentile(sorted, 0.99)),
            Round(sorted[^1]),
            Round(sorted.Average()));
    }

    /// <summary>
    /// The nearest-rank percentile: the smallest value at or above which the given fraction of the sample sits.
    /// No interpolation, so a reported number is one that was actually measured.
    /// </summary>
    private static double Percentile(double[] sorted, double fraction)
    {
        int rank = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    private static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}

/// <summary>
/// One phase of the run: the same machine, the same audio, the same preset, and one choice of which analysis
/// frame reaches the screen.
/// </summary>
/// <param name="Mode">Newest, or the frame the listener is hearing.</param>
/// <param name="OffsetMs">The av-sync offset in force.</param>
/// <param name="Frames">Pictures the probe reported.</param>
/// <param name="DistinctAnalysisFrames">
/// How many DIFFERENT analysis frames reached the screen. Fewer than the hops that were published means the
/// renderer never saw them; more pictures than distinct frames means it drew some of them twice.
/// </param>
/// <param name="Dropped">
/// Pictures the probe's ring lost because the drain was behind, counted from the gaps in the frame index. Not a
/// failure - the ring is a handover - but a number that says how much of the run the distribution is of.
/// </param>
/// <param name="ErrorMs">
/// Signed audio-to-picture error. Positive is a LATE picture (showing audio already heard), negative an early
/// one (showing audio mixed but not yet played). This is the quantity the story is about.
/// </param>
/// <param name="AbsoluteErrorMs">
/// The same, unsigned. This is what a percentile against a refresh interval has to be taken of: a signed p95
/// says nothing about how far wrong the early half of the distribution went.
/// </param>
/// <param name="OutputBufferMs">Mixed but not yet heard: the budget compensation is paid out of.</param>
/// <param name="AnalysisToSeenMs">Mixer finishing the hop, to the render thread first seeing that frame.</param>
/// <param name="FrameAgeAtPresentMs">
/// That first sight, to the present that carried it. Not the draw cost: compensated, this is mostly the
/// deliberate delay, because a frame chosen for its age has been in the renderer's history since it arrived.
/// Uncompensated it is the draw plus, on a repeated picture, the render tick it waited.
/// </param>
/// <param name="FrameIntervalMs">
/// The phase's mean frame interval: its measured length over the frame indices it spanned. The envelope's cost is
/// counted in these.
/// </param>
/// <param name="SmoothingAddedMs">
/// T-184: what temporal smoothing adds to a transient on top of <paramref name="ErrorMs"/>, which describes the frame
/// chosen and not how far the envelope has carried it. The frames a full-scale rise takes to reach half height -
/// the first at or past attack times ln 2 - less the one it arrives on, times <paramref name="FrameIntervalMs"/>.
/// Zero with smoothing off and with a zero rise.
/// </param>
internal sealed record LatencyPhase(
    string Mode,
    double OffsetMs,
    int Frames,
    int DistinctAnalysisFrames,
    int Dropped,
    Distribution ErrorMs,
    Distribution AbsoluteErrorMs,
    Distribution OutputBufferMs,
    Distribution AnalysisToSeenMs,
    Distribution FrameAgeAtPresentMs,
    double FrameIntervalMs = 0,
    double SmoothingAddedMs = 0);

/// <summary>What the run did, what it measured, and - as loudly as prose can - what it could not see.</summary>
internal sealed record LatencyReport(
    string StartedUtc,
    string Machine,
    double PhaseSeconds,
    string Device,
    string OutputFormat,
    bool Exclusive,
    int RequestedBufferMs,
    int ReportedBufferMs,
    string Preset,
    string Adapter,
    bool Warp,
    int Width,
    int Height,
    double RenderFps,
    string Track,
    double BudgetMs,
    double SmoothingAttackMs,
    double SmoothingDecayMs,
    LatencyPhase Uncompensated,
    LatencyPhase Compensated,
    IReadOnlyList<string> NotMeasured,
    IReadOnlyList<string> Errors)
{
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };

    /// <summary>
    /// Whether the run MEASURED something, which is all an exit code from this tool is entitled to say. It is
    /// deliberately not "p95 was inside the budget": the picture-to-photon edge is not in this number and this
    /// dev machine is not the reference machine, so a pass or fail on the budget would be a claim the harness
    /// cannot support. The budget is reported, and <see cref="Verdict"/> says which side of it the run fell.
    /// </summary>
    public bool Passed =>
        Errors.Count == 0 && Uncompensated.ErrorMs.Count > 100 && Compensated.ErrorMs.Count > 100;

    public string Verdict => !Passed
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"NO MEASUREMENT · {Uncompensated.ErrorMs.Count} uncompensated and {Compensated.ErrorMs.Count} compensated sample(s), {Errors.Count} error(s)")
        : string.Create(
            CultureInfo.InvariantCulture,
            $"MEASURED · av error p50/p95/p99: {Uncompensated.ErrorMs.P50:F2}/{Uncompensated.ErrorMs.P95:F2}/{Uncompensated.ErrorMs.P99:F2} ms uncompensated, " +
            $"{Compensated.ErrorMs.P50:F2}/{Compensated.ErrorMs.P95:F2}/{Compensated.ErrorMs.P99:F2} ms compensated. " +
            $"|error| p95 {Compensated.AbsoluteErrorMs.P95:F2} ms against a {BudgetMs:F2} ms refresh interval " +
            $"({(Compensated.AbsoluteErrorMs.P95 <= BudgetMs ? "inside" : "outside")}), " +
            $"and the present-to-photon edge is not in it. Temporal smoothing rise {SmoothingAttackMs:F0} ms / fall {SmoothingDecayMs:F0} ms " +
            $"adds {Compensated.SmoothingAddedMs:F2} ms to a transient at {Compensated.FrameIntervalMs:F2} ms a frame.");
}
