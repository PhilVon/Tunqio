using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using Tunqio.App.Activation;
using Tunqio.App.JumpLists;
using Tunqio.App.Library;
using Tunqio.App.Notifications;
using Tunqio.App.Playback;
using Tunqio.App.Shell;
using Tunqio.App.Tray;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Visualization;
using Tunqio.Library;
using Tunqio.Library.Database;
using Tunqio.Library.Playlists;

namespace Tunqio.App;

/// <summary>
/// Application entry. Start-up order per docs/solution-structure.md: bootstrap logger and exception handlers,
/// build the generic host (paths, settings, logging), then the window. Shutdown flushes settings and logs.
/// The engine, library and windows-integration services join the host in their stories. Nothing here is
/// <c>async void</c>: start-up and shutdown are synchronous and short; long work moves to hosted services.
/// </summary>
public partial class App : Application
{
    /// <summary>Identifies this process's lines in the shared daily log file.</summary>
    public static readonly Guid SessionId = Guid.NewGuid();

    private IHost? _host;
    private Window? _window;
    private SmtcBridge? _mediaControls;
    private TrayController? _tray;
    private ToastController? _toasts;
    private JumpListController? _jumpList;
    private bool _hiddenOnMinimise;
    private static nint _mainWindowHandle;

    public App()
    {
        InitializeComponent();
        // T-188: all three, so an exception that ends the process says why in the app's own log - including one raised
        // during shutdown, which is where 47 crashes went unrecorded.
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.CloseAndFlush();
    }

    /// <summary>The main window's HWND for pickers and dialogs that need an owner; zero before the window exists.</summary>
    public static nint MainWindowHandle => _mainWindowHandle;

    /// <summary>The application's service provider, available after <see cref="OnLaunched"/>.</summary>
    public static IServiceProvider Services => ServicesIfRunning
        ?? throw new InvalidOperationException("The host has not started yet, or has already shut down.");

    /// <summary>
    /// The service provider while the host is running; null before start-up and once shutdown has begun disposing it.
    /// For XAML callbacks that can arrive after the window has closed - a page's Unloaded among them (T-188) - and must
    /// do nothing then rather than throw on the XAML thread, which ends the process with a stowed exception.
    /// </summary>
    public static IServiceProvider? ServicesIfRunning => (Current as App)?._host?.Services;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Stopwatch startup = Stopwatch.StartNew();
        string[] commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
        // --data-root PATH (E6-S6): a scratch profile for a harness, read before anything is opened under the default one. Toasts
        // carry it in their presses (E7-S4), so a press that has to start Tunqio again starts it on the same root.
        string? dataRootSwitch = DataRootSwitch.Path(commandLine);
        var paths = dataRootSwitch is { } dataRoot ? new AppPaths(dataRoot) : new AppPaths();
        paths.EnsureCreated();

        Log.Logger = AppLogging.Create(paths, SessionId);

