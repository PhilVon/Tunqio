using Microsoft.Extensions.DependencyInjection;
using Tunqio.App.Controls;

namespace Tunqio.App.Library;

/// <summary>
/// Where a page finds the container's <see cref="HoverPreviewController"/>. A grid page is created by the sidebar's
/// frame, so it has no constructor to be injected through; it resolves, as the pages' view models already do.
/// </summary>
internal static class HoverPreview
{
    /// <summary>The container's controller.</summary>
    public static HoverPreviewController Controller => App.Services.GetRequiredService<HoverPreviewController>();

    /// <summary>Hands a grid's pointer report to the controller.</summary>
    public static void Report(AlbumHoverEventArgs e)
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
}
