using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Navigation;

namespace Tunqio.App.Library;

/// <summary>
/// A page in the library pane's frame that reloads when the library changed under it (a scan that added or
/// flagged rows, a folder removed, missing tracks purged). The pane calls it on the page that is showing; a
/// cached page navigated back to asks <see cref="LibraryFreshness"/> whether it is stale.
/// </summary>
public interface ILibraryRefreshable
{
    void RefreshLibrary();
}

/// <summary>
/// For a page with <c>NavigationCacheMode="Enabled"</c>: whether the instance the frame kept loaded before
/// the library last changed. A fresh navigation always loads; Back loads only when the version moved.
/// </summary>
public sealed class LibraryFreshness
{
    private readonly LibraryScanCoordinator _scans = App.Services.GetRequiredService<LibraryScanCoordinator>();
    private long _loaded = -1;

    public bool ShouldLoad(NavigationEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return e.NavigationMode == NavigationMode.New || _loaded != _scans.LibraryVersion;
    }

    /// <summary>Call as the load starts, so a change during the load still counts as unseen.</summary>
    public void MarkLoaded() => _loaded = _scans.LibraryVersion;
}
