using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Playback;
using Tunqio.Core.Audio;
using Tunqio.Core.Playback;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Shell;

/// <summary>
/// The diagnostics overlay's state (E2-S8): what the numbers are, and the fact that they keep moving while it is
/// open. Everything it says is <see cref="Diagnostics"/>'s; what is here is the refresh and the toggle.
/// </summary>
/// <remarks>
/// The refresh runs only while the overlay is shown. It reads the engine and the renderer directly rather than
/// waiting for a snapshot, because half of what it reports — frame time, callback counts — is not on one; and it
/// runs at 2 Hz rather than at the snapshot's 10 Hz, because these are numbers a person reads rather than a
/// position a thumb follows, and a value that changes ten times a second cannot be read at all.
/// </remarks>
public sealed partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    /// <summary>How often the values are re-read while the overlay is open.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

    private readonly SynchronizationContext? _ui;
    private readonly IPlaybackSessionSource? _source;
    private readonly Func<RenderStats?> _renderer;
    private readonly Func<ReactiveThemeStatus?> _theming;
    private readonly Func<bool?> _rendererAudioSource;
    private readonly string? _build;
    private readonly TimeProvider _time;
    private ITimer? _timer;
    private bool _disposed;

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    /// <param name="source">Where the session comes from; without one the overlay says the engine is unavailable.</param>
    /// <param name="renderer">Reads the renderer's statistics; it may not exist, and returns null when it does not.</param>
    /// <param name="build">The version string for the Build section.</param>
    /// <param name="ui">The XAML thread's context. Null runs updates inline, which is what the tests want.</param>
    /// <param name="clock">Drives the refresh; a fake clock is how a test advances it.</param>
    /// <param name="theming">
    /// Reads audio-reactive theming (T-155). A delegate for the same reason <paramref name="renderer"/> is one:
    /// the controller does not exist until the audio engine is up, and on a machine with no sound never will.
    /// </param>
    public DiagnosticsViewModel(
        IPlaybackSessionSource? source,
        Func<RenderStats?>? renderer = null,
        string? build = null,
        SynchronizationContext? ui = null,
        TimeProvider? clock = null,
        Func<ReactiveThemeStatus?>? theming = null,
        Func<bool?>? rendererAudioSource = null)
    {
        _source = source;
        _renderer = renderer ?? (() => null);
        _theming = theming ?? (() => null);
        _rendererAudioSource = rendererAudioSource ?? (() => null);
        _build = build;
        _ui = ui;
        _time = clock ?? TimeProvider.System;
        Refresh();
    }

    /// <summary>The overlay's contents, replaced whole on every refresh.</summary>
    public ObservableCollection<DiagnosticsSection> Sections { get; } = [];

    /// <summary>The same thing as text, which is what the Copy button puts on the clipboard.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Why there is no renderer, when there is not; the shell sets it if the surface would not come up.</summary>
    public string? RendererProblem { get; set; }

    /// <summary>Shows or hides the overlay — what Ctrl+Shift+D does.</summary>
    public void Toggle() => IsVisible = !IsVisible;

    /// <summary>Re-reads every value. Called by the timer, and once at construction so the first frame is not blank.</summary>
    public void Refresh()
    {
        PlaybackSession? session = _source?.Session;
        IReadOnlyList<DiagnosticsSection> sections = Diagnostics.Describe(
            session?.Current, ReadEngineStats(session), Read(), _build, RendererProblem, ReadTheming(), ReadAudioSource(), ReadClock(session));

        Sections.Clear();
        foreach (DiagnosticsSection section in sections)
        {
            Sections.Add(section);
        }

        Text = Diagnostics.Report(sections);
        OnPropertyChanged(nameof(Text));
    }

    /// <summary>
    /// The renderer's statistics, or null. The renderer is native and can be mid-teardown when the window closes,
    /// so a throw here must not take the overlay — or the window's own stats timer — with it.
    /// </summary>
    private RenderStats? Read()
    {
        try
        {
            return _renderer();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Serilog.Log.Debug(e, "The renderer could not be read for the diagnostics overlay");
            return null;
        }
    }

    /// <summary>
    /// The theming's state, or null. Read behind the same net as the renderer: the controller ticks on a timer
    /// thread and the layer on the UI one, and a torn read here must not take the overlay down.
    /// </summary>
    private ReactiveThemeStatus? ReadTheming()
    {
        try
        {
            return _theming();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Serilog.Log.Debug(e, "The reactive theming could not be read for the diagnostics overlay");
            return null;
        }
    }

    /// <summary>
    /// The engine's output statistics, or null. <c>mp_engine_get_stats</c> is a native call that throws on a refusal
    /// or a closed handle, and <see cref="Refresh"/> runs from a timer: an exception out of it reaches the XAML
    /// unhandled-exception handler, which logs and does not recover, so an unguarded read here ended the process (T-159).
    /// </summary>
    /// <summary>The engine's clock, for the live buffer depth; guarded for the same reason as <see cref="ReadEngineStats"/>.</summary>
    private static PlaybackClock? ReadClock(PlaybackSession? session)
    {
        try
        {
            return session?.EngineClock;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Serilog.Log.Debug(e, "Diagnostics could not read the engine clock");
            return null;
        }
    }

    private static EngineStats? ReadEngineStats(PlaybackSession? session)
    {
        try
        {
            return session?.EngineStats;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Serilog.Log.Debug(e, "The engine statistics could not be read for the diagnostics overlay");
            return null;
        }
    }

    /// <summary>Whether the visualizer has an engine behind it (T-179); null when nobody can say.</summary>
    private bool? ReadAudioSource()
    {
        try
        {
            return _rendererAudioSource();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Serilog.Log.Debug(e, "The visualizer's audio source could not be read for the diagnostics overlay");
            return null;
        }
    }

    partial void OnIsVisibleChanged(bool value)
    {
        _timer?.Dispose();
        _timer = null;
        if (!value || _disposed)
        {
            return;
        }

        Refresh();
        _timer = _time.CreateTimer(_ => Post(Refresh), null, RefreshInterval, RefreshInterval);
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
            return;
        }

        // No JoinableTaskFactory in this app; the context is the XAML thread's DispatcherQueue one and Post never blocks the caller.
#pragma warning disable VSTHRD001
        _ui.Post(_ => action(), null);
#pragma warning restore VSTHRD001
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }
}