        // Captured here rather than inside ConfigureServices: OnLaunched is the XAML thread, and the registrations
        // that hand it on must not be at the mercy of which thread the host happens to build the collection on.
        SynchronizationContext? ui = SynchronizationContext.Current;
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services => services.AddTunqio(paths, ui))
            .Build();
        _host.Start();
        Controls.AlbumArt.Cache = _host.Services.GetService<IArtCache>();

        ILogger<App> logger = _host.Services.GetRequiredService<ILogger<App>>();
        StartupNotice? databaseNotice = OpenLibraryDatabase(logger);
        ISettingsStore settings = _host.Services.GetRequiredService<ISettingsStore>();
        int launchCount = settings.GetValue(SettingsKeys.AppLaunchCount, 0) + 1;
        string? previousSession = settings.GetValue<string?>(SettingsKeys.AppLastSessionId, null);
        settings.SetValue(SettingsKeys.AppLaunchCount, launchCount);
        settings.SetValue(SettingsKeys.AppLastLaunchUtc, DateTimeOffset.UtcNow);
        settings.SetValue(SettingsKeys.AppLastSessionId, SessionId.ToString());
        settings.Flush();

        logger.LogInformation(
            "{Product} {Version} session {SessionId} started: launch #{LaunchCount}, previous session {PreviousSession}, data root {DataRoot}",
            Identity.ProductName, typeof(App).Assembly.GetName().Version?.ToString(3), SessionId, launchCount,
            previousSession ?? "none", paths.DataRoot);

        if (LibrarySpikeRunner.IsRequested(commandLine))
        {
            // E3-S3 measurement mode over a chosen database; the shell window is not shown.
            var spike = new LibrarySpikeRunner(logger, commandLine, paths.DatabasePath, Path.Combine(paths.LogsDirectory, "library-spike.json"));
            _window = spike.CreateWindow();
            _window.Closed += OnWindowClosed;
            _window.Activate();
            spike.Start();
            return;
        }

        // T-157: the stored preset parameters listen to the visualizer surface for PresetChanged, and the first of those is
        // raised by the window's own AttachAsync. Resolved here, before the window exists, so the launch's preset gets its
        // stored values; resolved later (with the settings page, say) it would miss every switch before the page opened.
        _ = _host.Services.GetRequiredService<PresetParameterMemory>();

        // Every argument below is required (T-180): MainWindow's collaborators carry no defaults, so this - the
        // single place the application is assembled - cannot drop one and still compile. That is the whole
        // mechanism behind T-156 and T-179, both of which were one omitted argument on a line like this.
        var window = new MainWindow(
            RenderSpikeRunner.WantsWarp(commandLine),
            settings,
            _host.Services.GetRequiredService<IPlaybackSessionSource>(),
            _host.Services.GetRequiredService<ILibraryNavigator>(),
            _host.Services.GetRequiredService<OpenCoordinator>(),
            _host.Services.GetRequiredService<Tunqio.Core.Library.ITrackRepository>(),
            _host.Services.GetRequiredService<Library.LibraryScanCoordinator>(),
            // The container's notices, not a second set: the tag editor dialog resolves this same object to leave
            // the Undo bar behind (E3-S10, flow 8), and the panel this window shows is bound to what it is given.
            _host.Services.GetRequiredService<ShellNotices>(),
            // The container's visualizer surface: the window attaches it to the panel and Settings >
            // Visualization (E4-S9) drives the same object, so a second one would switch a preset on a
            // renderer nobody is looking at.
            _host.Services.GetRequiredService<IVisualizationHost>(),
            // The album art cache (T-147): the same one the panel draws from, so the glow's colours and the
            // picture above it come out of one decode.
            _host.Services.GetService<IArtCache>(),
            // The mode (E5-S1): the container's one, which reads and writes ui.mode.
            _host.Services.GetRequiredService<ShellState>(),
            // The first-run welcome (E6-S6), over the settings pages' own view models so its choices are theirs. Given the
            // launch count as it was before this launch counted itself: whether it shows is its decision (the once-only
            // rule and the existing-profile guard are on FirstRunWelcomeViewModel.ShouldShow).
            new FirstRunWelcomeViewModel(
                settings,
                _host.Services.GetRequiredService<Tunqio.Core.Library.ILibraryFolderRepository>(),
                _host.Services.GetRequiredService<LibrarySettingsViewModel>(),
                _host.Services.GetRequiredService<OutputSettingsViewModel>(),
                _host.Services.GetRequiredService<AppearanceSettingsViewModel>(),
                _host.Services.GetRequiredService<ILibraryFolderPicker>(),
                _host.Services.GetRequiredService<IPlaybackSessionSource>(),
                launchCount - 1,
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) is { Length: > 0 } music ? music : null),
            // The rater (E6-S7): the container's one, whose events the library pages also follow.
            _host.Services.GetRequiredService<Tunqio.Core.Library.ITrackRater>());
        _window = window;
        _mainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _mediaControls = StartMediaControls(logger);
        _tray = StartTray(window, settings, logger);
        _toasts = StartToasts(settings, dataRootSwitch, logger);
        logger.LogInformation("Shell backdrop: {Backdrop}", window.ApplyBackdrop());
        _window.Closed += OnWindowClosed;
        if (databaseNotice is not null)
        {
            window.ShowNotice(databaseNotice);
        }

        _window.Activate();
        logger.LogInformation("Main window shown after {ElapsedMs} ms", startup.ElapsedMilliseconds);
        _ = StartAudioAsync(window, logger, commandLine);
        _ = StartLibraryWatcherAsync(logger);
        StartActivationRouting(window, logger);
        _jumpList = StartJumpList(logger);

        if (RenderSpikeRunner.IsRequested(commandLine))
        {
            new RenderSpikeRunner(window, logger, commandLine, Path.Combine(paths.LogsDirectory, "render-spike.json")).Start();
        }

        if (NowPlayingSpikeRunner.IsRequested(commandLine))
        {
            // E2-S3 measurement mode: 1000 px art through the panel's own binding, against the UI thread's frames.
            new NowPlayingSpikeRunner(
                window, window.NowPlayingPanelControl, logger, commandLine,
                Path.Combine(paths.LogsDirectory, "nowplaying-spike.json")).Start();
        }

        if (ShellSpikeRunner.IsRequested(commandLine))
        {
            // E2-S1 measurement mode: resize through the documented widths and report what the panels came out at.
            new ShellSpikeRunner(window, logger, commandLine, Path.Combine(paths.LogsDirectory, "shell-spike.json")).Start();
        }
    }

    /// <summary>
    /// Start-up steps 2d and 3e (docs/solution-structure.md), E7-S1: this launch's own arguments, and every activation a
    /// later process redirects here (<see cref="Program"/>), go through one <see cref="CommandRouter"/> on the XAML thread.
    /// The router waits for audio itself, so a file opened from Explorer on a cold start plays once the engine is up.
    /// </summary>
    private void StartActivationRouting(MainWindow window, ILogger<App> logger)
    {
        try
        {
            var target = new SessionCommandTarget(
                _host!.Services.GetRequiredService<IPlaybackSessionSource>(),
                _host.Services.GetRequiredService<OpenFilesService>(),
                // Jump list items name library ids (E7-S5).
                _host.Services.GetRequiredService<ITrackRepository>(),
                _host.Services.GetRequiredService<IPlaylistRepository>(),
                BringMainWindowToForeground,
                SessionCommandTarget.SessionWait,
                _host.Services.GetRequiredService<ILogger<SessionCommandTarget>>());
            var router = new CommandRouter(target, _host.Services.GetRequiredService<ILogger<CommandRouter>>());
            Microsoft.UI.Dispatching.DispatcherQueue dispatcher = window.DispatcherQueue;
            _ = router.RouteAsync(Program.LaunchTokens, Environment.CurrentDirectory, emptyMeansShow: false);
            Program.Inbox.Attach(tokens =>
            {
                if (!dispatcher.TryEnqueue(() => _ = router.RouteAsync(tokens, workingDirectory: null, emptyMeansShow: true)))
                {
                    Log.Warning("Activation dropped: the window is closing");
                }
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            logger.LogError(e, "Activation routing did not start; files and tunqio:// links opened while running are ignored this session");
        }
    }

    /// <summary>
    /// The taskbar jump list (E7-S5, ADR-006): <see cref="JumpListController"/> over <see cref="WinRtJumpList"/>, started after the
    /// window is shown with its first refresh. The API needs package identity, so the unpackaged build skips it with this one line
    /// and never touches the API (Q-119); the installed package is where it runs (E8-S1). Its items are <c>tunqio://track</c> and
    /// <c>tunqio://playlist</c> commands, which reach <see cref="CommandRouter"/> like every other activation. A machine that will not
    /// give a jump list costs the jump list, not the launch.
    /// </summary>
    private JumpListController? StartJumpList(ILogger<App> logger)
    {
        try
        {
            if (WinRtJumpList.WhyUnavailable() is { } reason)
            {
                logger.LogInformation("Jump list: skipped, {Reason}; the taskbar jump list needs the installed package (E8-S1)", reason);
                return null;
            }

            var controller = new JumpListController(
                _host!.Services.GetRequiredService<IPlaybackSessionSource>(),
                _host.Services.GetRequiredService<ITrackRepository>(),
                _host.Services.GetRequiredService<IPlaylistRepository>(),
                new WinRtJumpList(),
                TimeProvider.System,
                _host.Services.GetRequiredService<ILogger<JumpListController>>());
            controller.Start();
            logger.LogInformation("Jump list: controller started; the first write follows in {Window}", JumpListController.CoalesceWindow);
            return controller;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            logger.LogError(e, "Jump list unavailable; recent tracks and pinned playlists will not reach the taskbar this session");
            return null;
        }
    }

    /// <summary>
    /// Restores and raises the main window, on the XAML thread whichever thread asks: a redirected activation (E7-S1) or Show
    /// from the tray (E7-S3). A window hidden to the tray is shown again first, and one hidden behind the mini player gets the
    /// mini player closed, which is the mini player's own way back to it.
    /// </summary>
    private void BringMainWindowToForeground()
    {
        if (_window is not { } window)
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (_host is null)
            {
                return;
            }

            if (window is MainWindow main)
            {
                main.ReturnFromHidden();
            }

            window.Activate();
            bool raised = NativeWindowing.BringToForeground(_mainWindowHandle);
            Log.Information("Activation: main window activated (foreground granted {Raised})", raised);
        });
    }

    /// <summary>
    /// Start-up step 2b (docs/solution-structure.md): open library.db and apply migrations. A damaged file has
    /// already been moved aside by <see cref="LibraryDatabase.Open(IAppPaths, TimeProvider?, ILogger?)"/>; the
    /// user is told. A database from a newer build is refused and the app runs without a library rather than
    /// touching the file (docs/library-and-data.md, "Failure handling").
    /// </summary>
    private StartupNotice? OpenLibraryDatabase(ILogger<App> logger)
    {
        try
        {
            LibraryDatabase database = _host!.Services.GetRequiredService<LibraryDatabase>();
            if (database.OpenResult.Recovery is not { } recovery)
            {
                return null;
            }

            string kept = Path.GetFileName(recovery.AsidePath);
            string why = recovery.Problem == LibraryDatabaseProblem.Unrecognised
                ? "was not a Tunqio library"
                : "was damaged";
            return new StartupNotice(
                "Library database reset",
                $"The library database {why} ({recovery.Detail}) and has been replaced with an empty one. The old file was kept as {kept}. " +
                "Rescan your music folders to rebuild the library; playlists can be restored from Settings > Library > Import playlists.",
                StartupNoticeSeverity.Warning);
        }
        catch (LibraryDatabaseException ex)
        {
            logger.LogError(ex, "Library database unavailable ({Problem})", ex.Problem);
            return new StartupNotice("Library unavailable", ex.Message, StartupNoticeSeverity.Error);
        }
    }

    /// <summary>
    /// Start-up step 3 (docs/solution-structure.md): the engine, the output and the saved queue, all after the first
    /// frame because loading mpcore and opening a WASAPI device are not worth delaying the window for. Nothing here
    /// can fail the launch — <see cref="AudioStartup"/> returns a notice instead, and the app runs without audio.
    /// </summary>
    private async Task StartAudioAsync(MainWindow window, ILogger<App> logger, string[] commandLine)
    {
        try
        {
            await StartAudioCoreAsync(window, logger).ConfigureAwait(true);
        }
        finally
        {
            // --export-diagnostics FILE (E6-S5): after audio, so system-info.txt names the output the engine opened,
            // and whether or not audio came up, so a machine with no sound still exports.
            await ExportDiagnosticsIfRequestedAsync(logger, commandLine).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The picker-free export tools/check-about.ps1 relies on: the same view model and exporter as the About page's
    /// button, written to the path on the command line. A failure is logged and the app carries on; the harness
    /// reads the log.
    /// </summary>
    private async Task ExportDiagnosticsIfRequestedAsync(ILogger<App> logger, string[] commandLine)
    {
        if (DiagnosticsExportSwitch.Path(commandLine) is not { } zipPath || _host is null)
        {
            return;
        }

        try
        {
            AboutSettingsViewModel about = _host.Services.GetRequiredService<AboutSettingsViewModel>();
            about.RedactPaths = DiagnosticsExportSwitch.Redact(commandLine);
            await about.ExportAsync(Path.GetFullPath(zipPath)).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            logger.LogError(e, "Diagnostics could not be exported to {Path} from the command line", zipPath);
        }
    }

    private async Task StartAudioCoreAsync(MainWindow window, ILogger<App> logger)
    {
        AudioStartup? audio = null;
        try
        {
            audio = _host!.Services.GetRequiredService<AudioStartup>();
            if (await audio.StartAsync().ConfigureAwait(true) is { } notice)
            {
                window.ShowNotice(notice);
            }

            // Audio-reactive theming (E4-S6) cannot start earlier than this: the analysis stream is the engine's,
            // and the engine is deliberately not on the path to the first frame.
            if (audio.AnalysisFrames is { } frames)
            {
                window.AttachReactiveTheming(frames, _host.Services.GetRequiredService<ISettingsStore>());
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            logger.LogError(e, "Audio did not start; this session has no playback");
        }
        finally
        {
            // ALWAYS, and on every path out including the failures: the visualizer is waiting for this to know
            // which engine to draw from, and a session with no audio must still get a picture rather than a
            // black panel (T-179). A zero handle is a visualizer that will only ever draw its idle animation,
            // which is the honest thing to show when there is nothing to listen to.
            window.AttachVisualizerAudio(audio?.NativeEngineHandle ?? nint.Zero);
        }
    }

    /// <summary>
    /// The system media transport controls (E7-S2, ADR-006): the volume flyout, the lock screen and the hardware media
    /// keys, for the main window. Built on the XAML thread as soon as the window has a handle, and before audio: the
    /// bridge waits for the session like the transport panel does, and keeps the media session disabled until then.
    /// The route is <c>SystemMediaTransportControlsInterop.GetForWindow</c>; <see cref="WindowsMediaControls"/> says
    /// why. A machine that will not give a media session costs the flyout and the media keys, not the launch.
    /// </summary>
    private SmtcBridge? StartMediaControls(ILogger<App> logger)
    {
        WindowsMediaControls? controls = null;
        try
        {
            controls = WindowsMediaControls.ForWindow(_mainWindowHandle, _host!.Services.GetRequiredService<ILogger<WindowsMediaControls>>());
            var bridge = new SmtcBridge(
                _host.Services.GetRequiredService<IPlaybackSessionSource>(),
                controls,
                _host.Services.GetService<IArtCache>(),
                TimeProvider.System,
                _host.Services.GetRequiredService<ILogger<SmtcBridge>>());
            logger.LogInformation("Media controls: SMTC through SystemMediaTransportControlsInterop.GetForWindow");
            return bridge;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            controls?.Dispose();
            logger.LogError(e, "Media controls unavailable; the volume flyout and media keys will not reach this session");
            return null;
        }
    }

    /// <summary>
    /// The notification-area icon (E7-S3, ADR-006) and the two window rules it decides: with <c>ui.closeToTray</c> on, closing
    /// the main window cancels the close and hides it; with <c>ui.minimizeToTray</c> on, minimising hides it. Playback is the
    /// session's and carries on either way. A machine that will not give an icon costs the tray, not the launch, and with no
    /// icon neither rule hides the window (<see cref="TrayController.ShouldHideOnClose"/>).
    /// </summary>
    private TrayController? StartTray(MainWindow window, ISettingsStore settings, ILogger<App> logger)
    {
        WinUiTrayIcon? icon = null;
        try
        {
            // The file is TrayIconFiles' decision, from docs/identity.md's names; the executable's icon until those exist.
            icon = new WinUiTrayIcon(TrayIconFiles.Load(
                AppContext.BaseDirectory,
                Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Identity.ExecutableName + ".exe"),
                _host!.Services.GetRequiredService<ILogger<TrayController>>()));
            var tray = new TrayController(
                _host!.Services.GetRequiredService<IPlaybackSessionSource>(),
                icon,
                settings,
                BringMainWindowToForeground,
                ExitFromTray,
                SynchronizationContext.Current,
                _host.Services.GetRequiredService<ILogger<TrayController>>());
            window.AppWindow.Closing += OnMainWindowClosing;
            window.AppWindow.Changed += OnMainWindowChanged;
            logger.LogInformation("Tray icon: shown in the notification area ({Visible})", icon.IsVisible);
            return tray;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            icon?.Dispose();
            logger.LogError(e, "Tray icon unavailable; closing and minimising the window behave as they do without the tray");
            return null;
        }
    }

    /// <summary>
    /// The now-playing toast (E7-S4, ADR-006): <see cref="ToastController"/> over Windows App SDK app notifications. It registers
    /// only while <c>ui.toastOnTrackChange</c> is on, so a profile that never turns it on never touches the notification
    /// platform or the registry. Presses reach <see cref="Program.Inbox"/> and the same <see cref="CommandRouter"/> as every
    /// other activation. A machine that will not give notifications costs the toast, not the launch.
    /// </summary>
    private ToastController? StartToasts(ISettingsStore settings, string? dataRoot, ILogger<App> logger)
    {
        WinUiToastNotifier? notifier = null;
        try
        {
            ILogger<ToastController> log = _host!.Services.GetRequiredService<ILogger<ToastController>>();
            ShellState shell = _host.Services.GetRequiredService<ShellState>();
            notifier = new WinUiToastNotifier(dataRoot, log);
            var toasts = new ToastController(
                _host.Services.GetRequiredService<IPlaybackSessionSource>(),
                notifier,
                settings,
                _host.Services.GetService<IArtCache>(),
                // T-191's logo, beside the executable in both shapes: the picture for a track with no art.
                Path.Combine(AppContext.BaseDirectory, "Assets", "TunqioLogo.png"),
                () => shell.Mode == ShellMode.Focus,
                () =>
                {
                    // Read once per track that would get a toast, and logged: what the rule saw is the evidence (check-toasts).
                    NativeWindowing.ForegroundWindow foreground = NativeWindowing.Foreground();
                    log.LogInformation("Toasts: foreground window {Foreground}", foreground);
                    return foreground.IsThisProcess;
                },
                log);
            logger.LogInformation("Toasts: controller started (ui.toastOnTrackChange {Enabled})", toasts.IsEnabled);
            return toasts;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            notifier?.Dispose();
            logger.LogError(e, "Toasts unavailable; no notification will be shown on a track change this session");
            return null;
        }
    }

    /// <summary>A close of the main window by any means: hidden instead while <c>ui.closeToTray</c> is on and Exit was not chosen.</summary>
    private void OnMainWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_tray?.ShouldHideOnClose() != true)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
        Log.Information("Tray: main window hidden to the tray on close; playback carries on");
    }

    /// <summary>The main window minimised: hidden while <c>ui.minimizeToTray</c> is on. Showing it restores it (NativeWindowing).</summary>
    private void OnMainWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        bool minimised = sender.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized };
        if (_hiddenOnMinimise)
        {
            // One minimise raises Changed more than once before the hide has settled (check-tray logged two hides for one
            // minimise). The window stays hidden until it is shown and restored, which is when the next minimise counts.
            if (sender.IsVisible && !minimised)
            {
                _hiddenOnMinimise = false;
            }

            return;
        }

        if (!sender.IsVisible || !minimised || _tray?.ShouldHideOnMinimize() != true)
        {
            return;
        }

        _hiddenOnMinimise = true;
        sender.Hide();
        Log.Information("Tray: main window hidden to the tray on minimise");
    }

    /// <summary>
    /// Exit from the tray menu: closes the main window, which runs the same shutdown as the close button with close-to-tray
    /// off (<see cref="OnWindowClosed"/>). The controller has already marked the close as a real one.
    /// </summary>
    private void ExitFromTray()
    {
        if (_window is not { } window)
        {
            return;
        }

        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (_host is not null)
            {
                window.Close();
            }
        });
    }

    /// <summary>
    /// Live library updates (E3-S6) start once the window is up, and the launch scan (E3-S12) follows after the
    /// coordinator's delay so the UI is interactive first (docs/library-and-data.md, "Scheduling"). Stopping is
    /// part of host disposal.
    /// </summary>
    private async Task StartLibraryWatcherAsync(ILogger<App> logger)
    {
        try
        {
            _host!.Services.GetRequiredService<LibraryScanCoordinator>().StartLaunchScan();
            await _host.Services.GetRequiredService<ILibraryWatcher>().StartAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            logger.LogError(e, "Library watcher did not start; changes on disk need a manual rescan this session");
        }

        try
        {
            // Playlist auto-export (E6-S2): after the window is up, like the watcher; it also catches up any change the
            // last session made inside its final export window.
            await _host!.Services.GetRequiredService<PlaylistFiles>().StartAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            logger.LogError(e, "Playlist auto-export did not start; playlist changes this session are not exported");
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_host is null)
        {
            return;
        }

        try
        {
            // Shutdown step 1-3: the queue and position are written back, and the device released, before the
            // library database and the settings file close under them. Container disposal would reach AudioStartup
            // first anyway, but only because it was created last; saying it here does not leave that to luck.
            // The media session goes first of all (E7-S2): a flyout press must not reach a session being torn down.
            // Each step is logged before it runs (T-188), so the last line of a session that dies here names the step.
            // The tray icon before that (E7-S3): its menu must not reach a session being torn down either, and disposing it
            // removes the icon, so none is left in the notification area after the process has gone.
            // The toast before both (E7-S4): a press must not reach a session being torn down, and a now-playing toast for a player
            // that has gone is removed rather than left in the notification centre.
            // The jump list before everything (E7-S5): a refresh reads the library database, which must not be reading while the
            // session and the database close under it. What it last wrote stays on the taskbar.
            Log.Information("Shutdown: jump list");
            _jumpList?.Dispose();
            _jumpList = null;
            Log.Information("Shutdown: toasts");
            _toasts?.Dispose();
            _toasts = null;
            Log.Information("Shutdown: tray icon");
            if (_window is not null && _tray is not null)
            {
                _window.AppWindow.Closing -= OnMainWindowClosing;
                _window.AppWindow.Changed -= OnMainWindowChanged;
            }

            _tray?.Dispose();
            _tray = null;
            Log.Information("Shutdown: media controls");
            _mediaControls?.Dispose();
            _mediaControls = null;
            Log.Information("Shutdown: audio");
            _host.Services.GetRequiredService<AudioStartup>().Dispose();
            Log.Information("Shutdown: playlist exports");
            FlushPlaylistExports();
            Log.Information("Shutdown: settings");
            _host.Services.GetRequiredService<ISettingsStore>().Flush();
            _host.Services.GetRequiredService<ILogger<App>>().LogInformation("Session {SessionId} ending", SessionId);
        }
        finally
        {
            // No hosted services yet; Dispose is the whole shutdown. E7 adds StopAsync for the integration services.
            // Unpublished before it is disposed (T-188): XAML unloads the window's pages after this handler returns, and a
            // callback that looks for services then must find none, not a disposed provider that throws.
            Log.Information("Shutdown: host");
            IHost host = _host;
            _host = null;
            host.Dispose();
            // The logger stays open (T-188): XAML keeps running after this, and an exception it raises on the way out has
            // to reach the file. ProcessExit closes it; the file sink writes each event through, so a crash loses nothing.
            Log.Information("Shutdown: host disposed");
        }
    }

    /// <summary>
    /// A playlist changed in the last few seconds is still waiting for its export window; write it before the database
    /// closes. Bounded, because a stuck disk must not hold the process open; the flush runs on the pool, so blocking the UI
    /// thread on it cannot deadlock (the same shape as <see cref="AudioStartup"/>'s teardown).
    /// </summary>
    private void FlushPlaylistExports()
    {
        try
        {
            PlaylistFiles files = _host!.Services.GetRequiredService<PlaylistFiles>();
            Task flush = Task.Run(() => files.FlushAsync());
#pragma warning disable VSTHRD002 // The window is closing and the container is disposing synchronously; the wait is capped and the work is on the pool.
            if (!flush.Wait(TimeSpan.FromSeconds(3)))
#pragma warning restore VSTHRD002
            {
                Log.Warning("Playlist exports did not finish writing within 3 s of shutdown");
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error(e, "Playlist exports could not be written at shutdown");
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e) =>
        LogFatal("XAML", e.Exception, e.Message);

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e) =>
        LogFatal("AppDomain", e.ExceptionObject as Exception, $"{e.ExceptionObject} (terminating {e.IsTerminating})");

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e) =>
        LogFatal("Unobserved task", e.Exception, e.Exception.Message);

    /// <summary>
    /// One unhandled exception, with its HRESULT, written and flushed before the handler returns (T-188): the process is
    /// usually about to end, and a stowed exception ends it without running anything else of ours.
    /// </summary>
    private static void LogFatal(string source, Exception? exception, string? message)
    {
        Log.Fatal(
            exception,
            "Unhandled exception ({Source}) in session {SessionId}: {Type} HRESULT 0x{HResult:X8} {Message}",
            source, SessionId, exception?.GetType().FullName ?? "(none)", exception?.HResult ?? 0, message);
        // Not closed: after an unobserved task exception, or when XAML survives, the app carries on logging. The file sink is
        // shared, which writes each event through to the file before Emit returns, so the line is on disk already.
    }
}
