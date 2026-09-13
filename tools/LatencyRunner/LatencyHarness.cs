using System.Diagnostics;
using System.Globalization;
using Tunqio.Core.Audio;
using Tunqio.Core.Visualization;
using Tunqio.Interop;

namespace Tunqio.LatencyRunner;

/// <summary>
/// E4-S8: plays real audio through a real output device with a real renderer drawing from it, and reports how
/// far the picture was from the sound.
///
/// It drives <see cref="NativeEngine"/> and <see cref="NativeRenderer"/> directly rather than through the
/// shell, for the same reason the soak runner drives the engine directly: what is being measured is the
/// analysis-to-picture path, and going through the shell would also be measuring the shell's own bookkeeping.
/// A number that included the WinUI dispatcher would name the wrong thing.
/// </summary>
/// <remarks>
/// TWO PHASES, ONE MACHINE. The interesting number is not the absolute one - that belongs to this box - but the
/// difference the compensation makes, and a difference is only trustworthy if both halves ran within a minute
/// of each other on the same device with the same audio. So the uncompensated phase and the compensated one are
/// one run of this program and not two invocations of it.
/// </remarks>
internal sealed class LatencyHarness : IDisposable
{
    /// <summary>
    /// How often the probe is drained. The ring holds a bounded number of samples and the renderer fills it at
    /// the frame rate, so this is what keeps the distribution one of the WHOLE phase rather than of its last
    /// fraction of a second. 250 ms at 60 fps is fifteen samples against a ring of a thousand.
    /// </summary>
    private static readonly TimeSpan DrainEvery = TimeSpan.FromMilliseconds(250);

    private const int ProbeCapacity = 1024;

    private readonly LatencyOptions _options;
    private readonly TextWriter _out;
    private readonly List<string> _errors = [];

    private NativeEngine? _engine;
    private NativeRenderer? _renderer;
    private NativeTrack? _track;
    private DeviceLease? _deviceLease;
    private string? _scratch;

    public LatencyHarness(LatencyOptions options, TextWriter output)
    {
        _options = options;
        _out = output;
    }

