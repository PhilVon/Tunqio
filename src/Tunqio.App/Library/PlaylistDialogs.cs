using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>
/// The playlist dialogs (E6-S1, docs/ui-screens-and-flows.md, "Dialogs: Playlist name"): a name for a new or renamed
/// playlist, and Add to playlist, which picks one or makes one. Built in code, like Remove folder's confirmation: each
/// is a text box or a list in a <see cref="ContentDialog"/>, and a dialog needs the page's <see cref="XamlRoot"/>.
/// </summary>
internal static class PlaylistDialogs
{
    /// <summary>Asks for a playlist name; null when cancelled. The action button waits for something that is not blank.</summary>
    public static async Task<string?> AskNameAsync(XamlRoot root, string title, string initial, string action)
    {
        ArgumentNullException.ThrowIfNull(root);
        var box = new TextBox { Text = initial, PlaceholderText = "Playlist name" };
        AutomationProperties.SetName(box, "Playlist name");
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = initial.Trim().Length > 0,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        box.Loaded += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    /// <summary>Add to playlist from an album tile: the album's tracks in disc and track order.</summary>
    public static async Task AddAlbumToPlaylistAsync(XamlRoot root, AlbumDto album)
    {
        ArgumentNullException.ThrowIfNull(album);
        AlbumDetailDto? detail = await App.Services.GetRequiredService<IAlbumRepository>().GetDetailAsync(album.Id);
        if (detail is not null)
        {
            await AddToPlaylistAsync(root, [.. detail.Tracks.Select(t => t.Id)]);
        }
    }

    /// <summary>
    /// Adds <paramref name="trackIds"/> to a playlist the user picks, or to a new one they name. Nothing happens when
    /// they cancel or there is nothing to add.
    /// </summary>
    public static async Task AddToPlaylistAsync(XamlRoot root, IReadOnlyList<long> trackIds)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(trackIds);
        if (trackIds.Count == 0)
        {
            return;
        }

        IPlaylistRepository repository = App.Services.GetRequiredService<IPlaylistRepository>();
        IReadOnlyList<PlaylistDto> playlists = await repository.ListAsync();

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            MaxHeight = 280,
            ItemsSource = playlists.Select(p => p.Name).ToList(),
        };
        AutomationProperties.SetName(list, "Playlists");
        var name = new TextBox { Header = playlists.Count > 0 ? "Or a new playlist" : "New playlist", PlaceholderText = "Playlist name" };
        AutomationProperties.SetName(name, "New playlist name");
        var panel = new StackPanel { Spacing = 12 };
        if (playlists.Count > 0)
        {
            panel.Children.Add(list);
        }

        panel.Children.Add(name);
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = trackIds.Count == 1
                ? "Add 1 track to a playlist"
                : string.Create(CultureInfo.CurrentCulture, $"Add {trackIds.Count} tracks to a playlist"),
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        void Update() => dialog.IsPrimaryButtonEnabled = name.Text.Trim().Length > 0 || list.SelectedIndex >= 0;
        list.SelectionChanged += (_, _) => Update();
        name.TextChanged += (_, _) => Update();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        // A typed name wins over a selection: it is the more deliberate of the two.
        long target = name.Text.Trim().Length > 0
            ? (await repository.CreateAsync(name.Text)).Id
            : playlists[list.SelectedIndex].Id;
        await repository.AddTracksAsync(target, trackIds);
        Serilog.Log.Debug("Added {Count} track(s) to playlist {PlaylistId}", trackIds.Count, target);
    }
}
