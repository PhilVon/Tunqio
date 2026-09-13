using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
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
    private readonly FrameworkElement _curation;
    private readonly FrameworkElement _controls;
    private readonly FrameworkElement _settings;
    private readonly FrameworkElement _nowPlayingMetadata;
    private readonly Func<bool> _animationsEnabled;
    private ShellLayoutMode? _applied;
    private ShellMode? _appliedShellMode;
    private ShellLayoutState? _state;

    /// <param name="root">The element the theme is set on; everything else must be inside it.</param>
    /// <param name="grid">The grid whose rows and columns the three panels sit in.</param>
    /// <param name="curation">Curation's dual pane (E5-S4), which takes the sidebar's place in that mode.</param>
    /// <param name="settings">The settings overlay (E6-S3), placed over the sidebar and Now Playing while it is open.</param>
    /// <param name="nowPlayingMetadata">Now Playing's art and metadata, hidden under the overlay; the visualizer is not.</param>
    /// <param name="animationsEnabled">
    /// Windows' "Show animations" switch, read at each mode change so a transition is instant the moment reduced
    /// motion is turned on (flow 10).
    /// </param>
    public ShellChrome(
        FrameworkElement root,
        Grid grid,
        FrameworkElement nowPlaying,
        FrameworkElement sidebar,
        FrameworkElement curation,
        FrameworkElement controls,
        FrameworkElement settings,
        FrameworkElement nowPlayingMetadata,
        Func<bool> animationsEnabled)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(nowPlaying);
        ArgumentNullException.ThrowIfNull(sidebar);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(nowPlayingMetadata);
        ArgumentNullException.ThrowIfNull(animationsEnabled);
        _root = root;
        _grid = grid;
        _nowPlaying = nowPlaying;
        _sidebar = sidebar;
        _curation = curation;
        _controls = controls;
        _settings = settings;
        _nowPlayingMetadata = nowPlayingMetadata;
        _animationsEnabled = animationsEnabled;
    }

    /// <summary>True while the settings overlay is showing.</summary>
    public bool SettingsOpen { get; private set; }

    /// <summary>
    /// Shows or hides the settings overlay (E6-S3). While it is open it covers the cells Now Playing and the sidebar take in
    /// the current shape, the art and metadata under it are hidden so the visualizer is what shows through, and the
    /// controls bar stays where it is and in reach. Closing puts every panel back as the shape and mode say.
    /// </summary>
    public void SetSettingsOpen(bool open)
    {
        SettingsOpen = open;
        ApplySettingsPlacement();
    }

    /// <summary>The shape currently applied, or null before the first <see cref="ApplyLayout"/>.</summary>
    public ShellLayoutMode? Mode => _applied;

    /// <summary>The mode currently applied, or null before the first <see cref="ApplyLayout"/>.</summary>
    public ShellMode? ShellMode => _appliedShellMode;

    /// <summary>
    /// Puts the three panels into the shape <paramref name="windowWidth"/> and <paramref name="mode"/> call for. A
    /// no-op when neither has changed, so dragging a window edge rebuilds the grid three times at most rather than
    /// once a pixel.
    /// </summary>
    /// <remarks>
    /// The sidebar is collapsed in Focus rather than taken out of the grid. A collapsed element stays in the visual
    /// tree and does not unload, so the library pane keeps its navigator, its page and its back stack, and leaving
    /// Focus shows the page that was there (AC-134).
    /// </remarks>
    public void ApplyLayout(double windowWidth, ShellMode mode)
    {
        ShellLayoutState state = ShellLayout.For(windowWidth, mode);
        if (_applied == state.Mode && _appliedShellMode == mode)
        {
            return;
        }

        bool modeChanged = _appliedShellMode is not null && _appliedShellMode != mode;
        _state = state;
        _applied = state.Mode;
        _appliedShellMode = mode;
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

        // Curation's editor sits in the sidebar's cell, on the same floor and divider. The library pane is collapsed
        // under it rather than removed, for the reason Focus collapses it.
        Grid.SetRow(_curation, Grid.GetRow(_sidebar));
        Grid.SetColumn(_curation, Grid.GetColumn(_sidebar));
        Grid.SetRowSpan(_curation, Grid.GetRowSpan(_sidebar));
        _curation.MinWidth = _sidebar.MinWidth;
        SetBorder(_curation, (_sidebar as Control)?.BorderThickness ?? default);
        ApplySettingsPlacement();
        if (modeChanged)
        {
            AnimateModeChange(state);
        }
    }

    /// <summary>
    /// Where the overlay goes and what it hides, for the shape last applied. Stacked, the overlay takes the Now Playing and
    /// sidebar rows. In columns the controls bar sits under Now Playing only, so the overlay takes the top row across both
    /// columns and the bar is widened to both for as long as the overlay is open; otherwise the sidebar's lower part would
    /// show beside the bar with the overlay over the rest of it.
    /// </summary>
    private void ApplySettingsPlacement()
    {
        if (_state is not { } state)
        {
            return;
        }

        bool open = SettingsOpen;
        if (state.Stacked)
        {
            Place(_settings, row: 0, column: 0, rowSpan: 2);
            Grid.SetColumnSpan(_settings, 1);
            Grid.SetColumnSpan(_controls, 1);
        }
        else
        {
            Place(_settings, row: 0, column: 0);
            Grid.SetColumnSpan(_settings, 2);
            Grid.SetColumnSpan(_controls, open ? 2 : 1);
        }

        _settings.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _nowPlayingMetadata.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        _sidebar.Visibility = !open && state.SidebarShown && !state.CurationEditor ? Visibility.Visible : Visibility.Collapsed;
        _curation.Visibility = !open && state.CurationEditor ? Visibility.Visible : Visibility.Collapsed;
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
    /// column was 128 px for a transport that needs 242. In Focus the sidebar's column has no share and no floor.
    /// </summary>
    private void LayOutInColumns(ShellLayoutState state)
    {
        double sidebarFloor = state.SidebarShown ? ShellLayout.SidebarMinWidth : 0;
        _grid.RowDefinitions.Add(Star(1));
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _grid.ColumnDefinitions.Add(Star(state.NowPlaying, ShellLayout.NowPlayingMinWidth));
        _grid.ColumnDefinitions.Add(Star(state.Sidebar, sidebarFloor));
        Place(_nowPlaying, row: 0, column: 0);
        Place(_controls, row: 1, column: 0);
        Place(_sidebar, row: 0, column: 1, rowSpan: 2);
        _nowPlaying.MinWidth = ShellLayout.NowPlayingMinWidth;
        _sidebar.MinWidth = sidebarFloor;
        _controls.MinWidth = 0;
        SetBorder(_sidebar, new Thickness(1, 0, 0, 0));
        SetBorder(_controls, new Thickness(0, 1, 0, 0));
    }

    /// <summary>
    /// The mode transition: Now Playing, and the sidebar when it has come back, fade in over
    /// <see cref="ShellLayout.ModeTransitionDuration"/> on their composition visuals. Nothing runs under reduced
    /// motion. The panels' sizes are not animated: the grid resizes them in one layout pass, and a fade over the new
    /// shape is what keeps that from reading as a jump.
    /// </summary>
    private void AnimateModeChange(ShellLayoutState state)
    {
        TimeSpan duration = ShellLayout.ModeTransition(_animationsEnabled());
        if (duration == TimeSpan.Zero)
        {
            return;
        }

        FadeIn(_nowPlaying, duration);
        if (state.CurationEditor)
        {
            FadeIn(_curation, duration);
        }
        else if (state.SidebarShown)
        {
            FadeIn(_sidebar, duration);
        }
    }

    private static void FadeIn(UIElement element, TimeSpan duration)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        ScalarKeyFrameAnimation fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = duration;
        visual.StartAnimation("Opacity", fade);
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
