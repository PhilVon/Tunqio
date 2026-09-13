using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>Code-behind for the Tracks views; the parameter is a <see cref="TracksSpec"/>.</summary>
public sealed partial class TracksPage : Page, ILibraryRefreshable
{
    public TracksPage()
    {
        ViewModel = App.Services.GetRequiredService<TracksViewModel>();
        InitializeComponent();
        foreach ((TrackColumn column, string label) in TracksViewModel.HideableColumns)
        {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = ViewModel.IsColumnVisible(column), Tag = column };
            item.Click += OnColumnToggled;
            ColumnsMenu.Items.Add(item);
            Table.SetColumnVisible(column, ViewModel.IsColumnVisible(column));
        }

        ViewModel.ColumnVisibilityChanged += (_, column) => Table.SetColumnVisible(column, ViewModel.IsColumnVisible(column));
    }

    public TracksViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        TracksSpec spec = e.Parameter as TracksSpec ?? TracksSpec.All;
        if (e.NavigationMode == NavigationMode.New || spec != ViewModel.Spec)
        {
            ViewModel.LoadAsync(spec).Forget("Tracks load");
        }
    }

    public void RefreshLibrary() => ViewModel.LoadAsync(ViewModel.Spec).Forget("Tracks refresh");

    private void OnColumnToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleMenuFlyoutItem { Tag: TrackColumn column } item)
        {
            ViewModel.SetColumnVisible(column, item.IsChecked);
        }
    }

    private void OnSortRequested(object? sender, TrackSort column) => ViewModel.SortBy(column);

    private void OnTrackAction(object? sender, TrackActionEventArgs e)
    {
        // Edit tags is the page's and not the view model's: it opens a dialog, which needs this page's XamlRoot.
        if (e.Action == TrackAction.EditTags)
        {
            TagEditorDialog.ShowAsync(XamlRoot, e.Tracks).Forget("Edit tags");
            return;
        }

        // Add to playlist (E6-S1) likewise opens a dialog.
        if (e.Action == TrackAction.AddToPlaylist)
        {
            PlaylistDialogs.AddToPlaylistAsync(XamlRoot, [.. e.Tracks.Select(t => t.Id)]).Forget("Add tracks to playlist");
            return;
        }

        ViewModel.HandleAsync(e.Action, e.Tracks, e.Anchor).Forget("Tracks " + e.Action);
    }
}
