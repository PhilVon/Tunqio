using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Library;

/// <summary>One item of a playlist, at <see cref="Position"/> (0-based, as the repository counts).</summary>
public sealed record PlaylistTrackRow(int Position, TrackDto Track, string Title, string Artists, string Album, string Duration)
{
    /// <summary>What a screen reader says for the row: the four columns that identify a track (accessibility contract).</summary>
    public string AutomationName => Title + " by " + Artists + ", " + Album + ", " + Duration;
}

/// <summary>
/// Playlist detail (E6-S1, docs/ui-screens-and-flows.md, "Playlist detail"): the ordered tracks with their total, play,
/// shuffle, rename, delete, remove and reorder. Every change goes to the repository and the page is read back from
/// it, so what is on the screen is what is stored.
/// </summary>
public sealed partial class PlaylistDetailViewModel : ObservableObject
{
    private readonly IPlaylistRepository _playlists;
    private readonly IPlaybackCommands _playback;
    private readonly ILibraryNavigator _navigator;
    private readonly IPlaylistFiles _files;
    private readonly IPlaylistFilePicker _filePicker;
    private long _id;

    /// <summary>What the last export did; empty until one has run.</summary>
    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNotice { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>"12 tracks · 48 min".</summary>
    [ObservableProperty]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<PlaylistTrackRow> Rows { get; set; } = [];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool NotFound { get; set; }

    public PlaylistDetailViewModel(
        IPlaylistRepository playlists, IPlaybackCommands playback, ILibraryNavigator navigator, IPlaylistFiles files, IPlaylistFilePicker filePicker)
    {
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(filePicker);
        _playlists = playlists;
        _playback = playback;
        _navigator = navigator;
        _files = files;
        _filePicker = filePicker;
    }

    /// <summary>Export M3U8 (E6-S2): asks where, suggesting the playlist's name, and writes paths relative to that folder.</summary>
    public async Task ExportAsync(CancellationToken ct = default)
    {
        if (NotFound)
        {
            return;
        }

        if (await _filePicker.PickSaveFileAsync(M3u8.FileNameFor(Name), ct) is not { } path)
        {
            return;
        }

        PlaylistExportResult? result = await _files.ExportAsync(_id, path, ct);
        SetNotice(result is null ? "This playlist no longer exists." : PlaylistFileText.Exported(result));
    }

    private void SetNotice(string text)
    {
        // Closed then opened, so a second export reopens a bar the user dismissed after the first.
        HasNotice = false;
        Notice = text;
        HasNotice = text.Length > 0;
    }

    public bool CanPlay => Rows.Count > 0;

    public async Task LoadAsync(long playlistId, CancellationToken ct = default)
    {
        _id = playlistId;
        PlaylistDetailDto? detail = await _playlists.GetDetailAsync(playlistId, ct);
        NotFound = detail is null;
        Name = detail?.Playlist.Name ?? "Playlist not found";
        Rows = detail is null
            ? []
            : [.. detail.Tracks.Select((t, i) => new PlaylistTrackRow(i, t, t.Title, t.ArtistNames, t.AlbumTitle ?? string.Empty, Format.Duration(t.DurationMs)))];
        Summary = detail is null ? string.Empty : PlaylistsViewModel.Describe(Rows.Count, Rows.Sum(r => (long)r.Track.DurationMs));
        IsEmpty = Rows.Count == 0;
        OnPropertyChanged(nameof(CanPlay));
    }

    public Task PlayAsync(CancellationToken ct = default) => CanPlay ? _playback.PlayNowAsync(Ids(), ct: ct) : Task.CompletedTask;

    public Task ShuffleAsync(CancellationToken ct = default) => CanPlay ? _playback.PlayNowAsync(Ids(), shuffle: true, ct: ct) : Task.CompletedTask;

    /// <summary>Plays the whole playlist with <paramref name="row"/> current, so the items before it stay behind it in the queue.</summary>
    public Task PlayFromAsync(PlaylistTrackRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _playback.PlayNowAsync(Ids(), row.Position, ct: ct);
    }

    /// <summary>Renames the playlist; a blank name does nothing.</summary>
    public async Task RenameAsync(string? name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        await _playlists.RenameAsync(_id, name, ct);
        await LoadAsync(_id, ct);
    }

    /// <summary>Deletes the playlist and goes back to the list. The page asks first.</summary>
    public async Task DeleteAsync(CancellationToken ct = default)
    {
        await _playlists.DeleteAsync(_id, ct);
        _navigator.OpenPlaylists();
    }

    public async Task RemoveAsync(IReadOnlyList<PlaylistTrackRow> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
        {
            return;
        }

        await _playlists.RemoveAtAsync(_id, [.. rows.Select(r => r.Position)], ct);
        await LoadAsync(_id, ct);
    }

    public Task MoveUpAsync(PlaylistTrackRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Position > 0 ? MoveAsync(row.Position, row.Position - 1, ct) : Task.CompletedTask;
    }

    public Task MoveDownAsync(PlaylistTrackRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Position < Rows.Count - 1 ? MoveAsync(row.Position, row.Position + 1, ct) : Task.CompletedTask;
    }

    /// <summary>
    /// Stores the order a drag left the list in. <paramref name="positions"/> is the list after the drop, as each row's
    /// position BEFORE it: [2, 0, 1] means the old third item is now first. A drag can move several selected rows at
    /// once, and the repository moves one item at a time, so the new order is replayed as single moves.
    /// </summary>
    public async Task ApplyOrderAsync(IReadOnlyList<int> positions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(positions);
        foreach ((int from, int to) in MovesFor(positions))
        {
            await _playlists.MoveAsync(_id, from, to, ct);
        }

        await LoadAsync(_id, ct);
    }

    /// <summary>
    /// The single moves that turn the order 0..n-1 into <paramref name="positions"/>: for each place in turn, the item that
    /// belongs there is moved in from wherever the earlier moves have left it. Pure, so the replay is testable.
    /// </summary>
    public static IReadOnlyList<(int From, int To)> MovesFor(IReadOnlyList<int> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        var current = Enumerable.Range(0, positions.Count).ToList();
        var moves = new List<(int From, int To)>();
        for (int place = 0; place < positions.Count; place++)
        {
            int at = current.IndexOf(positions[place]);
            if (at < 0)
            {
                throw new ArgumentException("positions must be a permutation of 0..n-1", nameof(positions));
            }

            if (at != place)
            {
                moves.Add((at, place));
                int item = current[at];
                current.RemoveAt(at);
                current.Insert(place, item);
            }
        }

        return moves;
    }

    private async Task MoveAsync(int from, int to, CancellationToken ct)
    {
        await _playlists.MoveAsync(_id, from, to, ct);
        await LoadAsync(_id, ct);
    }

    private long[] Ids() => [.. Rows.Select(r => r.Track.Id)];
}
