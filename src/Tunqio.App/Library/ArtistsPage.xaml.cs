using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>Code-behind for Library › Artists.</summary>
public sealed partial class ArtistsPage : Page
{
    public ArtistsPage()
    {
        ViewModel = App.Services.GetRequiredService<ArtistsViewModel>();
        InitializeComponent();
        JumpBar.ItemsSource = ArtistsViewModel.Letters;
    }

    public ArtistsViewModel ViewModel { get; }

    /// <summary>"3 albums · 40 tracks" for the row's second line.</summary>
    public static string Counts(int albums, int tracks) => Format.Albums(albums) + " · " + Format.Tracks(tracks);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.NavigationMode == NavigationMode.New)
        {
            ViewModel.LoadAsync().Forget("Artists load");
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistDto artist)
        {
            ViewModel.Open(artist);
        }
    }

    private void OnLetterClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string letter })
        {
            JumpAsync(letter).Forget("Artists jump");
        }
    }

    private async Task JumpAsync(string letter)
    {
        int index = await ViewModel.JumpToAsync(letter);
        if (index < 0)
        {
            return;
        }

        object item = List.Items[index];
        List.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
        List.UpdateLayout();
        (List.ContainerFromIndex(index) as Control)?.Focus(FocusState.Keyboard);
    }
}
