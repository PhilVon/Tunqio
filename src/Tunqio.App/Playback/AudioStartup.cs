using Microsoft.Extensions.Logging;
using Tunqio.Core;
using Tunqio.Core.Audio;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Visualization;
using Tunqio.Interop;

namespace Tunqio.App.Playback;

/// <summary>
/// Brings audio up after the first frame and takes it down when the window closes (docs/solution-structure.md,
/// start-up steps 3a-3d and the shutdown sequence). Creating the engine loads mpcore.dll and the BASS add-ons, and
/// opening the output touches WASAPI, so none of it belongs on the path to the first frame.
/// </summary>
/// <remarks>
/// <para>
/// Every failure here is survivable and none of them stops the launch: the library, search and browsing are worth
/// having without audio. A remembered device that has gone falls back to the system default; an output that will not
/// open at all falls back to the default shared device; an engine that cannot be created (mpcore missing, ABI
/// refused) leaves <see cref="Session"/> null and <see cref="AppPlaybackCommands"/> logs the requests it cannot
/// serve. Each of those produces a <see cref="StartupNotice"/> for the window's InfoBar.
/// </para>
/// <para>
/// Disposal is synchronous because the DI container's own disposal is: it runs the session's async teardown — which
/// is what writes the queue and position back — on the thread pool and waits a bounded time for it, rather than
/// pumping the UI thread.
/// </para>
/// </remarks>
public sealed class AudioStartup : IPlaybackSessionSource, IDisposable
{
    /// <summary>How long the window's close waits for the queue to be written and the device released.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    private readonly ITrackRepository _tracks;
    private readonly IPlayHistoryRepository _history;
    private readonly IQueueStateRepository _queues;
    private readonly ISettingsStore _settings;
    private readonly ILogger<AudioStartup> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Func<IAudioEngine> _createEngine;
    private IAudioEngine? _engine;
    private bool _disposed;

    /// <param name="createEngine">
    /// How the engine is made; the default loads mpcore. A test passes a fake, because the fallbacks below are the
    /// part of a launch nobody can reproduce on demand — a device that has gone, a driver that refuses.
    /// </param>
    public AudioStartup(
        ITrackRepository tracks,
        IPlayHistoryRepository history,
        IQueueStateRepository queues,
        ISettingsStore settings,
        ILogger<AudioStartup> logger,
        ILoggerFactory? loggerFactory = null,
        Func<IAudioEngine>? createEngine = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(queues);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        _tracks = tracks;
        _history = history;
        _queues = queues;
        _settings = settings;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _createEngine = createEngine ?? (() => NativeAudioEngine.Create());
    }

    /// <summary>The session, once <see cref="StartAsync"/> has succeeded; null before that and when audio is unavailable.</summary>
    public PlaybackSession? Session { get; private set; }

    /// <summary>
    /// The analysis stream over this session's engine, for audio-reactive theming (E4-S6) and the visualizer.
    /// Null until <see cref="StartAsync"/> has produced an engine, and null for a fake one: a test engine has no
    /// native analysis thread behind it, and the theming is tested against synthetic frames rather than this.
    /// </summary>
    public IAnalysisFrameSource? AnalysisFrames { get; private set; }

    /// <summary>
    /// The <c>mp_engine</c> behind this session, for the one caller that needs the handle itself rather than a
    /// wrapper: <c>mp_renderer_create</c> takes the engine the visualizer draws from (T-179).
    /// <see cref="nint.Zero"/> when audio did not start, or when the engine is a test fake.
    /// </summary>
    /// <remarks>
    /// This is deliberately not an <see cref="IAnalysisFrameSource"/>. The theming reads analysis on the managed
    /// side and can take a stream; the renderer polls the engine itself, on its own thread, and the ABI hands it
    /// an <c>mp_engine*</c> at creation. Handing the shell an nint is the honest shape of that, and the same
    /// shape the SwapChainPanel's IUnknown already travels in.
    /// </remarks>
    public nint NativeEngineHandle { get; private set; }

    /// <inheritdoc />
    public event EventHandler<PlaybackSession>? SessionReady;

    /// <summary>True once <see cref="StartAsync"/> has run, whether or not it produced a session.</summary>
    public bool Started { get; private set; }

    /// <summary>
    /// Creates the engine, opens the output the settings resolve to and restores the saved queue. Returns the notice
    /// the window should show, or null when everything opened as asked. Safe to call once; later calls do nothing.
    /// </summary>
    public async Task<StartupNotice?> StartAsync(CancellationToken ct = default)
    {
        if (Started || _disposed)
        {
            return null;
        }

        Started = true;
        IAudioEngine engine;
        try
        {
            engine = _createEngine();
        }
        catch (Exception e) when (e is NativeException or NativeAbiMismatchException
            or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            _logger.LogError(e, "The audio engine could not be created; this session has no playback");
            return new StartupNotice(
                "Audio unavailable",
                "The audio engine could not be loaded, so nothing will play this session. Your library is still browsable. " +
                "Reinstalling Tunqio replaces the missing or mismatched audio components.",
                StartupNoticeSeverity.Error);
        }

        _engine = engine;
        StartupNotice? notice = await OpenOutputAsync(engine, ct).ConfigureAwait(false);
        if (notice is { Severity: StartupNoticeSeverity.Error })
        {
            _engine = null;
            await engine.DisposeAsync().ConfigureAwait(false);
            return notice;
        }

        if (engine is NativeAudioEngine native)
        {
            AnalysisFrames = new NativeAnalysisFrameSource(native.Native);
            NativeEngineHandle = native.Native.Handle;
        }

        var session = new PlaybackSession(
            engine, _tracks, _history, _queues, _settings, logger: _loggerFactory?.CreateLogger<PlaybackSession>());
        Session = session;
        await RestoreAsync(ct).ConfigureAwait(false);
        // After the restore, so the first snapshot anything sees already has the queue the user left behind in it.
        SessionReady?.Invoke(this, session);
        return notice;
    }

