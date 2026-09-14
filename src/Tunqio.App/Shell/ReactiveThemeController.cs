using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Shell;

/// <summary>Where a reactive palette is painted: the shell's gradient, and in tests a recorder.</summary>
public interface IReactiveThemeSink
{
    /// <summary>Paints <paramref name="palette"/>. Called at the poll rate while the theming is running.</summary>
    void Apply(ReactiveThemePalette palette);

    /// <summary>
    /// Puts the static theme back: reactive theming has been turned off, or Windows has asked for reduced
    /// motion or high contrast. Idempotent, because the reasons to stop can arrive together.
    /// </summary>
    void Clear();
}

/// <summary>
/// Audio-reactive theming as the shell runs it (E4-S6): poll <see cref="IAnalysisFrameSource"/> at 30 Hz, fold
/// each frame into <see cref="ReactiveThemeEngine"/>, paint the result, and stop the moment any of the three
/// switches says to.
/// </summary>
/// <remarks>
/// <para>
/// <b>Polled, not subscribed.</b> <see cref="IAnalysisFrameSource.Frames"/> only pushes when there is a new
/// frame, so a paused player pushes nothing - and a theming that only wakes on a frame would freeze on the last
/// chord and, worse, would not notice reduced motion until the music started again. A tick of its own means
/// both "ease back to rest" and "stop" have a clock that does not depend on anything playing.
/// </para>
/// <para>
/// <b>Stopping is not waited for.</b> <see cref="IAccessibilitySignals.Changed"/> stops the theming where it is
/// raised, without waiting for the next tick, and the tick re-reads the switches anyway so a change event that
/// never arrives still costs at most one <see cref="PollInterval"/>. Those are the two halves of AC-127's
/// "within one second": the event is the fast path and the poll is the bound.
/// </para>
/// <para>
/// <b>The renderer gets the same palette.</b> <c>mp_renderer_set_theme</c> carries it into every preset's
/// constant buffer, so the visualizer and the window around it are painted from one palette rather than two.
/// The shell passes the host from E4-S9 (T-156); before that it did not, and the field E4-S6 added to every
/// preset's <c>b0</c> carried nothing in the running app.
/// </para>
/// <para>
/// <b>A renderer that is not there is not an error, and the guard is here rather than on the host</b> (T-156).
/// <c>VisualizationHost</c> refuses every mutator while detached, uniformly, and the settings page depends on
/// that: a detached theme call that quietly succeeded would hide a real fault there. This is the one caller
/// that ticks 30 times a second whatever else is or is not up - the same reason it polls rather than subscribes
/// - so tolerating a missing renderer is this class's job. <see cref="RendererSkips"/> counts what was not
/// said, so "the visualizer is not being told" is a number in the diagnostics overlay rather than a silence.
/// </para>
/// <para>
/// <b>Nothing may leave the timer callback</b> (T-159, found by T-58 on <c>VisualizationHost.Poll</c>). A
/// <see cref="System.Threading.Timer"/> callback runs on a pool thread, so an exception that escapes it does
/// not fail a call - it takes the process down, and this one crosses the ABI thirty times a second. So the
/// callback is <see cref="TickSafely"/> rather than <see cref="Tick"/>: it takes T-58's shape, keeping the
/// reason on <see cref="TickFailure"/> and stopping a poll that a repeat will not fix. <see cref="Tick"/>
/// itself still throws, because a test drives it directly and a swallowed assertion is worse than a crash.
/// </para>
/// <para>
/// <b>The one deliberate difference from T-58's shape</b> is that a renderer that will not take the palette
/// stops nothing. There, a failing <c>mp_renderer_get_stats</c> is terminal - an ABI pairing that will be
/// refused identically for the life of the process. Here a detached renderer is the normal transient state:
/// the panel has not loaded yet, or the window is closing, and the tick is not only the renderer's feed but
/// the theming's own clock - the bound on AC-127's "within one second" and the thing that eases the gradient
/// back to rest. Stopping it would take the window's own gradient down with the visualizer's.
/// </para>
/// </remarks>
public sealed class ReactiveThemeController : IDisposable
{
    /// <summary>30 Hz, the rate the roadmap sets and the rate <c>NativeAnalysisFrameSource</c> publishes at.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000.0 / 30.0);

