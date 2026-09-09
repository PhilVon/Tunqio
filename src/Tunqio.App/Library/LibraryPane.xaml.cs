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

    /// <summary>The "," key (VK_OEM_COMMA), for Ctrl+, (docs/ui-screens-and-flows.md, "Keyboard shortcuts": Settings).</summary>
    private const VirtualKey CommaKey = (VirtualKey)188;

    /// <summary>The root page of each pane item; a Tracks page is told which fixed view it is.</summary>
    private static readonly IReadOnlyDictionary<string, (Type Page, object? Parameter)> Routes = new Dictionary<string, (Type, object?)>(StringComparer.Ordinal)
    {
        ["albums"] = (typeof(AlbumsPage), null),
        ["artists"] = (typeof(ArtistsPage), null),
        ["tracks"] = (typeof(TracksPage), TracksSpec.All),
        ["genres"] = (typeof(GenresPage), null),
        ["folders"] = (typeof(FoldersPage), null),
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
        var settings = new KeyboardAccelerator { Key = CommaKey, Modifiers = VirtualKeyModifiers.Control };
        settings.Invoked += OnSettingsAccelerator;
        KeyboardAccelerators.Add(settings);
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

    /// <summary>Settings › Library in the frame (E3-S12); already there, it is shown (the search cleared).</summary>
    public void OpenSettings()
    {
        if (PageFrame.Content is LibrarySettingsPage)
        {
            ClearSearch();
            return;
        }

        PageFrame.Navigate(typeof(LibrarySettingsPage), null, new EntranceNavigationTransitionInfo());
    }

    private void OnSettingsAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        OpenSettings();
        args.Handled = true;
    }

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

        // A root page (or settings) selects its pane item; a detail page leaves the selection where it was.
        object? item = e.SourcePageType == typeof(LibrarySettingsPage)
            ? Nav.SettingsItem
            : Routes.FirstOrDefault(r => r.Value.Page == e.SourcePageType && Equals(r.Value.Parameter, e.Parameter)).Key is { } tag
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
