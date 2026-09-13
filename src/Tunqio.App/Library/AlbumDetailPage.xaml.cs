using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;
using Windows.Foundation;
using Windows.System;

namespace Tunqio.App.Library;

/// <summary>Code-behind for the album detail page; the parameter is the album id.</summary>
public sealed partial class AlbumDetailPage : Page, ILibraryRefreshable
{
    private AlbumTrackRow? _menuAnchor;
    private long? _albumId;

    public AlbumDetailPage()
    {
        ViewModel = App.Services.GetRequiredService<AlbumDetailViewModel>();
        InitializeComponent();
    }

    public AlbumDetailViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is long albumId)
        {
            _albumId = albumId;
            LoadAsync(albumId).Forget("Album detail load");
        }
    }

    public void RefreshLibrary()
    {
        if (_albumId is { } albumId)
        {
            LoadAsync(albumId).Forget("Album detail refresh");
        }
    }

    private async Task LoadAsync(long albumId)
    {
        await ViewModel.LoadAsync(albumId);
        if (ViewModel.HasMultipleDiscs)
        {
            DiscsSource.Source = ViewModel.Discs;
            List.ItemsSource = DiscsSource.View;
        }
        else
        {
            List.ItemsSource = ViewModel.Rows;
        }
    }

    private void OnPlay(object sender, RoutedEventArgs e) => ViewModel.PlayAsync().Forget("Play album");

    private void OnShuffle(object sender, RoutedEventArgs e) => ViewModel.ShuffleAsync().Forget("Shuffle album");

    private void OnPlayNext(object sender, RoutedEventArgs e) => ViewModel.PlayNextAsync().Forget("Play album next");

    private void OnEnqueue(object sender, RoutedEventArgs e) => ViewModel.EnqueueAsync().Forget("Queue album");

    private void OnShowInFolder(object sender, RoutedEventArgs e) => ViewModel.ShowInFolder();

    private void OnArtistClick(object sender, RoutedEventArgs e) => ViewModel.OpenArtist();

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RowOf(e.OriginalSource) is { } row)
        {
            List.SelectedItem = row;
            Raise(TrackAction.Play, row);
        }
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        AlbumTrackRow? anchor = RowOf(e.OriginalSource) ?? List.SelectedItem as AlbumTrackRow;
        if (anchor is null)
        {
            return;
        }

        EnsureSelected(anchor);
        Raise(Modifiers.Shift ? TrackAction.PlayNext : Modifiers.Control ? TrackAction.Enqueue : TrackAction.Play, anchor);
        e.Handled = true;
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (RowOf(args.OriginalSource) is not { } row)
        {
            return;
        }

        EnsureSelected(row);
        _menuAnchor = row;
        if (args.TryGetPosition(List, out Point point))
        {
            RowMenu.ShowAt(List, point);
        }
        else if (args.OriginalSource is FrameworkElement element)
        {
            RowMenu.ShowAt(element);
        }

        args.Handled = true;
    }

    private void OnMenuPlay(object sender, RoutedEventArgs e) => Raise(TrackAction.Play, _menuAnchor);

    private void OnMenuPlayNext(object sender, RoutedEventArgs e) => Raise(TrackAction.PlayNext, _menuAnchor);

    private void OnMenuEnqueue(object sender, RoutedEventArgs e) => Raise(TrackAction.Enqueue, _menuAnchor);

    private void OnMenuOpenArtist(object sender, RoutedEventArgs e) => Raise(TrackAction.OpenArtist, _menuAnchor);

    private void OnMenuShowInFolder(object sender, RoutedEventArgs e) => Raise(TrackAction.ShowInFolder, _menuAnchor);

    // Add to playlist (E6-S1): a dialog, so the page and not the view model opens it.
    private void OnAddAlbumToPlaylist(object sender, RoutedEventArgs e) =>
        PlaylistDialogs.AddToPlaylistAsync(XamlRoot, [.. ViewModel.Rows.Select(r => r.Track.Id)]).Forget("Add album to playlist");

    private void OnMenuAddToPlaylist(object sender, RoutedEventArgs e) =>
        PlaylistDialogs.AddToPlaylistAsync(XamlRoot, [.. SelectedRows(_menuAnchor).Select(r => r.Track.Id)]).Forget("Add tracks to playlist");

    private void EnsureSelected(AlbumTrackRow row)
    {
        if (!List.SelectedItems.Contains(row))
        {
            List.SelectedItem = row;
        }
    }

    private void Raise(TrackAction action, AlbumTrackRow? anchor) =>
        ViewModel.HandleAsync(action, SelectedRows(anchor), anchor).Forget("Album track " + action);

    /// <summary>The selected rows in list order, or just <paramref name="anchor"/> when nothing is selected.</summary>
    private List<AlbumTrackRow> SelectedRows(AlbumTrackRow? anchor)
    {
        var rows = new List<AlbumTrackRow>();
        foreach (ItemIndexRange range in List.SelectedRanges)
        {
            for (int i = range.FirstIndex; i <= range.LastIndex; i++)
            {
                if (List.Items[i] is AlbumTrackRow row)
                {
                    rows.Add(row);
                }
            }
        }

        if (rows.Count == 0 && anchor is not null)
        {
            rows.Add(anchor);
        }

        return rows;
    }

    private AlbumTrackRow? RowOf(object? element)
    {
        for (DependencyObject? node = element as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewItem container)
            {
                return List.ItemFromContainer(container) as AlbumTrackRow;
            }
        }

        return (element as FrameworkElement)?.DataContext as AlbumTrackRow;
    }
}