    private readonly IAnalysisFrameSource _frames;
    private readonly ISettingsStore _settings;
    private readonly IAccessibilitySignals _accessibility;
    private readonly IReactiveThemeSink _sink;
    private readonly IVisualizationHost? _renderer;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private ReactiveThemeEngine _engine;
    private ITimer? _timer;
    private long _lastTick;
    private uint _lastSequence;
    private bool _haveSequence;
    private bool _running;
    private bool _disposed;

    /// <param name="isDark">
    /// Whether the shell is in its dark theme. Read on every rebuild rather than captured, because the theme
    /// can change under a running window and the contrast guarantee is made against the text of the theme that
    /// is actually on screen.
    /// </param>
    /// <param name="renderer">
    /// The visualizer, if one is attached; null simply leaves the presets untold. Required and positional, with
    /// no default, deliberately (T-180): T-156 was this argument being omitted by the shell — the only caller
    /// that mattered — which left <c>mp_renderer_set_theme</c> with no caller in the running app and E4-S6's
    /// sixteen floats in every preset's b0 carrying nothing, while every test passed because every test passes
    /// it. A caller that genuinely wants the presets untold now has to write <c>null</c> and mean it.
    /// </param>
    /// <param name="time">Injected so a test owns the clock; <see cref="TimeProvider.System"/> otherwise.</param>
    public ReactiveThemeController(
        IAnalysisFrameSource frames,
        ISettingsStore settings,
        IAccessibilitySignals accessibility,
        IReactiveThemeSink sink,
        Func<bool> isDark,
        IVisualizationHost? renderer,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(accessibility);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(isDark);
        _frames = frames;
        _settings = settings;
        _accessibility = accessibility;
        _sink = sink;
        _renderer = renderer;
        _time = time ?? TimeProvider.System;
        IsDark = isDark;
        _engine = new ReactiveThemeEngine(isDark(), ReactiveThemeOptions.Read(settings));
        _lastTick = _time.GetTimestamp();
        // Not "the renderer is not attached": nothing has looked yet, and a readout that asserted a renderer
        // state it had not checked would say "not attached" beside a Renderer section reporting 144 fps.
        RendererProblem = renderer is null ? "no renderer was passed" : "nothing has been sent yet";

        _settings.Changed += OnSettingChanged;
        _accessibility.Changed += OnAccessibilityChanged;
    }

    /// <summary>Whether the shell is in its dark theme.</summary>
    public Func<bool> IsDark { get; }

    /// <summary>The settings as they were last read.</summary>
    public ReactiveThemeOptions Options => _engine.Options;

    /// <summary>
    /// True while the theme is following the music: the setting is on, Windows is not asking for reduced
    /// motion, and no high-contrast theme is in force.
    /// </summary>
    public bool Active
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>Why the theming is not running, or null while it is. Empty-string-free, for a diagnostics line.</summary>
    public string? StoppedBecause { get; private set; } = "not started";

    /// <summary>
    /// The clock reading at which the theming last stopped, and the one at which the switch that stopped it was
    /// seen. Both from the injected <see cref="TimeProvider"/>, so AC-127 can be measured rather than slept at.
    /// </summary>
    public long? StoppedAt { get; private set; }

    /// <summary>How many ticks have been served since the controller was created.</summary>
    public long Ticks { get; private set; }

    /// <summary>How many of those ticks painted a palette.</summary>
    public long Applied { get; private set; }

    /// <summary>
    /// How many painted palettes reached the visualizer through <c>mp_renderer_set_theme</c>. It tracks
    /// <see cref="Applied"/> while a renderer is attached and stops dead when one is not, which is what makes
    /// "the theme is reaching the presets" checkable rather than assumed.
    /// </summary>
    public long RendererPushes { get; private set; }

