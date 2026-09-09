using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace Tunqio.App.Library;

/// <summary>Code-behind for the library pane; see the XAML for the design notes.</summary>
public sealed partial class LibraryPane : UserControl
{
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
        InitializeComponent();
        _navigator = App.Services.GetRequiredService<LibraryNavigator>();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _navigator.Detach(PageFrame);
    }

    /// <summary>The frame the pages live in (the shell's Alt+Left target).</summary>
    public Frame Frame => PageFrame;

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
            return;
        }

        PageFrame.Navigate(page, parameter, new EntranceNavigationTransitionInfo());
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        _currentParameter = e.Parameter;
        Nav.IsBackEnabled = PageFrame.CanGoBack;

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

    private bool GoBack()
    {
        if (!PageFrame.CanGoBack)
        {
            return false;
        }

        PageFrame.GoBack();
        return true;
    }
}
