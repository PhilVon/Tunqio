using Microsoft.Extensions.DependencyInjection;
using Tunqio.App.Library;
using Tunqio.App.Playback;
using Tunqio.Core;
using Tunqio.Core.Visualization;
using Tunqio.Interop;

namespace Tunqio.App.Shell;

/// <summary>
/// DI registration for the shell's own view models (E2-S7).
/// </summary>
/// <remarks>
/// Only <see cref="ShellNotices"/> so far, and it is here rather than left to the window because two parts of the
/// app need the <em>same</em> one. <see cref="MainWindow"/> binds it to the <see cref="NoticePanel"/> it shows;
/// <see cref="TagEditorDialog"/> resolves it from the container to leave the Undo bar behind after a write
/// (docs/ui-screens-and-flows.md, flow 8), because a row menu in a list control has no container of its own to be
/// injected from. A second instance would satisfy the resolve and collect notices nobody is bound to — so the
/// singleton the container holds is the one the window is given, and the window no longer builds its own.
/// </remarks>
public static class ShellServices
{
    /// <param name="uiContext">
    /// The XAML thread's context, captured at registration rather than read inside the factory: the first thing to
    /// ask for a notice may well be a background continuation, and the context current <em>there</em> is not the
    /// one the <c>ObservableCollection</c> behind the notice panel has to be touched on.
    /// </param>
    public static IServiceCollection AddShell(this IServiceCollection services, SynchronizationContext? uiContext)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(p =>
        {
            var notices = new ShellNotices(
                p.GetRequiredService<IPlaybackSessionSource>(),
                p.GetRequiredService<LibraryScanCoordinator>(),
                uiContext);
            // A rating's file write that failed is told here (E6-S7); the rater is the container's, so a write that
            // waited for playback to release the file reports through the same bar when it finally runs.
            p.GetRequiredService<Core.Library.ITrackRater>().FileWriteCompleted += (_, change) => notices.ShowRatingWrite(change);
            return notices;
        });
        // The mode (E5-S1, ADR-007). One for the process: the window's layout, the switcher and the shortcuts all
        // move this value, and ui.mode is written back from here.
        services.AddSingleton(p => new ShellState(p.GetRequiredService<ISettingsStore>()));
        // One visualizer surface for the process (E4-S9). MainWindow attaches it to the SwapChainPanel;
        // VisualizationSettingsViewModel switches its preset and moves its parameters. Two of these would be two
        // D3D devices, one of them drawing into nothing.
        services.AddSingleton<IVisualizationHost>(_ => new VisualizationHost());
        services.AddSingleton(p => new VisualizationSettingsViewModel(
            p.GetRequiredService<IVisualizationHost>(),
            p.GetRequiredService<ISettingsStore>(),
            p.GetRequiredService<IAppPaths>()));
        // Settings > Playback, Output and Appearance (E6-S3). Singletons like the Visualization page's: the overlay caches
        // its pages, and one view model per page is what they bind to.
        services.AddSingleton(p => new PlaybackSettingsViewModel(p.GetRequiredService<ISettingsStore>()));
        services.AddSingleton(p => new OutputSettingsViewModel(
            p.GetRequiredService<IPlaybackSessionSource>(),
            p.GetRequiredService<ISettingsStore>(),
            p.GetRequiredService<IAppPaths>().DataRoot));
        services.AddSingleton(p => new AppearanceSettingsViewModel(p.GetRequiredService<ISettingsStore>()));
        return services;
    }
}
