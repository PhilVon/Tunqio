using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;

namespace Tunqio.App.Library;

/// <summary>Code-behind for the artist detail page; the parameter is the artist id.</summary>
public sealed partial class ArtistDetailPage : Page, ILibraryRefreshable
{
    private long? _artistId;

    public ArtistDetailPage()
    {
        ViewModel = App.Services.GetRequiredService<ArtistDetailViewModel>();
        InitializeComponent();
    }

    public ArtistDetailViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is long artistId)
        {
            _artistId = artistId;
            ViewModel.LoadAsync(artistId).Forget("Artist detail load");
        }
    }

    public void RefreshLibrary()
    {
        if (_artistId is { } artistId)
        {
            ViewModel.LoadAsync(artistId).Forget("Artist detail refresh");
        }
    }

    private void OnPlayAll(object sender, RoutedEventArgs e) => ViewModel.PlayAllAsync().Forget("Play artist");

    private void OnAlbumAction(object? sender, AlbumActionEventArgs e) => ViewModel.HandleAsync(e.Action, e.Album).Forget("Album " + e.Action);

    // Hover preview (E5-S5), for both grids on this page.
    private void OnAlbumHover(object? sender, AlbumHoverEventArgs e) => HoverPreview.Report(e);

    private void OnUnloaded(object sender, RoutedEventArgs e) => HoverPreview.Controller.LeaveAll();
}
