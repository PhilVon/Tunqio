using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>Where the Curation editor's left pane takes tracks from: the whole library, or a playlist.</summary>
public sealed record CurationSource(string Name, long? PlaylistId)
{
    public static CurationSource Library { get; } = new("Library", null);

    public override string ToString() => Name;
}

/// <summary>
/// Curation mode's dual pane (E5-S4, docs/ui-screens-and-flows.md, "Modes, defined precisely" and flow 6): a source list
/// on the left, the library or a playlist, and a target playlist on the right that tracks are dragged or added into,
/// reordered and removed from, with undo and redo. The target and its history are <see cref="PlaylistEditor"/>'s.
/// </summary>
/// <remarks>
/// One for the process, like the shell's mode: the playlist page's "Edit in Curation" names a playlist here before the
/// mode changes, and the pane that shows it is the window's.
/// The editor's state is copied into these properties after each awaited call rather than raised from the editor, whose
/// continuations run on the thread pool; the awaits here resume on the UI thread, so the copy is where the UI reads it.
/// </remarks>
public sealed partial class CurationViewModel : ObservableObject, IDisposable
{
    private readonly IPlaylistRepository _playlists;
    private readonly ITrackRepository _tracks;
    private readonly PlaylistEditor _editor;
    private long? _pendingEdit;
    private bool _sourceLoaded;
    private int _sourceVersion;
    private int _sourceSelectionCount;
    private int _targetSelectionCount;

