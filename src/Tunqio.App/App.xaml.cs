using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using Tunqio.Core;
using Tunqio.Library;

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
            })
            .Build();
        _host.Start();

        ILogger<App> logger = _host.Services.GetRequiredService<ILogger<App>>();
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

        _window = new MainWindow();
        _window.Closed += OnWindowClosed;
        _window.Activate();
        logger.LogInformation("Main window shown after {ElapsedMs} ms", startup.ElapsedMilliseconds);
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