    /// <summary>
    /// Opens the device the <c>output.*</c> settings name, and falls back rather than failing the launch: a remembered
    /// device that is gone, or a device that refuses to open, becomes the system default in shared mode. Exclusive is
    /// only ever a request and the core downgrades it on its own (docs/decisions.md), so the mode is not retried here.
    /// </summary>
    private async Task<StartupNotice?> OpenOutputAsync(IAudioEngine engine, CancellationToken ct)
    {
        IReadOnlyList<OutputDevice> devices;
        try
        {
            devices = engine.EnumerateDevices();
        }
        catch (NativeException e)
        {
            _logger.LogWarning(e, "Output devices could not be enumerated; trying the system default");
            devices = [];
        }

        OutputSelection selection = OutputPolicy.Resolve(_settings, devices);
        try
        {
            await engine.InitializeAsync(selection.Config, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Output open on {Device} ({Mode}, {BufferMs} ms buffer)",
                selection.Device?.Name ?? "the system default",
                OutputPolicy.ModeName(selection.Config.Mode),
                selection.Config.BufferMs);

            return selection.RequestedDeviceMissing
                ? new StartupNotice(
                    "Audio device not found",
                    "The output device you chose is not connected, so Tunqio is playing through the system default. " +
                    "Choose it again in Settings › Output when it is back.",
                    StartupNoticeSeverity.Warning)
                : null;
        }
        catch (NativeException e)
        {
            _logger.LogWarning(
                e, "Output {Device} would not open; falling back to the system default in shared mode", selection.Device?.Name ?? "default");
        }

        var fallback = new OutputConfig(OutputConfig.DefaultDevice, OutputMode.Shared, OutputPolicy.DefaultBufferMs(OutputMode.Shared));
        try
        {
            await engine.InitializeAsync(fallback, ct).ConfigureAwait(false);
            return new StartupNotice(
                "Audio device unavailable",
                "The output you chose would not open, so Tunqio is playing through the system default in shared mode. " +
                "Choose a device again in Settings › Output.",
                StartupNoticeSeverity.Warning);
        }
        catch (NativeException e)
        {
            _logger.LogError(e, "No audio output could be opened; this session has no playback");
            return new StartupNotice(
                "Audio unavailable",
                "No audio device could be opened, so nothing will play this session. Your library is still browsable. " +
                "Connect an output device and restart Tunqio.",
                StartupNoticeSeverity.Error);
        }
    }

    /// <summary>
    /// Start-up step 3d: bring the saved queue back, paused at where the user was when <c>playback.resumeOnLaunch</c>
    /// is on. A queue that will not load is not worth failing a launch over — the user gets an empty transport.
    /// </summary>
    private async Task RestoreAsync(CancellationToken ct)
    {
        try
        {
            bool restored = await Session!.RestoreAsync(ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Queue restore: {Restored}, {Count} item(s)", restored ? "restored" : "nothing saved", Session.Queue.Items.Count);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogError(e, "The saved queue could not be restored; starting with an empty one");
        }
    }

    /// <summary>
    /// Shutdown steps 1-3: the session's teardown writes the queue and position back, closes the open handles and
    /// releases the device. It runs on the thread pool with a cap on the wait, so a driver that will not let go
    /// cannot hold the window open.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        PlaybackSession? session = Session;
        IAudioEngine? engine = _engine;
        // Before the engine: the frame source polls it, and polling a destroyed engine is the one way this can
        // touch a handle after its owner has gone.
        (AnalysisFrames as IDisposable)?.Dispose();
        AnalysisFrames = null;
        Session = null;
        _engine = null;
        if (session is null && engine is null)
        {
            return;
        }

        Task teardown = Task.Run(async () =>
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }

            if (engine is not null)
            {
                await engine.DisposeAsync().ConfigureAwait(false);
            }
        });

        try
        {
#pragma warning disable VSTHRD002 // The window is closing and the container is disposing synchronously; the wait is capped and the work is on the pool.
            if (!teardown.Wait(ShutdownTimeout))
#pragma warning restore VSTHRD002
            {
                _logger.LogWarning("Audio teardown did not finish within {Timeout}; closing anyway", ShutdownTimeout);
            }
        }
        catch (AggregateException e)
        {
            _logger.LogError(e, "Audio teardown failed");
        }
    }
}