    public async Task<LatencyReport> RunAsync(CancellationToken ct)
    {
        string trackPath = ChooseTrack();
        _deviceLease = DeviceLease.Take(TimeSpan.FromMinutes(3));
        if (!_deviceLease.Held)
        {
            _errors.Add("another process held the WASAPI output device for the whole three minutes this run waited; " +
                        "measuring anyway, so a device error below is contention and not a defect");
        }

        NativeEngine engine = NativeEngine.Create();
        _engine = engine;
        engine.SetOutput(new OutputConfig(_options.DeviceIndex, _options.Mode, _options.BufferMs));
        engine.SetVolume(_options.Volume);
        EngineStats opened = engine.GetStats();

        NativeRenderer renderer = NativeRenderer.Create(
            nint.Zero,
            engine.Handle,
            new RendererConfig(_options.Width, _options.Height, 1f, 1f, _options.ForceWarp, VSync: true, Headless: true));
        _renderer = renderer;
        // High, pinned. The adaptive controller is E4-S7's story and a tier change mid-phase would move the
        // frame rate underneath a measurement whose whole subject is the beat between two rates.
        renderer.SetQuality((int)QualityPolicy.High);
        string preset = ChoosePreset(renderer);

        NativeTrack track = engine.OpenTrack(trackPath);
        _track = track;
        engine.Play(track);

        await _out.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"measuring on {opened.OutputFormat} / {opened.OutputSampleRate} Hz, {(opened.Exclusive ? "exclusive" : "shared")}, " +
            $"a {opened.OutputBuffer.TotalMilliseconds:F0} ms buffer, preset '{preset}' at {_options.Width}x{_options.Height}, " +
            $"{_options.Phase.TotalSeconds:F0} s a phase")).ConfigureAwait(false);

        // A second of settling before anything is counted: the first pictures are drawn before the analysis has
        // published anything, the preset's first frames pay for shader warm-up, and the WASAPI buffer has not
        // reached its steady depth. None of that is what the run is about.
        await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);

        LatencyPhase uncompensated = await MeasureAsync(renderer, AvSyncMode.Newest, 0f, ct).ConfigureAwait(false);
        LatencyPhase compensated = await MeasureAsync(renderer, AvSyncMode.Audible, _options.OffsetMs, ct).ConfigureAwait(false);

        RenderStats rendered = renderer.GetStats();
        engine.Stop();

        return new LatencyReport(
            StartedUtc: DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Machine: Environment.MachineName + ", " + Environment.ProcessorCount + " logical cores",
            PhaseSeconds: _options.Phase.TotalSeconds,
            Device: opened.OutputFormat,
            OutputFormat: string.Create(CultureInfo.InvariantCulture, $"{opened.OutputSampleRate} Hz / {opened.OutputChannels} ch / {opened.OutputFormat}"),
            Exclusive: opened.Exclusive,
            RequestedBufferMs: _options.BufferMs,
            ReportedBufferMs: (int)Math.Round(opened.OutputBuffer.TotalMilliseconds),
            Preset: preset,
            Adapter: rendered.Adapter,
            Warp: rendered.Warp,
            Width: rendered.Width,
            Height: rendered.Height,
            RenderFps: Math.Round(rendered.Fps, 1),
            Track: Path.GetFileName(trackPath),
            BudgetMs: Math.Round(_options.BudgetMs, 3),
            Uncompensated: uncompensated,
            Compensated: compensated,
            NotMeasured: NotMeasured,
            Errors: _errors);
    }

    /// <summary>
    /// What is missing from every number above, said on the report rather than only in a document, because a
    /// JSON file outlives the prose around it and a reader who finds this file alone has to be able to see
    /// what it does not contain.
    /// </summary>
    private static IReadOnlyList<string> NotMeasured =>
    [
        "present to photon. The renderer here is headless: it draws into an offscreen texture and there is no " +
        "swap chain, so there is no Present and no compositor. On a real SwapChainPanel that edge is at least " +
        "one refresh interval and usually two, plus the panel's own response, and all of it is ADDITIVE to a " +
        "picture that is already late.",

        "the device's analogue and driver delay beyond what WASAPI reports as buffered.",

        "the reference machine. These numbers are this dev box: an i7-9700K with an RTX 4080 SUPER, which is " +
        "not the low-power integrated GPU the criteria were written against. See docs/spikes/e4-s8-latency-floor.md.",

        "the spectrum's own 21.3 ms. The picture is aligned on the hop mp_analysis_frame is labelled with, " +
        "which is where the WAVEFORM comes from. The spectrum in the same frame is a 2048-point Hann window " +
        "whose energy centroid sits 1024 samples earlier, and no choice of frame makes both fields right.",
    ];

    private async Task<LatencyPhase> MeasureAsync(NativeRenderer renderer, AvSyncMode mode, float offsetMs, CancellationToken ct)
    {
        renderer.SetAvSync(mode, offsetMs, ProbeCapacity);
        // Half a second for the change to take on the render thread and for the history ring to refill behind
        // the new target, then throw away whatever it collected while it was doing so.
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
        renderer.DrainLatency();

        var samples = new List<LatencySample>();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < _options.Phase && !ct.IsCancellationRequested)
        {
            await Task.Delay(DrainEvery, CancellationToken.None).ConfigureAwait(false);
            samples.AddRange(renderer.DrainLatency());
        }

        samples.AddRange(renderer.DrainLatency());
        if (samples.Count == 0)
        {
            _errors.Add($"the {mode} phase produced no samples at all");
            return new LatencyPhase(mode.ToString(), offsetMs, 0, 0, 0,
                Distribution.Of([]), Distribution.Of([]), Distribution.Of([]), Distribution.Of([]), Distribution.Of([]));
        }

        // Gaps in the frame index are samples the ring lost because this loop was behind it. Counted rather
        // than ignored: it says what fraction of the phase the distribution is actually of.
        int dropped = 0;
        for (int i = 1; i < samples.Count; i++)
        {
            dropped += (int)Math.Max(0, samples[i].FrameIndex - samples[i - 1].FrameIndex - 1);
        }

        int distinct = samples.Select(s => s.AnalysisSequence).Distinct().Count();
        await _out.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"  {mode,-8} offset {offsetMs,6:F1} ms: {samples.Count} pictures, {distinct} distinct analysis frames, " +
            $"{dropped} lost to the ring")).ConfigureAwait(false);

        return new LatencyPhase(
            mode.ToString(),
            offsetMs,
            samples.Count,
            distinct,
            dropped,
            Distribution.Of([.. samples.Select(s => s.ErrorMs)]),
            Distribution.Of([.. samples.Select(s => Math.Abs(s.ErrorMs))]),
            Distribution.Of([.. samples.Select(s => s.OutputBufferMs)]),
            Distribution.Of([.. samples.Select(s => s.AnalysisToSeenMs)]),
            Distribution.Of([.. samples.Select(s => s.FrameAgeAtPresentMs)]));
    }

    private string ChoosePreset(NativeRenderer renderer)
    {
        if (!string.IsNullOrEmpty(_options.PresetRoot) && Directory.Exists(_options.PresetRoot))
        {
            renderer.SetUserPresetRoot(_options.PresetRoot);
            renderer.RescanPresets();
        }

        IReadOnlyList<PresetInfo> catalogue = renderer.EnumeratePresets();
        if (catalogue.Count == 0)
        {
            throw new LatencyUsageException("the renderer's preset catalogue is empty, which should be impossible - the core carries a built-in preset");
        }

        PresetInfo preferred = catalogue.FirstOrDefault(p => p.Id == "spectrum-bars");
        string id = _options.PresetId ?? (string.IsNullOrEmpty(preferred.Id) ? catalogue[0].Id : preferred.Id);
        renderer.SetPreset(id);
        return id;
    }

    /// <summary>
    /// The audio to play. By default a tone this harness writes to its own scratch directory, long enough for
    /// the whole run: see <see cref="ToneFixture"/> for why not the fixture library and why not somebody's
    /// music. A <c>-source</c> file is played as given; a <c>-source</c> directory is searched for the first
    /// playable file in name order rather than shuffled, because a run that picked a different track each time
    /// would be comparing its two phases across two pieces of music.
    /// </summary>
    private string ChooseTrack()
    {
        if (_options.Source is null)
        {
            _scratch = Path.Combine(Path.GetTempPath(), "tunqio-latency-" + Environment.ProcessId);
            // Settling, the two phases, each phase's own half-second of settling, and slack.
            double seconds = (2 * _options.Phase.TotalSeconds) + 10;
            return ToneFixture.Write(_scratch, seconds);
        }

        if (File.Exists(_options.Source))
        {
            return _options.Source;
        }

        if (!Directory.Exists(_options.Source))
        {
            throw new LatencyUsageException($"{_options.Source} is neither a file nor a directory.");
        }

        string? first = Directory
            .EnumerateFiles(_options.Source, "*", SearchOption.AllDirectories)
            .Where(f => LatencyOptions.AudioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return first ?? throw new LatencyUsageException($"{_options.Source} holds no playable audio.");
    }

    public void Dispose()
    {
        if (_track is not null && _engine is not null)
        {
            _track.Dispose();
        }

        _renderer?.Dispose();
        _engine?.Dispose();
        _deviceLease?.Dispose();
        if (_scratch is not null && Directory.Exists(_scratch))
        {
            try
            {
                Directory.Delete(_scratch, recursive: true);
            }
            catch (IOException)
            {
                // The tone is a few megabytes in %TEMP% and the measurement is already made; a file the
                // decoder has not let go of yet is not worth failing a finished run over.
            }
        }
    }
}
