using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tunqio.App.Controls;
using Tunqio.Core.Library;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace Tunqio.App.Library;

/// <summary>Code-behind for Curation's dual pane; see the XAML for the design notes and <see cref="CurationViewModel"/> for the rules.</summary>
public sealed partial class CurationPane : UserControl
{
    /// <summary>
    /// The data package property a source drag carries: the track ids, comma-separated. A private key rather than text, so
    /// a drag from the source means nothing to another app, or to the shell's own drop-to-play.
    /// </summary>
    internal const string TrackIdsKey = "Tunqio.CurationTrackIds";

    private ObservableCollection<PlaylistTrackRow> _targetItems = [];
    private CancellationTokenSource? _filterDelay;
    private int[]? _reselect;
    private bool _syncing;

    public CurationPane()
    {
        ViewModel = App.Services.GetRequiredService<CurationViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelChanged;
        // handledEventsToo: the ListView's own reorder handling sees a drag over it first.
        TargetList.AddHandler(DragOverEvent, new DragEventHandler(OnTargetDragOver), handledEventsToo: true);
        TargetList.AddHandler(DropEvent, new DragEventHandler(OnTargetDrop), handledEventsToo: true);
    }

    public CurationViewModel ViewModel { get; }

    public static string Duration(int durationMs) => Format.Duration(durationMs);

    /// <summary>What a screen reader says for a source row: the four columns that identify a track (accessibility contract).</summary>
    public static string RowName(string title, string artists, string? album, int durationMs) =>
        title + " by " + artists + ", " + (album ?? string.Empty) + ", " + Format.Duration(durationMs);

    /// <summary>Curation was entered: re-read the playlists and the target.</summary>
    public void Activate() => ViewModel.ActivateAsync().Forget("Curation activate");

    /// <summary>Ctrl+Z from the shell. False when there is nothing to undo, which leaves the key unhandled.</summary>
    public bool Undo()
    {
        if (!ViewModel.CanUndo)
        {
            return false;
        }

        ViewModel.UndoAsync().Forget("Curation undo");
        return true;
    }

