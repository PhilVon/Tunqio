using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;
using Windows.System;

namespace Tunqio.App.Library;

/// <summary>Code-behind for the library pane; see the XAML for the design notes.</summary>
public sealed partial class LibraryPane : UserControl
{
    /// <summary>The "/" key (VK_OEM_2 on a US layout), which has no <see cref="VirtualKey"/> name.</summary>
    private const VirtualKey SlashKey = (VirtualKey)191;

    /// <summary>The root page of each pane item; a Tracks page is told which fixed view it is.</summary>
    private static readonly Dictionary<string, (Type Page, object? Parameter)> Routes = new(StringComparer.Ordinal)
    {
        ["albums"] = (typeof(AlbumsPage), null),
        ["artists"] = (typeof(ArtistsPage), null),
        ["tracks"] = (typeof(TracksPage), TracksSpec.All),
        ["genres"] = (typeof(GenresPage), null),
        ["folders"] = (typeof(FoldersPage), null),
        ["playlists"] = (typeof(PlaylistsPage), null),
        ["recent-added"] = (typeof(TracksPage), TracksSpec.RecentlyAdded),
        ["recent-played"] = (typeof(TracksPage), TracksSpec.RecentlyPlayed),
        ["most-played"] = (typeof(TracksPage), TracksSpec.MostPlayed),
    };

    private readonly LibraryNavigator _navigator;
    private readonly LibraryScanCoordinator _scans;
    private bool _syncingSelection;
    private object? _currentParameter;

