using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tunqio.Core.Library;
using Windows.System;

namespace Tunqio.App.Controls;

/// <summary>Code-behind for the Albums grid; see the XAML for the design notes.</summary>
public sealed partial class AlbumsGrid : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(AlbumsGrid), new PropertyMetadata(null, (d, _) => ((AlbumsGrid)d).FillViewport()));

    private int _elementsCreated;
    private long _elementsPrepared;
    private bool _filling;
    private AlbumDto? _menuAlbum;

    public AlbumsGrid()
    {
        InitializeComponent();
        Loaded += (_, _) => FillViewport();
    }

    /// <summary>A tile was clicked, invoked from the keyboard or asked something through its menu.</summary>
    public event EventHandler<AlbumActionEventArgs>? ActionRequested;

    /// <summary>
    /// The pointer came onto a tile or left it (E5-S5). The grid reports; the page hands it to the hover preview,
    /// which decides whether that means anything.
    /// </summary>
    public event EventHandler<AlbumHoverEventArgs>? TileHoverChanged;

    /// <summary>An <see cref="IncrementalItemsSource{T}"/> of <see cref="AlbumDto"/>; any list binds, an incremental one pages.</summary>
    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public ScrollViewer ScrollViewer => Scroller;

    /// <summary>Distinct tile elements created so far: stays near one screen's worth when recycling works.</summary>
    public int ElementsCreated => _elementsCreated;

    /// <summary>Tile preparations so far (every album scrolled into view).</summary>
    public long ElementsPrepared => _elementsPrepared;

    private IIncrementalList? Source => ItemsSource as IIncrementalList;

    /// <summary>Moves keyboard focus to the first tile.</summary>
    public bool FocusFirstTile() => Repeater.TryGetElement(0) is Control first && first.Focus(FocusState.Keyboard);

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => FillViewport();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => FillViewport();

    private void OnElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is FrameworkElement element && element.Tag is null)
        {
            element.Tag = ++_elementsCreated;
        }

        _elementsPrepared++;
    }

    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        if (AlbumOf(sender) is { } album)
        {
            Raise(AlbumAction.Play, album);
        }
    }

    private void OnTilePointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (AlbumOf(sender) is { } album)
        {
            TileHoverChanged?.Invoke(this, new AlbumHoverEventArgs(album, entered: true));
        }
    }

    // Exited, cancelled and capture lost all mean the pointer is no longer resting on this tile.
    private void OnTilePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (AlbumOf(sender) is { } album)
        {
            TileHoverChanged?.Invoke(this, new AlbumHoverEventArgs(album, entered: false));
        }
    }

    private void OnTileKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || AlbumOf(e.OriginalSource) is not { } album)
        {
            return;
        }

        if (Modifiers.Shift)
        {
            Raise(AlbumAction.PlayNext, album);
        }
        else if (Modifiers.Control)
        {
            Raise(AlbumAction.Enqueue, album);
        }
        else
        {
            return; // plain Enter is the Button's own click
        }

        e.Handled = true;
    }

    private void OnTileMenuOpening(object? sender, object e) => _menuAlbum = AlbumOf((sender as FlyoutBase)?.Target);

    private void OnMenuPlay(object sender, RoutedEventArgs e) => RaiseForMenu(AlbumAction.Play);

    private void OnMenuPlayNext(object sender, RoutedEventArgs e) => RaiseForMenu(AlbumAction.PlayNext);

    private void OnMenuEnqueue(object sender, RoutedEventArgs e) => RaiseForMenu(AlbumAction.Enqueue);

    private void OnMenuOpen(object sender, RoutedEventArgs e) => RaiseForMenu(AlbumAction.Open);

    private void OnMenuShowInFolder(object sender, RoutedEventArgs e) => RaiseForMenu(AlbumAction.ShowInFolder);

    private void RaiseForMenu(AlbumAction action)
    {
        if (_menuAlbum is { } album)
        {
            Raise(action, album);
        }
    }

    private void Raise(AlbumAction action, AlbumDto album) => ActionRequested?.Invoke(this, new AlbumActionEventArgs(action, album));

    /// <summary>
    /// The album behind a tile or anything inside it. ItemsRepeater hands an x:Bind template its item through
    /// the template component, not DataContext, so the item is found by the element's index in the repeater.
    /// </summary>
    private AlbumDto? AlbumOf(object? element)
    {
        if (element is FrameworkElement { DataContext: AlbumDto bound })
        {
            return bound;
        }

        for (DependencyObject? node = element as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is UIElement ui && ReferenceEquals(VisualTreeHelper.GetParent(ui), Repeater))
            {
                int index = Repeater.GetElementIndex(ui);
                return index >= 0 && ItemsSource is System.Collections.IList list && index < list.Count ? list[index] as AlbumDto : null;
            }
        }

        return null;
    }

    /// <summary>Loads pages while the bottom of the content is within two viewports of the scroll position.</summary>
    private void FillViewport()
    {
        if (_filling || Source is not { HasMore: true } source || !IsLoaded)
        {
            return;
        }

        _ = FillViewportAsync(source);
    }

    private async Task FillViewportAsync(IIncrementalList source)
    {
        _filling = true;
        try
        {
            while (source.HasMore && NearEnd())
            {
                await source.LoadMoreAsync();
                Scroller.UpdateLayout();
            }
        }
        catch (Exception) when (source.LastError is not null)
        {
            // The source keeps the error for the view to show (E2-S7); the grid just stops asking.
        }
        finally
        {
            _filling = false;
        }
    }

    private bool NearEnd() => Scroller.VerticalOffset + 2 * Scroller.ViewportHeight >= Scroller.ExtentHeight;
}

/// <summary>The pointer came onto (<see cref="Entered"/>) or left an album tile.</summary>
public sealed class AlbumHoverEventArgs(AlbumDto album, bool entered) : EventArgs
{
    /// <summary>The album behind the tile.</summary>
    public AlbumDto Album { get; } = album;

    /// <summary>True when the pointer came onto the tile, false when it left.</summary>
    public bool Entered { get; } = entered;
}
