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
    /// <param name="renderer">The visualizer, if one is attached; null simply leaves the presets untold.</param>
    /// <param name="time">Injected so a test owns the clock; <see cref="TimeProvider.System"/> otherwise.</param>
    public ReactiveThemeController(
        IAnalysisFrameSource frames,
        ISettingsStore settings,
        IAccessibilitySignals accessibility,
        IReactiveThemeSink sink,
        Func<bool> isDark,
        IVisualizationHost? renderer = null,
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
            _timer ??= _time.CreateTimer(_ => Tick(), null, PollInterval, PollInterval);
        }

        Evaluate();
    }

    /// <summary>
    /// One poll: re-read the switches, take whatever the analysis has, fold in the time since the last tick and
    /// paint. Safe to call from any thread; the sink is called on the caller's.
    /// </summary>
    public void Tick()
    {
        ReactiveThemePalette? painted = null;
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
            _renderer?.SetThemeColors(palette.ToThemeColors());
        }
        else if (stopped)
        {
            _sink.Clear();
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
        lock (_gate)
        {
            if (_disposed)
            {
                return;
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
        }

        if (stopped)
        {
            _sink.Clear();
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