    /// <summary>Ctrl+Y from the shell.</summary>
    public bool Redo()
    {
        if (!ViewModel.CanRedo)
        {
            return false;
        }

        ViewModel.RedoAsync().Forget("Curation redo");
        return true;
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CurationViewModel.TargetRows):
                RebindTarget();
                break;
            case nameof(CurationViewModel.Playlists):
            case nameof(CurationViewModel.Target):
                Sync(() => TargetPicker.SelectedItem = ViewModel.Playlists.FirstOrDefault(p => p.Id == ViewModel.Target?.Id));
                break;
            case nameof(CurationViewModel.Sources):
            case nameof(CurationViewModel.Source):
                Sync(() => SourcePicker.SelectedItem = ViewModel.Sources.FirstOrDefault(s => s.PlaylistId == ViewModel.Source.PlaylistId));
                break;
        }
    }

    private void Sync(Action set)
    {
        _syncing = true;
        try
        {
            set();
        }
        finally
        {
            _syncing = false;
        }
    }

    // The target binds a collection of its own, because the ListView's reorder moves rows in the collection it is given;
    // after every change the rows come back from the view model, which read them back from the repository.
    private void RebindTarget()
    {
        _targetItems = new ObservableCollection<PlaylistTrackRow>(ViewModel.TargetRows);
        TargetList.ItemsSource = _targetItems;
        if (_reselect is { } positions)
        {
            _reselect = null;
            foreach (int position in positions.Where(p => p >= 0 && p < _targetItems.Count))
            {
                TargetList.SelectRange(new ItemIndexRange(position, 1));
            }
        }
    }

    // ---- source ------------------------------------------------------------------------------------------------------

    private void OnSourcePicked(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && SourcePicker.SelectedItem is CurationSource source)
        {
            ViewModel.SelectSourceAsync(source).Forget("Curation source");
        }
    }

    /// <summary>A short pause after the last keystroke, so a word typed is one query and not one per letter.</summary>
    private void OnFilterChanged(object sender, TextChangedEventArgs e) => FilterAfterPauseAsync(FilterBox.Text).Forget("Curation filter");

    private async Task FilterAfterPauseAsync(string text)
    {
        if (_filterDelay is { } previous)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        var delay = new CancellationTokenSource();
        _filterDelay = delay;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), delay.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await ViewModel.SetFilterAsync(text);
    }

    private void OnSourceSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SourceSelectionCount = SourceList.SelectedItems.Count;

    private void OnSourceDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        IReadOnlyList<TrackDto> tracks = CurationViewModel.DraggedTracks([.. e.Items.OfType<TrackDto>()], SelectedSource());
        if (tracks.Count == 0 || !ViewModel.HasTarget)
        {
            e.Cancel = true;
            return;
        }

        e.Data.Properties[TrackIdsKey] = string.Join(',', tracks.Select(t => t.Id.ToString(CultureInfo.InvariantCulture)));
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void OnSourceDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SourceRowOf(e.OriginalSource) is { } track)
        {
            AddTimed([track.Id], null, "double-click");
        }
    }

    private void OnSourceKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && SelectedSource() is { Count: > 0 } tracks)
        {
            AddTimed([.. tracks.Select(t => t.Id)], null, "Enter");
            e.Handled = true;
        }
    }

    private void OnAddSelected(object sender, RoutedEventArgs e)
    {
        if (SelectedSource() is { Count: > 0 } tracks)
        {
            AddTimed([.. tracks.Select(t => t.Id)], null, "Add");
        }
    }

    /// <summary>The selected source rows in list order.</summary>
    private List<TrackDto> SelectedSource()
    {
        var tracks = new List<TrackDto>();
        foreach (ItemIndexRange range in SourceList.SelectedRanges)
        {
            for (int i = range.FirstIndex; i <= range.LastIndex; i++)
            {
                if (SourceList.Items[i] is TrackDto track)
                {
                    tracks.Add(track);
                }
            }
        }

        return tracks;
    }

    private TrackDto? SourceRowOf(object? element)
    {
        for (DependencyObject? node = element as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewItem container)
            {
                return SourceList.ItemFromContainer(container) as TrackDto;
            }
        }

        return null;
    }

    /// <summary>
    /// Adds and logs how long it took from the gesture to the target showing the result: the write, the read-back and the
    /// rebind (AC-140 is about a drag of 500 finishing inside a second, and this is the figure a check reads).
    /// </summary>
    private void AddTimed(IReadOnlyList<long> ids, int? position, string how) => AddTimedAsync(ids, position, how).Forget("Curation add");

    private async Task AddTimedAsync(IReadOnlyList<long> ids, int? position, string how)
    {
        var watch = Stopwatch.StartNew();
        await ViewModel.AddAsync(ids, position);
        Serilog.Log.Debug("Curation added {Count} track(s) by {How} in {Ms} ms", ids.Count, how, watch.ElapsedMilliseconds);
    }

    // ---- target ------------------------------------------------------------------------------------------------------

    private void OnTargetPicked(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing && TargetPicker.SelectedItem is PlaylistDto playlist)
        {
            ViewModel.SelectTargetAsync(playlist.Id).Forget("Curation target");
        }
    }

    private void OnNewPlaylist(object sender, RoutedEventArgs e) => NewPlaylistAsync().Forget("Curation new playlist");

    private async Task NewPlaylistAsync()
    {
        string? name = await PlaylistDialogs.AskNameAsync(XamlRoot, "New playlist", string.Empty, "Create");
        await ViewModel.CreatePlaylistAsync(name);
    }

    private void OnExport(object sender, RoutedEventArgs e) => ViewModel.ExportAsync().Forget("Curation export");

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void OnTargetSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.TargetSelectionCount = TargetList.SelectedItems.Count;

    private void OnTargetDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.ContainsKey(TrackIdsKey))
        {
            return; // the list's own reorder, or something that is not ours
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to " + ViewModel.Target?.Name;
        e.Handled = true;
    }

    private void OnTargetDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Properties.TryGetValue(TrackIdsKey, out object? value) || value is not string text)
        {
            return;
        }

        long[] ids = [.. text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => long.Parse(s, CultureInfo.InvariantCulture))];
        e.Handled = true;
        AddTimed(ids, DropPosition(e.GetPosition(TargetList)), "drag");
    }

    /// <summary>
    /// The row a drop at <paramref name="point"/> goes before: the first realised row whose middle is below it. Past every
    /// realised row it goes after the last of them, and into an empty or unrealised list it appends (null).
    /// </summary>
    private int? DropPosition(Point point)
    {
        int? lastAbove = null;
        for (int i = 0; i < _targetItems.Count; i++)
        {
            if (TargetList.ContainerFromIndex(i) is not FrameworkElement container)
            {
                continue;
            }

            Point top = container.TransformToVisual(TargetList).TransformPoint(new Point(0, 0));
            if (point.Y < top.Y + container.ActualHeight / 2)
            {
                return i;
            }

            lastAbove = i;
        }

        return lastAbove is { } last && last < _targetItems.Count - 1 ? last + 1 : null;
    }

    private void OnTargetDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != DataPackageOperation.Move)
        {
            return;
        }

        int[] order = [.. _targetItems.Select(r => r.Position)];
        ViewModel.ReorderAsync(order).Forget("Curation reorder");
    }

    private void OnTargetKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Delete when SelectedTarget().Count > 0:
                RemoveSelected();
                e.Handled = true;
                break;
            case VirtualKey.Up when alt && SelectedTarget().Count > 0:
                MoveSelected(-1);
                e.Handled = true;
                break;
            case VirtualKey.Down when alt && SelectedTarget().Count > 0:
                MoveSelected(+1);
                e.Handled = true;
                break;
        }
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void OnRemoveSelected(object sender, RoutedEventArgs e) => RemoveSelected();

    /// <summary>Moves the selection a place, and keeps it selected where it went so the next press moves it again.</summary>
    private void MoveSelected(int delta)
    {
        List<PlaylistTrackRow> rows = SelectedTarget();
        if (rows.Count == 0)
        {
            return;
        }

        _reselect = PlaylistEditor.ShiftedPositions(_targetItems.Count, [.. rows.Select(r => r.Position)], delta);
        ViewModel.MoveAsync(rows, delta).Forget("Curation move");
    }

    private void RemoveSelected()
    {
        List<PlaylistTrackRow> rows = SelectedTarget();
        if (rows.Count > 0)
        {
            ViewModel.RemoveAsync(rows).Forget("Curation remove");
        }
    }

    /// <summary>The selected target rows in list order.</summary>
    private List<PlaylistTrackRow> SelectedTarget()
    {
        var rows = new List<PlaylistTrackRow>();
        foreach (ItemIndexRange range in TargetList.SelectedRanges)
        {
            for (int i = range.FirstIndex; i <= range.LastIndex; i++)
            {
                if (TargetList.Items[i] is PlaylistTrackRow row)
                {
                    rows.Add(row);
                }
            }
        }

        return rows;
    }
}
