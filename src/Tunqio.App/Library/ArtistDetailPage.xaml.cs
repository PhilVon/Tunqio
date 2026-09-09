using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;

namespace Tunqio.App.Library;

/// <summary>Code-behind for the artist detail page; the parameter is the artist id.</summary>
public sealed partial class ArtistDetailPage : Page
{
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
            ViewModel.LoadAsync(artistId).Forget("Artist detail load");
        }
    }

    private void OnPlayAll(object sender, RoutedEventArgs e) => ViewModel.PlayAllAsync().Forget("Play artist");

    private void OnAlbumAction(object? sender, AlbumActionEventArgs e) => ViewModel.HandleAsync(e.Action, e.Album).Forget("Album " + e.Action);
}