    /// <summary>
    /// How many were not told: no renderer was passed, the renderer is detached, or it refused the call. Never
    /// a throw out of <see cref="Tick"/>, because this runs on a timer thread thirty times a second.
    /// </summary>
    public long RendererSkips { get; private set; }

    /// <summary>Why the visualizer is not being told, or null while it is. For the diagnostics overlay.</summary>
    public string? RendererProblem { get; private set; }

    /// <summary>
    /// Why the poll stopped, when it has; null while it is running. Nothing is expected to land here - the
    /// renderer's own refusals are handled and counted inside the tick - so anything that does is a fault, and
    /// keeping it is what stops a timer callback from repeating a failure nothing will fix.
    /// </summary>
    public Exception? TickFailure { get; private set; }

    /// <summary>The current track's art palette, blended into the hue. Null for a track without one.</summary>
    public void SetArtPalette(ArtPalette? palette)
    {
        lock (_gate)
        {
            _engine.SetArtPalette(palette);
        }
    }

    /// <summary>Starts the 30 Hz tick. A test drives <see cref="Tick"/> itself and never calls this.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _timer ??= _time.CreateTimer(_ => TickSafely(), null, PollInterval, PollInterval);
        }

        Evaluate();
    }

    /// <summary>
    /// The timer's callback: <see cref="Tick"/> with nothing allowed out of it (T-159). A fault stops the poll
    /// and keeps its reason, rather than repeating thirty times a second - or, before this existed, taking the
    /// process down on the first one. Stopping the poll does not disarm the accessibility switches:
    /// <see cref="IAccessibilitySignals.Changed"/> still calls <see cref="Evaluate"/>, so reduced motion keeps
    /// its fast path and loses only its bound.
    /// </summary>
    private void TickSafely()
    {
        try
        {
            Tick();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TickFailure = ex;
            Serilog.Log.Error(ex, "Audio-reactive theming stopped: its poll threw");
            lock (_gate)
            {
                _ = _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            _sink.Clear();
        }
    }

    /// <summary>
    /// One poll: re-read the switches, take whatever the analysis has, fold in the time since the last tick and
    /// paint. Safe to call from any thread; the sink is called on the caller's.
    /// </summary>
    public void Tick()
    {
        ReactiveThemePalette? painted = null;
        ReactiveThemePalette? resting = null;
        bool stopped = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Ticks++;
            long now = _time.GetTimestamp();
            TimeSpan elapsed = _time.GetElapsedTime(_lastTick, now);
            _lastTick = now;

            if (!Allowed(out string? because))
            {
                stopped = StopLocked(because!);
                // Read under the lock: the engine is not thread-safe and this runs on the timer thread.
                resting = _engine.Resting;
            }
            else
            {
                if (!_running)
                {
                    StartLocked();
                }

                AnalysisFrame? frame = null;
                if (_frames.TryGetLatest(out AnalysisFrame latest)
                    && (!_haveSequence || latest.Sequence != _lastSequence))
                {
                    _lastSequence = latest.Sequence;
                    _haveSequence = true;
                    frame = latest;
                }

                painted = _engine.Advance(frame, elapsed);
                Applied++;
            }
        }

        if (painted is { } palette)
        {
            _sink.Apply(palette);
            PushToRenderer(palette);
        }
        else if (stopped)
        {
            _sink.Clear();
            // The visualizer is told to stop too. Without this the presets keep whatever palette was last sent -
            // the renderer holds the theme across a preset switch by design - so the window would go back to its
            // static colours and the visualizer inside it would stay frozen on the last chord, which is the
            // two-palettes-in-one-window failure this class exists to avoid.
            PushToRenderer(resting!);
        }
    }

    /// <summary>
    /// Re-reads the switches now, without waiting for a tick, and stops or starts accordingly. This is what
    /// makes reduced motion take effect when it is thrown rather than at the next frame - which matters most
    /// when there is no next frame, because nothing is playing.
    /// </summary>
    public void Evaluate()
    {
        bool stopped;
        ReactiveThemePalette resting;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // The window calls this when its theme has actually changed (ActualThemeChanged). ui.theme reached
            // OnSettingChanged a dispatcher turn earlier, while the window still had the old theme, so the engine there
            // was kept for the theme being left; this is where it catches up (T-69 review: with music playing, the
            // gradient behind the controls bar stayed the light theme's after Light then Dark).
            bool dark = IsDark();
            if (_engine.DarkTheme != dark)
            {
                _engine = new ReactiveThemeEngine(dark, _engine.Options);
            }

            if (Allowed(out string? because))
            {
                if (!_running)
                {
                    StartLocked();
                }

                return;
            }

            stopped = StopLocked(because!);
            resting = _engine.Resting;
        }

        if (stopped)
        {
            _sink.Clear();
            PushToRenderer(resting);
        }
    }

    /// <summary>Stops the tick, puts the static theme back and unsubscribes.</summary>
    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            timer = _timer;
            _timer = null;
            _running = false;
        }

        _settings.Changed -= OnSettingChanged;
        _accessibility.Changed -= OnAccessibilityChanged;
        timer?.Dispose();
        _sink.Clear();
    }

    /// <summary>
    /// The palette into every preset's constant buffer, when there is a renderer attached to take it. Called on
    /// the timer thread thirty times a second, so it must not throw and must not log per tick: the reason is
    /// carried on <see cref="RendererProblem"/> and the count on <see cref="RendererSkips"/>, and a change of
    /// reason is logged once.
    /// </summary>
    private void PushToRenderer(ReactiveThemePalette palette)
    {
        if (_renderer is not { IsAttached: true } renderer)
        {
            Skipped(_renderer is null ? "no renderer was passed" : "the renderer is not attached");
            return;
        }

        try
        {
            renderer.SetThemeColors(palette.ToThemeColors());
            RendererPushes++;
            if (RendererProblem is not null)
            {
                RendererProblem = null;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // Detached between the check and the call - the window is closing - or the core refused it;
            // NativeException is an InvalidOperationException, so both land here.
            Skipped(e.Message);
        }
    }

    private void Skipped(string because)
    {
        RendererSkips++;
        if (string.Equals(RendererProblem, because, StringComparison.Ordinal))
        {
            return;
        }

        RendererProblem = because;
        Serilog.Log.Debug("The reactive theme is not reaching the visualizer: {Because}", because);
    }

    private bool Allowed(out string? because)
    {
        // Read in the order the contract states them, so the reason given is the first one that applies.
        if (!_engine.Options.Enabled)
        {
            because = "ui.reactiveTheming is off";
            return false;
        }

        if (!_accessibility.AnimationsEnabled)
        {
            because = "Windows is asking for reduced motion";
            return false;
        }

        if (_accessibility.HighContrast)
        {
            because = "a high-contrast theme is in force";
            return false;
        }

        because = null;
        return true;
    }

    private void StartLocked()
    {
        _running = true;
        StoppedBecause = null;
        StoppedAt = null;
        _haveSequence = false;
        _engine.Reset();
    }

    private bool StopLocked(string because)
    {
        StoppedBecause = because;
        if (!_running)
        {
            return false;
        }

        _running = false;
        StoppedAt = _time.GetTimestamp();
        _engine.Reset();
        return true;
    }

    private void OnAccessibilityChanged(object? sender, EventArgs e) => Evaluate();

    private void OnSettingChanged(object? sender, string key)
    {
        if (key is not (SettingsKeys.UiReactiveTheming or SettingsKeys.UiReactiveSmoothing or SettingsKeys.UiTheme))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ReactiveThemeOptions options = ReactiveThemeOptions.Read(_settings);
            bool dark = IsDark();

            // The theme decides which text the contrast guarantee is made against, so a theme change is a new
            // engine rather than a new option: the colours it has been smoothing toward are the other theme's.
            if (_engine.DarkTheme == dark)
            {
                _engine.Options = options;
            }
            else
            {
                _engine = new ReactiveThemeEngine(dark, options);
            }
        }

        Evaluate();
    }
}
