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
    private bool _syncingSelection;
    private object? _currentParameter;

    public LibraryPane()
    {
        _navigator = App.Services.GetRequiredService<LibraryNavigator>();
        Search = App.Services.GetRequiredService<SearchViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _navigator.Detach(PageFrame);
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
        if (PageFrame.Content is null)
        {
            Nav.SelectedItem = Nav.MenuItems[0]; // Albums, the default sidebar page
        }
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_syncingSelection && args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            Navigate(tag);
        }
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

        // A root page selects its pane item; a detail page leaves the selection where it was.
        string? tag = Routes.FirstOrDefault(r => r.Value.Page == e.SourcePageType && Equals(r.Value.Parameter, e.Parameter)).Key;
        if (tag is null)
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            Nav.SelectedItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);
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
