using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using Tunqio.App.Library;
using Tunqio.App.Playback;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
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

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

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

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IAppPaths>(paths);
                services.AddLibrary();
                // The library views' play/enqueue actions (E3-S8) target the session; until E1-S10 the stand-in logs them.
                services.AddSingleton<IPlaybackCommands, PendingPlaybackCommands>();
                services.AddLibraryViews();
            })
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

        var window = new MainWindow(forceWarp: RenderSpikeRunner.WantsWarp(commandLine));
        _window = window;
        _window.Closed += OnWindowClosed;
        if (databaseNotice is not null)
        {
            window.ShowNotice(databaseNotice);
        }

        _window.Activate();
        logger.LogInformation("Main window shown after {ElapsedMs} ms", startup.ElapsedMilliseconds);
        _ = StartLibraryWatcherAsync(logger);

        if (RenderSpikeRunner.IsRequested(commandLine))
        {
            new RenderSpikeRunner(window, logger, commandLine, Path.Combine(paths.LogsDirectory, "render-spike.json")).Start();
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
    /// Live library updates (E3-S6) start once the window is up; the launch scan itself arrives with the
    /// Library settings page (E3-S12). Stopping is part of host disposal.
    /// </summary>
    private async Task StartLibraryWatcherAsync(ILogger<App> logger)
    {
        try
        {
            await _host!.Services.GetRequiredService<ILibraryWatcher>().StartAsync().ConfigureAwait(false);
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
