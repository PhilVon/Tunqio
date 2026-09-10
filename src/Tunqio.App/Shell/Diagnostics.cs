using System.Globalization;
using System.Text;
using Tunqio.Core.Audio;
using Tunqio.Core.Playback;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Shell;

/// <summary>One line of the overlay: what it is, and what it says right now.</summary>
public sealed record DiagnosticsRow(string Label, string Value);

/// <summary>A group of rows under a heading.</summary>
public sealed record DiagnosticsSection(string Title, IReadOnlyList<DiagnosticsRow> Rows);

/// <summary>
/// What the diagnostics overlay says (E2-S8), as a pure function of the three things it reports on: the playback
/// snapshot, the engine's output statistics and the renderer's frame statistics. Pure, and free of XAML, because
/// the criterion is that the values are right and can be copied — and a number formatted inside a
/// <c>TextBlock</c> can only be checked by reading it off a screen.
/// </summary>
/// <remarks>
/// The overlay is also where the readout E0-S5 left in the corner of the shell ends up. That readout was always a
/// placeholder — it is a developer's line of text over the user's album art — and moving it here is the point of
/// this story as much as the new numbers are.
/// </remarks>
public static class Diagnostics
{
    /// <summary>The sections the overlay draws. Anything that is not there yet is said to be missing, not omitted.</summary>
    public static IReadOnlyList<DiagnosticsSection> Describe(
        PlaybackSnapshot? snapshot,
        EngineStats? engine,
        RenderStats? renderer,
        string? build = null,
        string? rendererProblem = null) =>
    [
        new("Playback", Playback(snapshot)),
        new("Output", Output(engine)),
        new("Renderer", Renderer(renderer, rendererProblem)),
        new("Build", [new DiagnosticsRow("Version", build ?? "unknown")]),
    ];

    /// <summary>
    /// The whole overlay as text for the clipboard: what someone pastes into a bug report. Tab-separated, so it
    /// survives being pasted into a message as readably as into a spreadsheet.
    /// </summary>
    public static string Report(IReadOnlyList<DiagnosticsSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var text = new StringBuilder();
        foreach (DiagnosticsSection section in sections)
        {
            text.Append('[').Append(section.Title).AppendLine("]");
            foreach (DiagnosticsRow row in section.Rows)
            {
                text.Append(row.Label).Append('\t').AppendLine(row.Value);
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static IReadOnlyList<DiagnosticsRow> Playback(PlaybackSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return [new DiagnosticsRow("State", "no session")];
        }

        return
        [
            new DiagnosticsRow("State", snapshot.State.ToString()),
            new DiagnosticsRow("Position", Clock(snapshot.Position) + " / " + Clock(snapshot.Duration)),
            new DiagnosticsRow("Track", snapshot.Track is { } track ? Inv($"{track.Title} (id {track.Id})") : "none"),
            new DiagnosticsRow("Queue", Inv($"{(snapshot.QueueIndex is int at ? at + 1 : 0)} of {snapshot.QueueCount}")),
            new DiagnosticsRow("Shuffle / repeat", (snapshot.Shuffle ? "on" : "off") + " / " + snapshot.Repeat.ToString().ToLowerInvariant()),
            new DiagnosticsRow("Volume", Inv($"{snapshot.Volume * 100:F0}%")),
            new DiagnosticsRow("Output health", Health(snapshot.Output)),
        ];
    }

    private static string Health(OutputStatus status) => status.Health switch
    {
        OutputHealth.Lost => "device lost" + Named(status),
        OutputHealth.Returned => "device offered back" + Named(status),
        _ => "ok",
    };

    private static string Named(OutputStatus status) =>
        string.IsNullOrWhiteSpace(status.DeviceId) ? string.Empty : " (" + status.DeviceId + ")";

    private static IReadOnlyList<DiagnosticsRow> Output(EngineStats? engine)
    {
        if (engine is null)
        {
            return [new DiagnosticsRow("Engine", "unavailable")];
        }

        return
        [
            new DiagnosticsRow("Started", engine.OutputStarted ? "yes" : "no"),
            new DiagnosticsRow("Mode", engine.Exclusive ? "exclusive" : "shared"),
            new DiagnosticsRow("Format", Inv($"{engine.OutputSampleRate} Hz · {engine.OutputChannels} ch · {engine.OutputFormat}")),
            // The buffer is the latency: it is how far ahead of the speakers the mixer is working.
            new DiagnosticsRow("Latency", Inv($"{engine.OutputBuffer.TotalMilliseconds:F1} ms")),
            new DiagnosticsRow("Callbacks", engine.Callbacks.ToString(CultureInfo.InvariantCulture)),
            new DiagnosticsRow("Underruns", engine.Underruns.ToString(CultureInfo.InvariantCulture)),
            new DiagnosticsRow("Callback max", Inv($"{engine.CallbackMax.TotalMilliseconds:F2} ms")),
        ];
    }

    private static IReadOnlyList<DiagnosticsRow> Renderer(RenderStats? renderer, string? problem)
    {
        if (renderer is null)
        {
            // The reason belongs in the report: "unavailable" in a pasted bug report is a question, not an answer.
            return [new DiagnosticsRow("Renderer", problem ?? "unavailable")];
        }

        return
        [
            new DiagnosticsRow("Adapter", renderer.Adapter + (renderer.Warp ? " (WARP)" : string.Empty)),
            new DiagnosticsRow("Surface", Inv($"{renderer.Width}×{renderer.Height}{(renderer.Visible ? string.Empty : " (hidden)")}")),
            new DiagnosticsRow("Frame rate", Inv($"{renderer.Fps:F1} fps")),
            new DiagnosticsRow("Frame time", Inv($"avg {renderer.FrameAverage.TotalMilliseconds:F2} ms · max {renderer.FrameMax.TotalMilliseconds:F1} ms")),
            new DiagnosticsRow("Missed refreshes", renderer.DxgiMissedRefreshes.ToString(CultureInfo.InvariantCulture)),
            new DiagnosticsRow("Frame histogram", Histogram(renderer.FrameHistogram)),
            new DiagnosticsRow("Device lost", renderer.DeviceLost ? "yes" : "no"),
        ];
    }

    /// <summary>
    /// The frame histogram with its bucket edges, because the bare array of counts means nothing to whoever is
    /// reading the overlay — and the last bucket is open-ended, which is the one that matters.
    /// </summary>
    private static string Histogram(IReadOnlyList<int> counts)
    {
        IReadOnlyList<double> edges = RenderStats.HistogramEdgesMs;
        var text = new StringBuilder();
        for (int i = 0; i < counts.Count; i++)
        {
            if (i > 0)
            {
                text.Append(" · ");
            }

            string label = i < edges.Count
                ? Inv($"<{edges[i]:0.#}ms")
                : Inv($">{edges[^1]:0.#}ms");
            text.Append(label).Append(' ').Append(counts[i].ToString(CultureInfo.InvariantCulture));
        }

        return text.Length == 0 ? "none" : text.ToString();
    }

    private static string Clock(TimeSpan time) => Controls.Format.Duration((int)Math.Max(0, time.TotalMilliseconds));

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
