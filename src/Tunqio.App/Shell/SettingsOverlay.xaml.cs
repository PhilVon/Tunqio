using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Tunqio.App.Library;

namespace Tunqio.App.Shell;

/// <summary>Code-behind for the settings overlay; see the XAML for the design notes.</summary>
public sealed partial class SettingsOverlay : UserControl
{
    /// <summary>Each section's page, by the tag its item carries.</summary>
    public static readonly IReadOnlyDictionary<string, Type> Pages = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["playback"] = typeof(PlaybackSettingsPage),
        ["output"] = typeof(OutputSettingsPage),
        ["library"] = typeof(LibrarySettingsPage),
        ["appearance"] = typeof(AppearanceSettingsPage),
        ["visualization"] = typeof(VisualizationSettingsPage),
    };

    public SettingsOverlay()
    {
        InitializeComponent();
    }

    /// <summary>Close or Esc: the window hides the overlay and gives the shell back.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Shows <paramref name="section"/>, or the section last shown, or Playback the first time. Called by the window as it
    /// makes the overlay visible.
    /// </summary>
    public void Show(string? section = null)
    {
        string tag = section is not null && Pages.ContainsKey(section)
            ? section
            : (Sections.SelectedItem as NavigationViewItem)?.Tag as string ?? "playback";
        NavigationViewItem item = Sections.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == tag);
        if (!ReferenceEquals(Sections.SelectedItem, item))
        {
            Sections.SelectedItem = item; // navigates, through OnSectionChanged
        }
        else if (SectionFrame.Content is null)
        {
            Navigate(tag);
        }

        item.Focus(FocusState.Programmatic);
    }

    /// <summary>The tag of the section showing, for a check or a caller that wants to reopen it.</summary>
    public string? CurrentSection => (Sections.SelectedItem as NavigationViewItem)?.Tag as string;

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnSectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag })
        {
            Navigate(tag);
        }
    }

    private void Navigate(string tag)
    {
        if (SectionFrame.Content?.GetType() != Pages[tag])
        {
            SectionFrame.Navigate(Pages[tag], null, new EntranceNavigationTransitionInfo());
        }
    }
}
