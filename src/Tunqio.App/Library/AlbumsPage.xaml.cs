using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;

namespace Tunqio.App.Library;

/// <summary>Code-behind for Library › Albums: the chips are pushed to and from the view model by hand so a replaced option list never leaves a chooser blank.</summary>
public sealed partial class AlbumsPage : Page, ILibraryRefreshable
{
    private readonly LibraryFreshness _freshness = new();
    private bool _syncing;

    public AlbumsPage()
    {
        ViewModel = App.Services.GetRequiredService<AlbumsViewModel>();
        InitializeComponent();
        SortBox.ItemsSource = AlbumsViewModel.SortOptions;
        SortBox.SelectedItem = ViewModel.SelectedSort;
    }

    public AlbumsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (_freshness.ShouldLoad(e))
        {
            LoadAsync().Forget("Albums load");
        }
    }

    public void RefreshLibrary() => LoadAsync().Forget("Albums refresh");

    private async Task LoadAsync()
    {
        _freshness.MarkLoaded();
        await ViewModel.InitializeAsync();
        SyncChips();
    }

    /// <summary>Re-applies the view model's choices after the option lists were replaced (which clears a ComboBox's selection).</summary>
    private void SyncChips()
    {
        _syncing = true;
        try
        {
            SortBox.SelectedItem = ViewModel.SelectedSort;
            GenreBox.SelectedItem = ViewModel.SelectedGenre;
            DecadeBox.SelectedItem = ViewModel.SelectedDecade;
            CodecBox.SelectedItem = ViewModel.SelectedCodec;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && SortBox.SelectedItem is FacetOption option)
        {
            ViewModel.SelectedSort = option;
        }
    }

    private void OnDirectionClick(object sender, RoutedEventArgs e) => ViewModel.Descending = DirectionToggle.IsChecked == true;

    private void OnGenreChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && GenreBox.SelectedItem is FacetOption option)
        {
            ViewModel.SelectedGenre = option;
        }
    }

    private void OnDecadeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && DecadeBox.SelectedItem is FacetOption option)
        {
            ViewModel.SelectedDecade = option;
        }
    }

    private void OnCodecChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && CodecBox.SelectedItem is FacetOption option)
        {
            ViewModel.SelectedCodec = option;
        }
    }

    private void OnAlbumAction(object? sender, AlbumActionEventArgs e) => ViewModel.HandleAsync(e.Action, e.Album).Forget("Album " + e.Action);

    // Hover preview (E5-S5). The controller is the container's one; it decides whether a hover means anything.
    private void OnAlbumHover(object? sender, AlbumHoverEventArgs e) => HoverPreview.Report(e);

    private void OnUnloaded(object sender, RoutedEventArgs e) => HoverPreview.Controller.LeaveAll();
}
