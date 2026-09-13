using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;

namespace Tunqio.App.Library;

/// <summary>Code-behind for Library › Playlists; see the XAML.</summary>
public sealed partial class PlaylistsPage : Page, ILibraryRefreshable
{
    public PlaylistsPage()
    {
        ViewModel = App.Services.GetRequiredService<PlaylistsViewModel>();
        InitializeComponent();
    }

    public PlaylistsViewModel ViewModel { get; }

    // Every arrival reloads, not only the first: coming back from a playlist's page after renaming or deleting it has
    // to show the list as it now is.
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.LoadAsync().Forget("Playlists load");
    }

    public void RefreshLibrary() => ViewModel.LoadAsync().Forget("Playlists refresh");

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistRow row)
        {
            ViewModel.Open(row);
        }
    }

    private void OnNewPlaylist(object sender, RoutedEventArgs e) => NewPlaylistAsync().Forget("New playlist");

    private async Task NewPlaylistAsync()
    {
        string? name = await PlaylistDialogs.AskNameAsync(XamlRoot, "New playlist", string.Empty, "Create");
        await ViewModel.CreateAsync(name);
    }
}
