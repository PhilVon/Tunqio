using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;
using Windows.Foundation;
using Windows.System;

namespace Tunqio.App.Library;

/// <summary>Code-behind for a playlist's page; the parameter is the playlist id.</summary>
public sealed partial class PlaylistDetailPage : Page, ILibraryRefreshable
{
    private ObservableCollection<PlaylistTrackRow> _items = [];
    private PlaylistTrackRow? _menuAnchor;
    private long? _playlistId;

    public PlaylistDetailPage()
    {
        ViewModel = App.Services.GetRequiredService<PlaylistDetailViewModel>();
        InitializeComponent();
    }

    public PlaylistDetailViewModel ViewModel { get; }

    public static bool Not(bool value) => !value;

    public static Visibility Unless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static string EmptyTitle(bool notFound) => notFound ? "This playlist no longer exists" : "This playlist is empty";

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is long playlistId)
        {
            _playlistId = playlistId;
            RunThenRebind(() => ViewModel.LoadAsync(playlistId), "Playlist load");
        }
    }

    public void RefreshLibrary()
    {
        if (_playlistId is { } playlistId)
        {
            RunThenRebind(() => ViewModel.LoadAsync(playlistId), "Playlist refresh");
        }
    }

    // The list binds a collection of its own, because the ListView's reorder moves rows in the collection it is given;
    // after every change the rows are read back from the view model, which read them back from the repository.
    private void Rebind()
    {
        _items = new ObservableCollection<PlaylistTrackRow>(ViewModel.Rows);
        List.ItemsSource = _items;
    }

    private void RunThenRebind(Func<Task> work, string what) => RunThenRebindAsync(work).Forget(what);

    private async Task RunThenRebindAsync(Func<Task> work)
    {
        await work();
        Rebind();
    }

    private void OnPlay(object sender, RoutedEventArgs e) => ViewModel.PlayAsync().Forget("Play playlist");

    private void OnShuffle(object sender, RoutedEventArgs e) => ViewModel.ShuffleAsync().Forget("Shuffle playlist");

    private void OnRename(object sender, RoutedEventArgs e) => RenameAsync().Forget("Rename playlist");

    private async Task RenameAsync()
    {
        string? name = await PlaylistDialogs.AskNameAsync(XamlRoot, "Rename playlist", ViewModel.Name, "Rename");
        await ViewModel.RenameAsync(name);
    }

    private void OnDelete(object sender, RoutedEventArgs e) => DeleteAsync().Forget("Delete playlist");

    private void OnExport(object sender, RoutedEventArgs e) => ViewModel.ExportAsync().Forget("Export playlist");

    /// <summary>Names this playlist as Curation's target, then switches mode; entering Curation opens it (E5-S4).</summary>
    private void OnEditInCuration(object sender, RoutedEventArgs e)
    {
        if (_playlistId is not { } playlistId)
        {
            return;
        }

        App.Services.GetRequiredService<CurationViewModel>().RequestEdit(playlistId);
        var shell = App.Services.GetRequiredService<Shell.ShellState>();
        if (!shell.Select(Shell.ShellMode.Curation))
        {
            App.Services.GetRequiredService<CurationViewModel>().ActivateAsync().Forget("Curation edit");
        }
    }

    private async Task DeleteAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Delete " + ViewModel.Name + "?",
            Content = "The playlist is deleted. Its tracks stay in the library.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.DeleteAsync();
        }
    }

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move)
        {
            return;
        }

        int[] order = [.. _items.Select(r => r.Position)];
        RunThenRebind(() => ViewModel.ApplyOrderAsync(order), "Reorder playlist");
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (RowOf(e.OriginalSource) is { } row)
        {
            ViewModel.PlayFromAsync(row).Forget("Play playlist from row");
        }
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter when (RowOf(e.OriginalSource) ?? List.SelectedItem as PlaylistTrackRow) is { } row:
                ViewModel.PlayFromAsync(row).Forget("Play playlist from row");
                e.Handled = true;
                break;
            case VirtualKey.Delete when Selected(null) is { Count: > 0 } rows:
                RunThenRebind(() => ViewModel.RemoveAsync(rows), "Remove from playlist");
                e.Handled = true;
                break;
        }
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (RowOf(args.OriginalSource) is not { } row)
        {
            return;
        }

        if (!List.SelectedItems.Contains(row))
        {
            List.SelectedItem = row;
        }

        _menuAnchor = row;
        if (args.TryGetPosition(List, out Point point))
        {
            RowMenu.ShowAt(List, point);
        }
        else if (args.OriginalSource is FrameworkElement element)
        {
            RowMenu.ShowAt(element);
        }

        args.Handled = true;
    }

    private void OnMenuPlay(object sender, RoutedEventArgs e)
    {
        if (_menuAnchor is { } row)
        {
            ViewModel.PlayFromAsync(row).Forget("Play playlist from row");
        }
    }

    private void OnMenuMoveUp(object sender, RoutedEventArgs e)
    {
        if (_menuAnchor is { } row)
        {
            RunThenRebind(() => ViewModel.MoveUpAsync(row), "Move playlist item up");
        }
    }

    private void OnMenuMoveDown(object sender, RoutedEventArgs e)
    {
        if (_menuAnchor is { } row)
        {
            RunThenRebind(() => ViewModel.MoveDownAsync(row), "Move playlist item down");
        }
    }

    private void OnMenuRemove(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<PlaylistTrackRow> rows = Selected(_menuAnchor);
        RunThenRebind(() => ViewModel.RemoveAsync(rows), "Remove from playlist");
    }

    /// <summary>The selected rows in list order, or just <paramref name="anchor"/> when nothing is selected.</summary>
    private List<PlaylistTrackRow> Selected(PlaylistTrackRow? anchor)
    {
        var rows = new List<PlaylistTrackRow>();
        foreach (ItemIndexRange range in List.SelectedRanges)
        {
            for (int i = range.FirstIndex; i <= range.LastIndex; i++)
            {
                if (List.Items[i] is PlaylistTrackRow row)
                {
                    rows.Add(row);
                }
            }
        }

        if (rows.Count == 0 && anchor is not null)
        {
            rows.Add(anchor);
        }

        return rows;
    }

    private PlaylistTrackRow? RowOf(object? element)
    {
        for (DependencyObject? node = element as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewItem container)
            {
                return List.ItemFromContainer(container) as PlaylistTrackRow;
            }
        }

        return (element as FrameworkElement)?.DataContext as PlaylistTrackRow;
    }
}
