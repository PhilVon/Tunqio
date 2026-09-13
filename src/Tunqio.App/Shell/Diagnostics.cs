using System.Globalization;
using System.Text;
using Tunqio.Core.Audio;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Shell;

/// <summary>One line of the overlay: what it is, and what it says right now.</summary>
public sealed record DiagnosticsRow(string Label, string Value);

/// <summary>A group of rows under a heading.</summary>
public sealed record DiagnosticsSection(string Title, IReadOnlyList<DiagnosticsRow> Rows);

/// <summary>
/// Audio-reactive theming as the overlay reports it (T-155): whether it is running, why it is not, what colours
/// are on screen, whether the visualizer is being told them, and which album the art colours came from.
/// </summary>
/// <remarks>
/// <para>
/// This exists because the feature is close to unfalsifiable by eye and was checked by eye. The gradient shows
/// through two <c>LayerFillColorDefaultBrush</c> panels, in colours held to 4.5:1 against the theme's
/// foregrounds, eased over a 0.5 to 4 s time constant - which is the correct design and is also why AC-266
/// asked a person to watch something stop and got, reasonably, "I saw no observable difference". A number that
/// stops moving is falsifiable; a subtle gradient is not.
/// </para>
/// <para>
/// <see cref="Painted"/> is read off the layer that painted it rather than off the engine that computed it, so
/// the row says what is on the screen and not what was asked for.
/// </para>
/// </remarks>
/// <param name="Active">True while the theme is following the music.</param>
/// <param name="StoppedBecause">The first switch that says no, or null while it is running.</param>
/// <param name="Painted">The colours the shell's gradient is showing, or null when the static theme is back.</param>
/// <param name="Ticks">Polls served since the controller was created, and <paramref name="Applied"/> of them painted.</param>
/// <param name="RendererPushes">Palettes that reached the presets through <c>mp_renderer_set_theme</c>.</param>
/// <param name="RendererSkips">Palettes the visualizer was not told, for the reason in <paramref name="RendererProblem"/>.</param>
/// <param name="RendererProblem">Why the visualizer is not being told, or null while it is.</param>
/// <param name="ArtHash">The <c>art_hash</c> the album art colours came from; null for a track with no art.</param>
/// <param name="ArtColours">Those colours, most populous first, or null when there are none.</param>
public sealed record ReactiveThemeStatus(
    bool Active,
    string? StoppedBecause,
    ReactiveThemePalette? Painted,
    long Ticks,
    long Applied,
    long RendererPushes,
    long RendererSkips,
    string? RendererProblem,
    string? ArtHash,
    IReadOnlyList<PaletteColor>? ArtColours);

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
        string? rendererProblem = null,
        ReactiveThemeStatus? theming = null,
        bool? rendererHasAudioSource = null) =>
    [
        new("Playback", Playback(snapshot)),
        new("Output", Output(engine)),
        new("Renderer", Renderer(renderer, rendererProblem, rendererHasAudioSource)),
        new("Reactive theming", Theming(theming)),
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

    /// <param name="audioSource">
    /// Whether the renderer was given an engine to draw from, or null when nothing has said. T-179: a renderer
    /// with no engine is the one failure that looks exactly like success - every preset falls back to its idle
    /// animation, which moves, so a reviewer watching the panel sees a visualizer apparently working. The row
    /// exists so that state has to be read rather than inferred from a picture.
    /// </param>
    private static IReadOnlyList<DiagnosticsRow> Renderer(RenderStats? renderer, string? problem, bool? audioSource)
    {
        if (renderer is null)
        {
            // The reason belongs in the report: "unavailable" in a pasted bug report is a question, not an answer.
            return [new DiagnosticsRow("Renderer", problem ?? "unavailable")];
        }

        return
        [
            new DiagnosticsRow("Adapter", renderer.Adapter + (renderer.Warp ? " (WARP)" : string.Empty)),
            new DiagnosticsRow("Audio source", audioSource switch
            {
                true => "engine attached",
                false => "NONE - every preset is drawing its idle animation",
                null => "unknown",
            }),
            new DiagnosticsRow("Surface", Inv($"{renderer.Width}×{renderer.Height}{(renderer.Visible ? string.Empty : " (hidden)")}")),
            new DiagnosticsRow("Quality", Quality(renderer)),
            new DiagnosticsRow("Frame cost", FrameCost(renderer)),
            new DiagnosticsRow("Frame rate", Inv($"{renderer.Fps:F1} fps")),
            new DiagnosticsRow("Frame time", Inv($"avg {renderer.FrameAverage.TotalMilliseconds:F2} ms · max {renderer.FrameMax.TotalMilliseconds:F1} ms")),
            new DiagnosticsRow("Missed refreshes", renderer.DxgiMissedRefreshes.ToString(CultureInfo.InvariantCulture)),
            new DiagnosticsRow("Frame histogram", Histogram(renderer.FrameHistogram)),
            new DiagnosticsRow("Device lost", renderer.DeviceLost ? "yes" : "no"),
        ];
    }

    /// <summary>
    /// The tier the renderer is drawing at, how it came to be that tier, and what it is costing in pixels
    /// (E4-S7, AC-128). The rendered size is here rather than folded into Surface because the two really are
    /// different things once a tier is below High: the surface is the panel and this is the rectangle of it
    /// the picture is drawn into, which the compositor stretches back out.
    /// </summary>
    /// <remarks>
    /// The change count is the part worth reading twice. A controller that is thrashing looks exactly like one
    /// that is working if all you can see is the tier it happens to be on, and "without oscillating" is half
    /// of what E4-S7 promises - so the number of times it has changed its mind is on the overlay and in the
    /// pasted report. It counts the controller's own decisions; a tier the user pinned is not one of them.
    /// </remarks>
    private static string Quality(RenderStats renderer)
    {
        string tier = renderer.Tier.ToString().ToLowerInvariant();
        string how = renderer.Policy == QualityPolicy.Auto ? "auto" : "set to " + renderer.Policy.ToString().ToLowerInvariant();
        string size = Inv($"{renderer.RenderWidth}×{renderer.RenderHeight} at {renderer.RenderScale:0.##}×");
        string changes = renderer.QualityChanges == 1
            ? "1 change"
            : Inv($"{renderer.QualityChanges} changes");
        return Inv($"{tier} ({how}) · {size} · {changes}");
    }

    /// <summary>
    /// What the controller is deciding on, and where it came from. The source belongs next to the number
    /// because the two answers mean different things: a GPU timestamp is the work in a frame, while the frame
    /// interval is the pace the frames arrived at - which under vsync says nothing about how hard the GPU was
    /// working, and is only the fallback for a device that will not make the queries.
    /// </summary>
    private static string FrameCost(RenderStats renderer)
    {
        string source = renderer.CostSource == RenderCostSource.GpuTimestamp ? "GPU timestamp" : "frame interval";
        return Inv($"{renderer.FrameCost.TotalMilliseconds:F2} ms ({source})");
    }

    /// <summary>
    /// What the theming is doing (T-155). Every row is a fact that changes when the feature's state does, which
    /// is what the gradient itself cannot offer: "running" becomes "stopped: Windows is asking for reduced
    /// motion" the moment the switch is thrown, and the palette line stops moving with it.
    /// </summary>
    private static IReadOnlyList<DiagnosticsRow> Theming(ReactiveThemeStatus? theming)
    {
        if (theming is null)
        {
            // Not "off": the controller is started when the audio engine comes up, and a session with no audio
            // never starts one at all. Saying which is the difference between a bug and a machine with no sound.
            return [new DiagnosticsRow("Theming", "not started (no analysis stream)")];
        }

        return
        [
            new DiagnosticsRow("State", theming.Active ? "running" : "stopped: " + (theming.StoppedBecause ?? "unknown")),
            new DiagnosticsRow("Palette", Palette(theming.Painted)),
            new DiagnosticsRow("Ticks", Inv($"{theming.Ticks} · {theming.Applied} painted")),
            new DiagnosticsRow("Visualizer", Told(theming)),
            new DiagnosticsRow("Album art", Art(theming.ArtHash, theming.ArtColours)),
        ];
    }

    /// <summary>
    /// The four colours on screen, named, because the whole point of the row is that a person can watch them
    /// move with the music - and four bare hex triples say nothing about which is which.
    /// </summary>
    private static string Palette(ReactiveThemePalette? painted) =>
        painted is null
            ? "none (the static theme is showing)"
            : Inv($"primary {painted.Primary.Hex} · secondary {painted.Secondary.Hex} · accent {painted.Accent.Hex} · background {painted.Background.Hex}");

    /// <summary>
    /// Whether the theme is reaching the presets (T-156). This is the row that would have answered AC-266:
    /// before that wiring existed it read "not told: no renderer was passed", every tick, for the life of the
    /// app.
    /// </summary>
    private static string Told(ReactiveThemeStatus theming) =>
        theming.RendererProblem is null
            ? Inv($"{theming.RendererPushes} palettes sent")
            : Inv($"not told: {theming.RendererProblem} ({theming.RendererSkips} skipped, {theming.RendererPushes} sent)");

    /// <summary>
    /// Which album the glow's colours came from (T-147). The hash is abbreviated because it is an identity and
    /// not a value; the colours are whole, because they are the thing being checked.
    /// </summary>
    private static string Art(string? hash, IReadOnlyList<PaletteColor>? colours)
    {
        if (string.IsNullOrEmpty(hash))
        {
            return "none (this track has no art)";
        }

        string id = hash.Length > 8 ? hash[..8] : hash;
        if (colours is null || colours.Count == 0)
        {
            return id + " · no palette stored";
        }

        return id + " · " + string.Join(' ', colours.Select(c => c.Hex));
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
