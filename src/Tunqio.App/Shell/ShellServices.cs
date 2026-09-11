using Microsoft.Extensions.DependencyInjection;
using Tunqio.App.Library;
using Tunqio.App.Playback;

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
        services.AddSingleton(p => new ShellNotices(
            p.GetRequiredService<IPlaybackSessionSource>(),
            p.GetRequiredService<LibraryScanCoordinator>(),
            uiContext));
        return services;
    }
}
