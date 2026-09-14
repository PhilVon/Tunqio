using Microsoft.Extensions.DependencyInjection;
using Tunqio.App.Controls;

namespace Tunqio.App.Library;

/// <summary>
/// Where a page finds the container's <see cref="HoverPreviewController"/>. A grid page is created by the sidebar's
/// frame, so it has no constructor to be injected through; it resolves, as the pages' view models already do.
/// </summary>
public static class HoverPreview
{
    /// <summary>The container's controller.</summary>
    internal static HoverPreviewController Controller => App.Services.GetRequiredService<HoverPreviewController>();

    /// <summary>Hands a grid's pointer report to the controller.</summary>
    internal static void Report(AlbumHoverEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Entered)
        {
            Controller.Enter(e.Album);
        }
        else
        {
            Controller.Leave(e.Album);
        }
    }

    /// <summary>A grid page unloaded: any preview it started stops. Safe after shutdown, when there is nothing to stop.</summary>
    internal static void LeaveAll() => LeaveAll(App.ServicesIfRunning);

    /// <summary>
    /// Stops any preview through <paramref name="services"/>'s controller, and does nothing when there are no services.
    /// A page's Unloaded arrives after the main window has closed and the host is gone (T-188); throwing there, on the XAML
    /// thread, ended every close of the app with a stowed exception (0xc000027b in Microsoft.UI.Xaml.dll).
    /// </summary>
    public static void LeaveAll(IServiceProvider? services) => services?.GetService<HoverPreviewController>()?.LeaveAll();
}
