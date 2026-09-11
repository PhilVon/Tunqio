using Microsoft.Extensions.DependencyInjection;
using Tunqio.App.Library;
using Tunqio.App.Playback;
using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Library;

namespace Tunqio.App;

/// <summary>
/// The composition root: everything the shell puts in its container, in one place a test can read.
/// </summary>
/// <remarks>
/// It used to be the body of <see cref="App"/>'s <c>ConfigureServices</c> lambda, which meant the only way to find
/// out what the app registers was to launch the app. That is how <see cref="ShellNotices"/> came to be resolved by
/// the tag editor without ever being registered: every headless test injected one directly, so the wiring itself
/// had no test and the failure waited for a real run. Registration adds descriptors and touches nothing - no
/// database is opened, no file is read - so a test can call this and read the descriptors back.
/// </remarks>
public static class AppServices
{
    /// <param name="paths">Where the app keeps its data; resolved by the library layer during start-up.</param>
    /// <param name="uiContext">
    /// The XAML thread's context, captured by the caller on that thread. The coordinators and view models that
    /// raise events for the UI to bind are given it here rather than reading it themselves, because by the time
    /// one of them is resolved the current context may be a thread-pool one.
    /// </param>
    public static IServiceCollection AddTunqio(
        this IServiceCollection services, IAppPaths paths, SynchronizationContext? uiContext)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(paths);
        services.AddLibrary();
        // The library views' play/enqueue actions (E3-S8) target the session, which AudioStartup creates
        // after the first frame; until then AppPlaybackCommands carries the requests.
        services.AddPlayback();
        services.AddLibraryViews(uiContext);
        services.AddShell(uiContext);
        return services;
    }
}
