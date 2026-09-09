using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tunqio.Core.Library;

namespace Tunqio.App.Controls;

/// <summary>Code-behind for the Tracks table; see the XAML for the design notes.</summary>
public sealed partial class TracksList : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(nameof(ItemsSource), typeof(object), typeof(TracksList), new PropertyMetadata(null));

    private int _containersCreated;
    private long _itemsRealised;

    public TracksList()
    {
        InitializeComponent();
    }

    /// <summary>An <see cref="IncrementalItemsSource{T}"/> of <see cref="TrackDto"/> (any list of tracks binds, but only an incremental one pages).</summary>
    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>The list's scroll host, once the control has loaded (the spike drives it; views leave it alone).</summary>
    public ScrollViewer? Scroller => VisualTree.FindDescendant<ScrollViewer>(List);

    public ListView ListView => List;

    /// <summary>Distinct item containers created so far: stays near the viewport's row count when recycling works.</summary>
    public int ContainersCreated => _containersCreated;

    /// <summary>Item-to-container bindings so far (grows with every row scrolled into view).</summary>
    public long ItemsRealised => _itemsRealised;

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
    }
}
