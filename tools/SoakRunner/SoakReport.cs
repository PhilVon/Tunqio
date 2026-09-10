using System.Globalization;
using System.Text.Json;

namespace Tunqio.SoakRunner;

/// <summary>One stats reading, kept so a run that fails can be read backwards to the sample it started failing at.</summary>
internal sealed record SoakSample(
    double ElapsedSeconds,
    long Callbacks,
    long Underruns,
    double CallbackMaxMs,
    double PositionSeconds,
    long WorkingSetMb);

/// <summary>
/// What the run did and whether it passed. <see cref="Underruns"/> is <c>mp_engine_stats.underruns</c> read at the
/// end, which is the criterion (E1-S11, AC-67); the rest is there so a failure says something more useful than "it
/// failed", and so a pass cannot be a run that quietly stopped making sound after two minutes.
/// </summary>
internal sealed record SoakReport(
    string StartedUtc,
    double RequestedSeconds,
    double ElapsedSeconds,
    bool RanToCompletion,
    string Device,
    string OutputFormat,
    bool Exclusive,
    int BufferMs,
    string Join,
    float Volume,
    int LibraryTracks,
    int TracksPlayed,
    int Joins,
    int Seeks,
    long Callbacks,
    long Underruns,
    int UnderrunEvents,
    double CallbackMaxMs,
    int Stalls,
    int DeviceEvents,
    IReadOnlyList<string> Errors,
    long WorkingSetStartMb,
    long WorkingSetEndMb,
    IReadOnlyList<SoakSample> Samples)
{
    /// <summary>How the report is written, to the console and to <c>-out</c>.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new() { WriteIndented = true };

    /// <summary>
    /// Zero underruns is the criterion, but a run only earns the word "pass" if it also played for the time it was
    /// asked to and kept the clock moving: silence produces no underruns either.
    /// </summary>
    public bool Passed =>
        RanToCompletion && Underruns == 0 && UnderrunEvents == 0 && Stalls == 0 && Errors.Count == 0 && TracksPlayed > 0;

    public string Verdict => Passed
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"PASS · {ElapsedSeconds / 60:F1} min, {TracksPlayed} tracks, {Joins} joins, {Callbacks} callbacks, 0 underruns, callback max {CallbackMaxMs:F2} ms")
        : string.Create(
            CultureInfo.InvariantCulture,
            $"FAIL · {ElapsedSeconds / 60:F1} min of {RequestedSeconds / 60:F1}, {Underruns} underrun(s) in stats, {UnderrunEvents} underrun event(s), {Stalls} stall(s), {Errors.Count} error(s)");
}
