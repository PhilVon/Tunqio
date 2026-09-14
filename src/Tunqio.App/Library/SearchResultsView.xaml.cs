using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Tunqio.App.Controls;
using Tunqio.Core.Library;
using Windows.Foundation;
using Windows.System;

namespace Tunqio.App.Library;

/// <summary>Picks a row template by the row's type.</summary>
public sealed partial class SearchResultTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Track { get; set; }

    public DataTemplate? Album { get; set; }

    public DataTemplate? Artist { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        TrackDto => Track,
        AlbumDto => Album,
        ArtistDto => Artist,
        _ => null,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

/// <summary>Code-behind for the search results; see the XAML for the design notes.</summary>
public sealed partial class SearchResultsView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(SearchViewModel), typeof(SearchResultsView), new PropertyMetadata(null));

    private object? _menuRow;

    public SearchResultsView()
    {
        InitializeComponent();
    }

    /// <summary>Esc in the list: the pane clears the search and restores the page.</summary>
    public event EventHandler? EscapeRequested;

    /// <summary>Up from the first row: the pane focuses the search box.</summary>
    public event EventHandler? BackToSearchRequested;

    public SearchViewModel? ViewModel
    {
        get => (SearchViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static string TrackSubline(string artists, string? album) =>
        string.IsNullOrEmpty(album) ? artists : artists.Length == 0 ? album : artists + " · " + album;

    public static string AlbumSubline(string? artist, int? year, int trackCount)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(artist))
        {
            parts.Add(artist);
        }

        if (year is { } y)
        {
            parts.Add(y.ToString(CultureInfo.InvariantCulture));
        }

        parts.Add(Format.Tracks(trackCount));
        return string.Join(" · ", parts);
    }

    public static string NoResultsFor(SearchResults? results) => results is null ? string.Empty : "for “" + results.Text.Trim() + "”";

    /// <summary>Down from the search box: selects and focuses the first row.</summary>
    public bool FocusFirst()
    {
        if (List.Items.Count == 0)
        {
            return false;
        }

        List.SelectedIndex = 0;
        if (List.ContainerFromIndex(0) is Control first)
        {
            return first.Focus(FocusState.Keyboard);
        }

        return List.Focus(FocusState.Keyboard);
    }

    private void OnItemClick(object sender, ItemClickEventArgs e) => Activate(e.ClickedItem, RowAction.Primary);

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                EscapeRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            case VirtualKey.Up when e.OriginalSource is ListViewItem item && List.IndexFromContainer(item) == 0:
                BackToSearchRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            case VirtualKey.Enter:
                object? row = RowOf(e.OriginalSource) ?? List.SelectedItem;
                if (row is not null)
                {
                    Activate(row, Modifiers.Shift ? RowAction.Next : Modifiers.Control ? RowAction.Queue : RowAction.Primary);
                    e.Handled = true;
                }

                return;
        }
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (RowOf(args.OriginalSource) is not { } row)
        {
            return;
        }

        _menuRow = row;
        List.SelectedItem = row;
        MenuFlyout menu = row switch
        {
            TrackDto => TrackMenu,
            AlbumDto => AlbumMenu,
            _ => ArtistMenu,
        };
        if (args.TryGetPosition(List, out Point point))
        {
            menu.ShowAt(List, point);
        }
        else if (args.OriginalSource is FrameworkElement element)
        {
            menu.ShowAt(element); // keyboard (Shift+F10, the menu key)
        }

        args.Handled = true;
    }

    private void OnToggleGroup(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SearchGroupKind kind })
        {
            ViewModel?.ToggleGroup(kind);
        }
    }

    private void OnTrackPlay(object sender, RoutedEventArgs e) => Track(TrackAction.Play);

    private void OnTrackPlayNext(object sender, RoutedEventArgs e) => Track(TrackAction.PlayNext);

    private void OnTrackEnqueue(object sender, RoutedEventArgs e) => Track(TrackAction.Enqueue);

    private void OnTrackOpenAlbum(object sender, RoutedEventArgs e) => Track(TrackAction.OpenAlbum);

    private void OnTrackOpenArtist(object sender, RoutedEventArgs e) => Track(TrackAction.OpenArtist);

    private void OnTrackShowInFolder(object sender, RoutedEventArgs e) => Track(TrackAction.ShowInFolder);

    // Edit tags (T-114) opens a dialog, which needs this view's XamlRoot, so it is the view's and not the view model's.
    private void OnTrackEditTags(object sender, RoutedEventArgs e)
    {
        if (_menuRow is TrackDto track)
        {
            TagEditorDialog.ShowAsync(XamlRoot, [track]).Forget("Edit search track tags");
        }
    }

    private void OnAlbumPlay(object sender, RoutedEventArgs e) => Album(AlbumAction.Play);

    private void OnAlbumPlayNext(object sender, RoutedEventArgs e) => Album(AlbumAction.PlayNext);

    private void OnAlbumEnqueue(object sender, RoutedEventArgs e) => Album(AlbumAction.Enqueue);

    private void OnAlbumOpen(object sender, RoutedEventArgs e) => Album(AlbumAction.Open);

    private void OnAlbumShowInFolder(object sender, RoutedEventArgs e) => Album(AlbumAction.ShowInFolder);

    private void OnArtistOpen(object sender, RoutedEventArgs e)
    {
        if (_menuRow is ArtistDto artist)
        {
            ViewModel?.OpenArtist(artist);
        }
    }

    private void Track(TrackAction action)
    {
        if (_menuRow is TrackDto track)
        {
            ViewModel?.HandleTrackAsync(action, track).Forget("Search track " + action);
        }
    }

    private void Album(AlbumAction action)
    {
        if (_menuRow is AlbumDto album)
        {
            ViewModel?.HandleAlbumAsync(action, album).Forget("Search album " + action);
        }
    }

    /// <summary>Enter or a click on a row: play a track or an album, open an artist; Shift and Ctrl queue instead.</summary>
    private void Activate(object row, RowAction how)
    {
        if (ViewModel is null)
        {
            return;
        }

        switch (row)
        {
            case TrackDto track:
                ViewModel.HandleTrackAsync(how switch { RowAction.Next => TrackAction.PlayNext, RowAction.Queue => TrackAction.Enqueue, _ => TrackAction.Play }, track).Forget("Search track");
                break;
            case AlbumDto album:
                ViewModel.HandleAlbumAsync(how switch { RowAction.Next => AlbumAction.PlayNext, RowAction.Queue => AlbumAction.Enqueue, _ => AlbumAction.Play }, album).Forget("Search album");
                break;
            case ArtistDto artist:
                ViewModel.OpenArtist(artist);
                break;
        }
    }

    private object? RowOf(object? source)
    {
        for (DependencyObject? node = source as DependencyObject; node is not null; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewItem item)
            {
                return List.ItemFromContainer(item);
            }
        }

        return null;
    }

    private enum RowAction
    {
        Primary,
        Next,
        Queue,
    }
}