    [ObservableProperty]
    public partial IReadOnlyList<PlaylistDto> Playlists { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<CurationSource> Sources { get; set; } = [CurationSource.Library];

    [ObservableProperty]
    public partial CurationSource Source { get; set; } = CurationSource.Library;

    [ObservableProperty]
    public partial string Filter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IList<TrackDto> SourceItems { get; set; } = [];

    [ObservableProperty]
    public partial bool SourceIsEmpty { get; set; }

    [ObservableProperty]
    public partial PlaylistDto? Target { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PlaylistTrackRow> TargetRows { get; set; } = [];

    /// <summary>"12 tracks · 48 min", or empty with no target.</summary>
    [ObservableProperty]
    public partial string TargetSummary { get; set; } = string.Empty;

    public CurationViewModel(IPlaylistRepository playlists, ITrackRepository tracks)
    {
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(tracks);
        _playlists = playlists;
        _tracks = tracks;
        _editor = new PlaylistEditor(playlists);
    }

    /// <summary>The query the library source pages through: album order, so an album's tracks sit together, filtered by <paramref name="text"/>.</summary>
    public static TrackQuery LibraryQuery(string? text) =>
        new(TrackSort.Album, Text: string.IsNullOrWhiteSpace(text) ? null : text.Trim(), PageSize: 200);

    public bool HasTarget => Target is not null;

    public bool NoTarget => Target is null;

    public bool TargetIsEmpty => Target is not null && TargetRows.Count == 0;

    public bool CanUndo => _editor.CanUndo;

    public bool CanRedo => _editor.CanRedo;

    /// <summary>"Undo add 6 tracks": the button's name says what it would do.</summary>
    public string UndoName => Name("Undo", _editor.UndoLabel);

    public string RedoName => Name("Redo", _editor.RedoLabel);

    /// <summary>How many source rows are selected; the pane reports it.</summary>
    public int SourceSelectionCount
    {
        get => _sourceSelectionCount;
        set
        {
            if (SetProperty(ref _sourceSelectionCount, value))
            {
                RaiseDerived();
            }
        }
    }

    /// <summary>How many target rows are selected; the pane reports it.</summary>
    public int TargetSelectionCount
    {
        get => _targetSelectionCount;
        set
        {
            if (SetProperty(ref _targetSelectionCount, value))
            {
                RaiseDerived();
            }
        }
    }

    /// <summary>The mode table's batch action bar: "visible, plus batch action bar when selection &gt; 1".</summary>
    public bool BatchBarVisible => SourceSelectionCount > 1 || TargetSelectionCount > 1;

    public bool CanAddSelection => HasTarget && SourceSelectionCount > 0;

    public bool CanEditSelection => HasTarget && TargetSelectionCount > 0;

    /// <summary>"6 selected in Library · 2 in Sunday".</summary>
    public string BatchText
    {
        get
        {
            var parts = new List<string>(2);
            if (SourceSelectionCount > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{SourceSelectionCount} selected in {Source.Name}"));
            }

            if (TargetSelectionCount > 0)
            {
                parts.Add(string.Create(CultureInfo.CurrentCulture, $"{TargetSelectionCount} selected in {Target?.Name}"));
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Names the playlist the next <see cref="ActivateAsync"/> opens as the target: "Edit in Curation".</summary>
    public void RequestEdit(long playlistId) => _pendingEdit = playlistId;

    /// <summary>
    /// Curation was entered. Re-reads the playlists (one may have been made, renamed or deleted from its page), opens the
    /// playlist asked for by <see cref="RequestEdit"/>, else keeps the target with its history, else takes the first.
    /// </summary>
    public async Task ActivateAsync(CancellationToken ct = default)
    {
        await RefreshPlaylistsAsync(ct);
        long? wanted = _pendingEdit ?? Target?.Id;
        _pendingEdit = null;
        PlaylistDto? pick = Playlists.FirstOrDefault(p => p.Id == wanted) ?? (Playlists.Count > 0 ? Playlists[0] : null);
        if (pick is null)
        {
            _editor.Close();
        }
        else if (pick.Id == _editor.Playlist?.Id)
        {
            await _editor.ReloadAsync(ct);
        }
        else
        {
            await _editor.OpenAsync(pick.Id, ct);
        }

        SyncTarget();
        await LoadSourceAsync(ct);
    }

    public async Task SelectTargetAsync(long? playlistId, CancellationToken ct = default)
    {
        if (playlistId == _editor.Playlist?.Id)
        {
            return;
        }

        if (playlistId is { } id)
        {
            await _editor.OpenAsync(id, ct);
        }
        else
        {
            _editor.Close();
        }

        SyncTarget();
    }

    public async Task SelectSourceAsync(CurationSource source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source == Source && _sourceLoaded)
        {
            return;
        }

        Source = source;
        await LoadSourceAsync(ct);
    }

    public async Task SetFilterAsync(string? text, CancellationToken ct = default)
    {
        string filter = text?.Trim() ?? string.Empty;
        if (filter == Filter && _sourceLoaded)
        {
            return;
        }

        Filter = filter;
        await LoadSourceAsync(ct);
    }

    /// <summary>Creates a playlist and makes it the target; a blank name does nothing.</summary>
    public async Task<bool> CreatePlaylistAsync(string? name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        PlaylistDto created = await _playlists.CreateAsync(name, ct);
        await RefreshPlaylistsAsync(ct);
        await _editor.OpenAsync(created.Id, ct);
        SyncTarget();
        return true;
    }

    /// <summary>Adds <paramref name="trackIds"/> to the target at <paramref name="position"/>, or at the end.</summary>
    public async Task<bool> AddAsync(IReadOnlyList<long> trackIds, int? position = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        if (trackIds.Count == 0)
        {
            return false;
        }

        bool changed = await _editor.AddAsync(trackIds, position, ct);
        await AfterEditAsync(ct);
        return changed;
    }

    public async Task<bool> RemoveAsync(IReadOnlyList<PlaylistTrackRow> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        bool changed = await _editor.RemoveAsync([.. rows.Select(r => r.Position)], ct);
        await AfterEditAsync(ct);
        return changed;
    }

    /// <summary>The order a drag left the target in, as each row's position before it.</summary>
    public async Task<bool> ReorderAsync(IReadOnlyList<int> order, CancellationToken ct = default)
    {
        bool changed = await _editor.ReorderAsync(order, ct);
        await AfterEditAsync(ct);
        return changed;
    }

    public async Task<bool> MoveAsync(IReadOnlyList<PlaylistTrackRow> rows, int delta, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        bool changed = await _editor.MoveAsync([.. rows.Select(r => r.Position)], delta, ct);
        await AfterEditAsync(ct);
        return changed;
    }

    public async Task<bool> UndoAsync(CancellationToken ct = default)
    {
        bool changed = await _editor.UndoAsync(ct);
        await AfterEditAsync(ct);
        return changed;
    }

    public async Task<bool> RedoAsync(CancellationToken ct = default)
    {
        bool changed = await _editor.RedoAsync(ct);
        await AfterEditAsync(ct);
        return changed;
    }

    public void Dispose() => _editor.Dispose();

    private static string Name(string verb, string? label) =>
        label is null ? verb : verb + " " + char.ToLower(label[0], CultureInfo.CurrentCulture) + label[1..];

    private static bool Matches(TrackDto track, string text) =>
        track.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase)
        || track.ArtistNames.Contains(text, StringComparison.CurrentCultureIgnoreCase)
        || (track.AlbumTitle ?? string.Empty).Contains(text, StringComparison.CurrentCultureIgnoreCase);

    private async Task AfterEditAsync(CancellationToken ct)
    {
        SyncTarget();
        if (Source.PlaylistId is { } source && source == Target?.Id)
        {
            await LoadSourceAsync(ct); // the source is the playlist being edited, so it changed too
        }
    }

    private async Task RefreshPlaylistsAsync(CancellationToken ct)
    {
        Playlists = await _playlists.ListAsync(ct);
        Sources = [CurationSource.Library, .. Playlists.Select(p => new CurationSource(p.Name, p.Id))];
        Source = Sources.FirstOrDefault(s => s.PlaylistId == Source.PlaylistId) ?? CurationSource.Library;
    }

    private async Task LoadSourceAsync(CancellationToken ct)
    {
        _sourceLoaded = true;
        int version = ++_sourceVersion;
        string? text = Filter.Length == 0 ? null : Filter;
        if (Source.PlaylistId is { } id)
        {
            PlaylistDetailDto? detail = await _playlists.GetDetailAsync(id, ct);
            List<TrackDto> matching = detail is null ? [] : [.. detail.Tracks.Where(t => text is null || Matches(t, text))];
            ShowSource(version, matching);
        }
        else
        {
            IncrementalItemsSource<TrackDto> paged = IncrementalItemsSource.Tracks(_tracks, LibraryQuery(text));
            await paged.LoadMoreAsync(ct);
            ShowSource(version, paged);
        }
    }

    private void ShowSource(int version, IList<TrackDto> items)
    {
        if (version != _sourceVersion)
        {
            return; // a newer filter or source has been asked for since; its answer is the one to show
        }

        SourceItems = items;
        SourceIsEmpty = items.Count == 0;
        SourceSelectionCount = 0;
    }

    private void SyncTarget()
    {
        Target = _editor.Playlist;
        TargetRows = [.. _editor.Tracks.Select((t, i) => new PlaylistTrackRow(i, t, t.Title, t.ArtistNames, t.AlbumTitle ?? string.Empty, Format.Duration(t.DurationMs)))];
        TargetSummary = Target is null ? string.Empty : PlaylistsViewModel.Describe(TargetRows.Count, TargetRows.Sum(r => (long)r.Track.DurationMs));
        TargetSelectionCount = 0;
        RaiseDerived();
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(HasTarget));
        OnPropertyChanged(nameof(NoTarget));
        OnPropertyChanged(nameof(TargetIsEmpty));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoName));
        OnPropertyChanged(nameof(RedoName));
        OnPropertyChanged(nameof(BatchBarVisible));
        OnPropertyChanged(nameof(CanAddSelection));
        OnPropertyChanged(nameof(CanEditSelection));
        OnPropertyChanged(nameof(BatchText));
    }
}
