using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tunqio.Core.Library;

namespace Tunqio.App.Controls;

/// <summary>Code-behind for the Albums grid; see the XAML for the design notes.</summary>
public sealed partial class AlbumsGrid : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(AlbumsGrid), new PropertyMetadata(null, (d, _) => ((AlbumsGrid)d).FillViewport()));

    private int _elementsCreated;
    private long _elementsPrepared;
    private bool _filling;

    public AlbumsGrid()
    {
        InitializeComponent();
        Loaded += (_, _) => FillViewport();
    }

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
