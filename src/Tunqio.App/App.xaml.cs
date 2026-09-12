using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using Tunqio.App.Library;
using Tunqio.App.Playback;
using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Visualization;
using Tunqio.Library;
using Tunqio.Library.Database;

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
    private static nint _mainWindowHandle;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>The main window's HWND for pickers and dialogs that need an owner; zero before the window exists.</summary>
    public static nint MainWindowHandle => _mainWindowHandle;

    /// <summary>The application's service provider, available after <see cref="OnLaunched"/>.</summary>
    public static IServiceProvider Services => ((App)Current)._host?.Services
        ?? throw new InvalidOperationException("The host has not started yet.");

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Stopwatch startup = Stopwatch.StartNew();
        var paths = new AppPaths();
        paths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("SessionId", SessionId)
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                paths.LogFileTemplate,
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SessionId}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

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

        string[] commandLine = Environment.GetCommandLineArgs().Skip(1).ToArray();
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
            _host.Services.GetRequiredService<IVisualizationHost>());
        _window = window;
        _mainWindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        logger.LogInformation("Shell backdrop: {Backdrop}", window.ApplyBackdrop());
        _window.Closed += OnWindowClosed;
        if (databaseNotice is not null)
        {
            window.ShowNotice(databaseNotice);
        }

        _window.Activate();
        logger.LogInformation("Main window shown after {ElapsedMs} ms", startup.ElapsedMilliseconds);
        _ = StartAudioAsync(window, logger);
        _ = StartLibraryWatcherAsync(logger);

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
    private async Task StartAudioAsync(MainWindow window, ILogger<App> logger)
    {
        try
        {
            AudioStartup audio = _host!.Services.GetRequiredService<AudioStartup>();
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
            _host.Services.GetRequiredService<AudioStartup>().Dispose();
            _host.Services.GetRequiredService<ISettingsStore>().Flush();
            _host.Services.GetRequiredService<ILogger<App>>().LogInformation("Session {SessionId} ending", SessionId);
        }
        finally
        {
            // No hosted services yet; Dispose is the whole shutdown. E7 adds StopAsync for the integration services.
            _host.Dispose();
            _host = null;
            Log.CloseAndFlush();
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Fatal(e.Exception, "Unhandled exception in session {SessionId}", SessionId);
        Log.CloseAndFlush();
    }
}
