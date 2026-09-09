using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tunqio.Core.Library;
using Windows.Foundation;
using Windows.System;

namespace Tunqio.App.Controls;

/// <summary>Code-behind for the Tracks table; see the XAML for the design notes.</summary>
public sealed partial class TracksList : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(TracksList), new PropertyMetadata(null));

    public static readonly DependencyProperty SortColumnProperty =
        DependencyProperty.Register(nameof(SortColumn), typeof(TrackSort), typeof(TracksList), new PropertyMetadata(TrackSort.Title, (d, _) => ((TracksList)d).UpdateHeader()));

    public static readonly DependencyProperty SortDescendingProperty =
        DependencyProperty.Register(nameof(SortDescending), typeof(bool), typeof(TracksList), new PropertyMetadata(false, (d, _) => ((TracksList)d).UpdateHeader()));

    public static readonly DependencyProperty CanSortProperty =
        DependencyProperty.Register(nameof(CanSort), typeof(bool), typeof(TracksList), new PropertyMetadata(true, (d, _) => ((TracksList)d).UpdateHeader()));

    private static readonly IReadOnlyDictionary<TrackColumn, string> Labels = new Dictionary<TrackColumn, string>
    {
        [TrackColumn.Number] = "#",
        [TrackColumn.Title] = "Title",
        [TrackColumn.Artist] = "Artist",
        [TrackColumn.Album] = "Album",
        [TrackColumn.Duration] = "Time",
        [TrackColumn.Format] = "Format",
        [TrackColumn.Plays] = "Plays",
        [TrackColumn.Rating] = "Rating",
    };

    /// <summary>The column widths of the header and the rows, restored when a hidden column comes back.</summary>
    private static readonly GridLength[] Widths =
    [
        new(40), new(3, GridUnitType.Star), new(2, GridUnitType.Star), new(2, GridUnitType.Star), new(64), new(56), new(48), new(88),
    ];

    private readonly bool[] _visible = [true, true, true, true, true, true, true, true];
    private int _containersCreated;
    private long _itemsRealised;
    private TrackDto? _menuAnchor;

    public TracksList()
    {
        InitializeComponent();
        UpdateHeader();
    }

    /// <summary>A header cell was clicked: the page decides whether that toggles direction or changes the column.</summary>
    public event EventHandler<TrackSort>? SortRequested;

    /// <summary>The selection (list order) was asked to play, queue, navigate or reveal.</summary>
    public event EventHandler<TrackActionEventArgs>? ActionRequested;

    /// <summary>An <see cref="IncrementalItemsSource{T}"/> of <see cref="TrackDto"/> (any list of tracks binds, but only an incremental one pages).</summary>
    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>The column the header marks as sorted.</summary>
    public TrackSort SortColumn
    {
        get => (TrackSort)GetValue(SortColumnProperty);
        set => SetValue(SortColumnProperty, value);
    }

    public bool SortDescending
    {
        get => (bool)GetValue(SortDescendingProperty);
        set => SetValue(SortDescendingProperty, value);
    }

    /// <summary>False for the fixed-order views (Recently added, Recently played, Most played): headers are labels only.</summary>
    public bool CanSort
    {
        get => (bool)GetValue(CanSortProperty);
        set => SetValue(CanSortProperty, value);
    }

    /// <summary>The list's scroll host, once the control has loaded (the spike drives it; views leave it alone).</summary>
    public ScrollViewer? Scroller => VisualTree.FindDescendant<ScrollViewer>(List);

    public ListView ListView => List;

    /// <summary>Distinct item containers created so far: stays near the viewport's row count when recycling works.</summary>
    public int ContainersCreated => _containersCreated;

    /// <summary>Item-to-container bindings so far (grows with every row scrolled into view).</summary>
    public long ItemsRealised => _itemsRealised;

    /// <summary>Shows or hides a column in the header and in every row, realised or still to come. The title column stays.</summary>
    public void SetColumnVisible(TrackColumn column, bool visible)
    {
        int index = (int)column;
        if (column == TrackColumn.Title || _visible[index] == visible)
        {
            return;
        }

        _visible[index] = visible;
        ApplyColumns(Header);
        foreach (object item in List.Items)
        {
            if (List.ContainerFromItem(item) is ListViewItem { ContentTemplateRoot: Grid row })
            {
                ApplyColumns(row);
            }
        }
    }

    /// <summary>The selected tracks in list order (the selection itself is in click order).</summary>
    public IReadOnlyList<TrackDto> SelectionInOrder()
    {
        var tracks = new List<TrackDto>();
        foreach (ItemIndexRange range in List.SelectedRanges)
        {
            for (int i = range.FirstIndex; i <= range.LastIndex; i++)
            {
                if (List.Items[i] is TrackDto track)
                {
                    tracks.Add(track);
                }
            }
        }

        return tracks;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Phase != 0)
        {
            return;
        }

        if (args.ItemContainer.Tag is null)
        {
            args.ItemContainer.Tag = ++_containersCreated;
        }

        _itemsRealised++;
        if (args.ItemContainer.ContentTemplateRoot is Grid row && Array.IndexOf(_visible, false) >= 0)
        {
            ApplyColumns(row);
        }
    }

    private void ApplyColumns(Grid grid)
    {
        for (int i = 0; i < Widths.Length; i++)
        {
            grid.ColumnDefinitions[i].Width = _visible[i] ? Widths[i] : new GridLength(0);
        }

        foreach (UIElement child in grid.Children)
        {
            child.Visibility = _visible[Grid.GetColumn((FrameworkElement)child)] ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateHeader()
    {
        if (Header is null)
        {
            return;
        }

        foreach (Button cell in Header.Children.OfType<Button>())
        {
            var column = (TrackColumn)Grid.GetColumn(cell);
            TrackSort sort = Enum.Parse<TrackSort>((string)cell.Tag);
            // The "#" cell sorts by album order too; the glyph goes on the Album cell only.
            bool sorted = CanSort && sort == SortColumn && column != TrackColumn.Number;
            cell.Content = Labels[column] + (sorted ? (SortDescending ? " ▼" : " ▲") : string.Empty);
            cell.IsEnabled = CanSort;
        }
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (CanSort && sender is Button { Tag: string tag })
        {
            SortRequested?.Invoke(this, Enum.Parse<TrackSort>(tag));
        }
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (TrackOf(e.OriginalSource) is { } track)
        {
            EnsureSelected(track);
            Raise(TrackAction.Play, track);
        }
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        TrackDto? anchor = TrackOf(e.OriginalSource) ?? List.SelectedItem as TrackDto;
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
        if (TrackOf(args.OriginalSource) is not { } track)
        {
            return;
        }

        EnsureSelected(track);
        _menuAnchor = track;
        if (args.TryGetPosition(List, out Point point))
        {
            RowMenu.ShowAt(List, point);
        }
        else if (args.OriginalSource is FrameworkElement element)
        {
            RowMenu.ShowAt(element); // keyboard (Shift+F10, the menu key)
        }

        args.Handled = true;
    }

    private void OnMenuPlay(object sender, RoutedEventArgs e) => Raise(TrackAction.Play, _menuAnchor);

    private void OnMenuPlayNext(object sender, RoutedEventArgs e) => Raise(TrackAction.PlayNext, _menuAnchor);

    private void OnMenuEnqueue(object sender, RoutedEventArgs e) => Raise(TrackAction.Enqueue, _menuAnchor);

    private void OnMenuOpenAlbum(object sender, RoutedEventArgs e) => Raise(TrackAction.OpenAlbum, _menuAnchor);

    private void OnMenuOpenArtist(object sender, RoutedEventArgs e) => Raise(TrackAction.OpenArtist, _menuAnchor);

    private void OnMenuShowInFolder(object sender, RoutedEventArgs e) => Raise(TrackAction.ShowInFolder, _menuAnchor);

    /// <summary>A row acted on outside the selection becomes the selection (the usual list convention).</summary>
    private void EnsureSelected(TrackDto track)
    {
        if (!List.SelectedItems.Contains(track))
        {
            List.SelectedItem = track;
        }
    }

    private void Raise(TrackAction action, TrackDto? anchor)
    {
        IReadOnlyList<TrackDto> tracks = SelectionInOrder();
        if (tracks.Count == 0 && anchor is not null)
        {
            tracks = [anchor];
        }

        if (tracks.Count > 0)
        {
            ActionRequested?.Invoke(this, new TrackActionEventArgs(action, tracks, anchor));
        }
    }

    /// <summary>The track behind a row or anything inside it (the container's item; DataContext as a fallback).</summary>
    private TrackDto? TrackOf(object? element)
    {
        for (DependencyObject? node = element as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewItem container)
            {
                return List.ItemFromContainer(container) as TrackDto;
            }
        }

        return (element as FrameworkElement)?.DataContext as TrackDto;
    }
}
