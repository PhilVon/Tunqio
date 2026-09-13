using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tunqio.Core;

namespace Tunqio.App.Shell;

/// <summary>
/// Applies <see cref="ShellLayout"/> and <see cref="ThemePolicy"/> to a live window (E2-S1). The decisions are in
/// those two; this is the part that touches XAML, and it is kept small and separate so the part with the criteria
/// on it stays testable without a window.
/// </summary>
public sealed class ShellChrome
{
    private readonly Grid _grid;
    private readonly FrameworkElement _root;
    private readonly FrameworkElement _nowPlaying;
    private readonly FrameworkElement _sidebar;
    private readonly FrameworkElement _controls;
    private ShellLayoutMode? _applied;

    /// <param name="root">The element the theme is set on; everything else must be inside it.</param>
    /// <param name="grid">The grid whose rows and columns the three panels sit in.</param>
    public ShellChrome(FrameworkElement root, Grid grid, FrameworkElement nowPlaying, FrameworkElement sidebar, FrameworkElement controls)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(nowPlaying);
        ArgumentNullException.ThrowIfNull(sidebar);
        ArgumentNullException.ThrowIfNull(controls);
        _root = root;
        _grid = grid;
        _nowPlaying = nowPlaying;
        _sidebar = sidebar;
        _controls = controls;
    }

    /// <summary>The shape currently applied, or null before the first <see cref="ApplyLayout"/>.</summary>
    public ShellLayoutMode? Mode => _applied;

    /// <summary>
    /// Puts the three panels into the shape <paramref name="windowWidth"/> calls for. A no-op when the shape has
    /// not changed, so dragging a window edge rebuilds the grid three times at most rather than once a pixel.
    /// </summary>
    public void ApplyLayout(double windowWidth)
    {
        ShellLayoutState state = ShellLayout.For(windowWidth);
        if (_applied == state.Mode)
        {
            return;
        }

        _applied = state.Mode;
        _grid.ColumnDefinitions.Clear();
        _grid.RowDefinitions.Clear();
        if (state.Stacked)
        {
            StackVertically(state);
        }
        else
        {
            LayOutInColumns(state);
        }
    }

    /// <summary>
    /// Compact: Now Playing over the sidebar, with the controls as a bar across the bottom of both. The controls
    /// take their natural height rather than a share — a third of a 700 px window spent on a transport bar is a
    /// third not spent on the music.
    /// </summary>
    private void StackVertically(ShellLayoutState state)
    {
        _grid.RowDefinitions.Add(Star(state.NowPlaying));
        _grid.RowDefinitions.Add(Star(state.Sidebar));
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(_nowPlaying, row: 0, column: 0);
        Place(_sidebar, row: 1, column: 0);
        Place(_controls, row: 2, column: 0);
        _sidebar.MinWidth = 0;
        _controls.MinWidth = 0;
        _nowPlaying.MinWidth = 0;
        SetBorder(_sidebar, new Thickness(0, 1, 0, 0));
        SetBorder(_controls, new Thickness(0, 1, 0, 0));
    }

    /// <summary>
    /// Medium and full: Now Playing and the sidebar side by side, each with the floor below which its content stops
    /// working, and the controls as a bar beneath Now Playing at their natural height (T-182). The sidebar spans both
    /// rows, so browsing keeps the full height of the window. The bar used to be a third column, and at 1000 px that
    /// column was 128 px for a transport that needs 242.
    /// </summary>
    private void LayOutInColumns(ShellLayoutState state)
    {
        _grid.RowDefinitions.Add(Star(1));
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.ColumnDefinitions.Add(Star(state.NowPlaying, ShellLayout.NowPlayingMinWidth));
        _grid.ColumnDefinitions.Add(Star(state.Sidebar, ShellLayout.SidebarMinWidth));
        Place(_nowPlaying, row: 0, column: 0);
        Place(_controls, row: 1, column: 0);
        Place(_sidebar, row: 0, column: 1, rowSpan: 2);
        _nowPlaying.MinWidth = ShellLayout.NowPlayingMinWidth;
        _sidebar.MinWidth = ShellLayout.SidebarMinWidth;
        _controls.MinWidth = 0;
        SetBorder(_sidebar, new Thickness(1, 0, 0, 0));
        SetBorder(_controls, new Thickness(0, 1, 0, 0));
    }

    /// <summary>
    /// Sets the theme on the root element rather than rebuilding anything, so the change is a repaint of brushes
    /// already in the tree: nothing is unloaded, and there is no frame where a default background shows through
    /// (AC-69). <see cref="ThemePreference.System"/> is <see cref="ElementTheme.Default"/>, which is how a WinUI
    /// element says "whatever Windows is".
    /// </summary>
    public void ApplyTheme(ThemePreference preference) => _root.RequestedTheme = preference switch
    {
        ThemePreference.Light => ElementTheme.Light,
        ThemePreference.Dark => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private static RowDefinition Star(double weight) => new() { Height = new GridLength(weight, GridUnitType.Star) };

    private static ColumnDefinition Star(double weight, double minWidth) =>
        new() { Width = new GridLength(weight, GridUnitType.Star), MinWidth = minWidth };

    /// <summary>
    /// Row, column and row span together, so a panel that spans rows in one shape does not carry the span into a
    /// shape where it would reach past the grid.
    /// </summary>
    private static void Place(FrameworkElement element, int row, int column, int rowSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetRowSpan(element, rowSpan);
    }

    /// <summary>The panel dividers follow the axis: a left edge beside a panel, a top edge beneath one.</summary>
    private static void SetBorder(FrameworkElement element, Thickness thickness)
    {
        switch (element)
        {
            case Control control:
                control.BorderThickness = thickness;
                break;
            case Border border:
                border.BorderThickness = thickness;
                break;
            default:
                break;
        }
    }
}