    public LibraryPane()
    {
        _navigator = App.Services.GetRequiredService<LibraryNavigator>();
        _scans = App.Services.GetRequiredService<LibraryScanCoordinator>();
        Search = App.Services.GetRequiredService<SearchViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The frame the pages live in (the shell's Alt+Left target).</summary>
    public Frame Frame => PageFrame;

    /// <summary>The search box's state and results (E3-S9).</summary>
    public SearchViewModel Search { get; }

    /// <summary>Collapsed while <paramref name="condition"/> holds (the page frame under the search results).</summary>
    public static Visibility Unless(bool condition) => condition ? Visibility.Collapsed : Visibility.Visible;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _navigator.Attach(PageFrame);
        _scans.LibraryChanged += OnLibraryChanged;
        if (PageFrame.Content is null)
        {
            Nav.SelectedItem = Nav.MenuItems[0]; // Albums, the default sidebar page
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _scans.LibraryChanged -= OnLibraryChanged;
        _navigator.Detach(PageFrame);
    }

    /// <summary>Rows changed (a scan, a folder removed, a purge): the page showing reloads; the cached ones catch up when they come back.</summary>
    private void OnLibraryChanged(object? sender, EventArgs e) => (PageFrame.Content as ILibraryRefreshable)?.RefreshLibrary();

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection)
        {
            return;
        }

        if (args.IsSettingsSelected)
        {
            OpenSettings();
        }
        else if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            Navigate(tag);
        }
    }

    /// <summary>
    /// The pane item that is already selected, chosen again. A detail page leaves the selection where it was, and
    /// SelectionChanged does not fire for the selected item, so Playlists chosen from an album reached through a
    /// playlist did nothing (found by tools/check-playlists.ps1, T-67). Any other item is left to SelectionChanged.
    /// </summary>
    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (!ReferenceEquals(args.InvokedItemContainer, sender.SelectedItem))
        {
            return;
        }

        if (args.IsSettingsInvoked)
        {
            OpenSettings();
        }
        else if (args.InvokedItemContainer is NavigationViewItem { Tag: string tag })
        {
            Navigate(tag);
        }
    }

    /// <summary>The pane's settings item was chosen: the window opens the settings overlay (E6-S3).</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>
    /// Asks for the settings overlay, and puts the pane's selection back on the page that is showing: Settings is not a
    /// page in this frame any more (E6-S3), so leaving the settings item selected would say something untrue.
    /// </summary>
    public void OpenSettings()
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
        SyncSelectionToPage();
    }

    /// <summary>
    /// The library views behind a single menu button (<paramref name="minimal"/>) or as a strip of icons down the
    /// pane's left edge. Minimal is for the narrow shell (T-182, Q-55): there the sidebar sits under Now Playing and
    /// is short rather than narrow, and Phil found the strip of vertical buttons impossible to navigate in the
    /// height left over. The pane is closed on every switch, so a narrowed window does not open with the views
    /// spread over the page.
    /// </summary>
    public void UseMinimalNavigation(bool minimal)
    {
        // In LeftMinimal the NavigationView reserves no room for its own Back and menu buttons: it draws them over
        // the top of its content, and they covered every page's title (Phil, T-182's second review). Measured at a
        // 716 px window: the buttons span y 465..501, the content starts at about 459, and the Albums title sat at
        // 472..496 underneath them. So the content is pushed below the button row in minimal mode only; in
        // LeftCompact the pane's own column already keeps the buttons beside the page.
        ContentHost.Margin = minimal ? new Thickness(0, MinimalNavigationInset, 0, 0) : new Thickness(0);

        NavigationViewPaneDisplayMode mode = minimal ? NavigationViewPaneDisplayMode.LeftMinimal : NavigationViewPaneDisplayMode.LeftCompact;
        if (Nav.PaneDisplayMode == mode)
        {
            return;
        }

        Nav.PaneDisplayMode = mode;
        Nav.IsPaneOpen = false;
    }

    /// <summary>
    /// How far the page is pushed down in minimal navigation: the 42 px from the top of the content to the bottom of
    /// the Back and menu buttons, measured, plus a little clearance so a title does not sit touching them.
    /// </summary>
    private const double MinimalNavigationInset = 44;

    private void Navigate(string tag)
    {
        (Type page, object? parameter) = Routes[tag];
        if (PageFrame.Content?.GetType() == page && Equals(parameter, _currentParameter))
        {
            ClearSearch(); // the page the user asked for is already there; show it
            return;
        }

        PageFrame.Navigate(page, parameter, new EntranceNavigationTransitionInfo());
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        _currentParameter = e.Parameter;
        Nav.IsBackEnabled = PageFrame.CanGoBack;
        ClearSearch(); // a result opened, a pane item chosen, back: the page is the destination now

        SyncSelectionToPage();
    }

    /// <summary>A root page selects its pane item; a detail page leaves the selection where it was.</summary>
    private void SyncSelectionToPage()
    {
        object? item = Routes.FirstOrDefault(r => r.Value.Page == PageFrame.Content?.GetType() && Equals(r.Value.Parameter, _currentParameter)).Key is { } tag
            ? Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag)
            : null;
        if (item is null)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            Nav.SelectedItem = item;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnBackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args) => GoBack();

    private void OnBackAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => args.Handled = GoBack();

    private void OnSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => args.Handled = FocusSearch();

    private bool GoBack()
    {
        if (!PageFrame.CanGoBack)
        {
            return false;
        }

        PageFrame.GoBack();
        return true;
    }

    private void OnPaneKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // "/" focuses the search box unless the user is typing somewhere.
        if (e.Key == SlashKey && !Modifiers.Control && FocusManager.GetFocusedElement(XamlRoot) is not TextBox)
        {
            e.Handled = FocusSearch();
        }
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Search.Text = sender.Text;

    /// <summary>The query icon (Enter is handled in <see cref="OnSearchKeyDown"/> so the modifiers are seen).</summary>
    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => Search.PlayFirstAsync().Forget("Search play first");

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                (Modifiers.Shift ? Search.PlayNextFirstAsync() : Modifiers.Control ? Search.EnqueueFirstAsync() : Search.PlayFirstAsync()).Forget("Search first result");
                e.Handled = true;
                break;
            case VirtualKey.Down:
                e.Handled = Results.FocusFirst();
                break;
            case VirtualKey.Escape when SearchBox.Text.Length > 0:
                ClearSearch();
                FocusPage();
                e.Handled = true;
                break;
        }
    }

    private void OnResultsEscape(object? sender, EventArgs e)
    {
        ClearSearch();
        FocusPage();
    }

    private void OnResultsBackToSearch(object? sender, EventArgs e) => FocusSearch();

    private bool FocusSearch() => SearchBox.Focus(FocusState.Keyboard);

    private void FocusPage() => (PageFrame.Content as Control)?.Focus(FocusState.Programmatic);

    private void ClearSearch()
    {
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = string.Empty; // TextChanged clears the view model
        }
    }
}
