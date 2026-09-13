namespace Tunqio.Core.Library;

/// <summary>
/// The Curation editor's target playlist and its undo and redo (E5-S4, flow 6). Every change is worked out as the whole
/// list it leaves, written with <see cref="IPlaylistRepository.ReplaceTracksAsync"/>, and read back; the list before
/// and the list read back are what one step of history holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why snapshots and not inverse operations.</b> The repository's operations are not closed under inversion: an add
/// appends, but undoing a remove means putting tracks back at positions in the middle, which nothing but a rewrite can
/// do. A snapshot makes every edit undoable by one write of the same shape, and a playlist is hundreds of ids, so two
/// copies per step is nothing. The history is capped at <see cref="Depth"/> steps all the same.
/// </para>
/// <para>
/// The list after an edit is the one <em>read back</em>, not the one asked for. The repository skips an id the library
/// no longer has, so the two can differ, and redo must restore what the playlist really was.
/// </para>
/// <para>
/// Operations are serialised: Ctrl+Z held down, or a drop landing while an undo is still writing, would otherwise read
/// the list while another edit was half way through it and record a step that never existed. It lives in Core rather
/// than beside the pane because nothing in it is UI, and so that <c>Tunqio.Benchmarks</c> can gate AC-140 on it.
/// </para>
/// </remarks>
public sealed class PlaylistEditor : IDisposable
{
    /// <summary>How many steps undo keeps.</summary>
    public const int Depth = 100;

    private readonly IPlaylistRepository _playlists;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinkedList<Step> _undo = new();
    private readonly Stack<Step> _redo = new();

    public PlaylistEditor(IPlaylistRepository playlists)
    {
        ArgumentNullException.ThrowIfNull(playlists);
        _playlists = playlists;
    }

    /// <summary>Raised after anything the editor shows has changed: the list, its name or the history.</summary>
    public event EventHandler? Changed;

    /// <summary>The playlist being edited, as it was last read; null before one is opened or when it has gone.</summary>
    public PlaylistDto? Playlist { get; private set; }

    /// <summary>The playlist's tracks in order, as last read back.</summary>
    public IReadOnlyList<TrackDto> Tracks { get; private set; } = [];

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>What Undo would undo ("Add 6 tracks"), or null.</summary>
    public string? UndoLabel => _undo.First?.Value.Label;

    /// <summary>What Redo would redo, or null.</summary>
    public string? RedoLabel => _redo.Count > 0 ? _redo.Peek().Label : null;

    /// <summary>Opens <paramref name="playlistId"/> with a fresh history; false when there is no such playlist.</summary>
    public Task<bool> OpenAsync(long playlistId, CancellationToken ct = default) => LockedAsync(async () =>
    {
        _undo.Clear();
        _redo.Clear();
        bool found = await ReadAsync(playlistId, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return found;
    });

    /// <summary>Closes the playlist and forgets its history.</summary>
    public void Close()
    {
        _undo.Clear();
        _redo.Clear();
        Playlist = null;
        Tracks = [];
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Re-reads the open playlist, keeping the history: the list may have been changed from somewhere else (its page,
    /// Add to playlist). A later undo still restores the snapshot it holds.
    /// </summary>
    public Task ReloadAsync(CancellationToken ct = default) => LockedAsync(async () =>
    {
        if (Playlist is { } playlist)
        {
            await ReadAsync(playlist.Id, ct).ConfigureAwait(false);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return true;
    });

    /// <summary>
    /// Inserts <paramref name="trackIds"/> at <paramref name="position"/> (clamped to the list), or appends them when it is
    /// null. A track already in the playlist is added again, as Add to playlist does.
    /// </summary>
    public Task<bool> AddAsync(IReadOnlyList<long> trackIds, int? position = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        return EditAsync(Describe("Add", trackIds.Count), ids => Insert(ids, trackIds, position), ct);
    }

    /// <summary>Removes the items at <paramref name="positions"/>; positions out of range are ignored.</summary>
    public Task<bool> RemoveAsync(IReadOnlyList<int> positions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(positions);
        return EditAsync(Describe("Remove", positions.Distinct().Count()), ids => Remove(ids, positions), ct);
    }

    /// <summary>
    /// Stores the order a drag left the list in: <paramref name="order"/> is the list after the drop, as each item's position
    /// before it, which is how the playlist page reads a ListView reorder too.
    /// </summary>
    public Task<bool> ReorderAsync(IReadOnlyList<int> order, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        return EditAsync("Reorder", ids => Permute(ids, order), ct);
    }

    /// <summary>Moves the items at <paramref name="positions"/> one place up (<paramref name="delta"/> −1) or down (+1), as a block.</summary>
    public Task<bool> MoveAsync(IReadOnlyList<int> positions, int delta, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(positions);
        return EditAsync(delta < 0 ? "Move up" : "Move down", ids => Shift(ids, positions, delta), ct);
    }

    /// <summary>Writes the list as it was before the last edit. False when there is nothing to undo.</summary>
    public Task<bool> UndoAsync(CancellationToken ct = default) => LockedAsync(async () =>
    {
        if (Playlist is not { } playlist || _undo.First is not { } node)
        {
            return false;
        }

        _undo.RemoveFirst();
        await _playlists.ReplaceTracksAsync(playlist.Id, node.Value.Before, ct).ConfigureAwait(false);
        _redo.Push(node.Value);
        await ReadAsync(playlist.Id, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    });

    /// <summary>Writes the list as the last undone edit left it. False when there is nothing to redo.</summary>
    public Task<bool> RedoAsync(CancellationToken ct = default) => LockedAsync(async () =>
    {
        if (Playlist is not { } playlist || !_redo.TryPop(out Step? step))
        {
            return false;
        }

        await _playlists.ReplaceTracksAsync(playlist.Id, step.After, ct).ConfigureAwait(false);
        Push(step);
        await ReadAsync(playlist.Id, ct).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    });

    public void Dispose() => _gate.Dispose();

    // ---- the list arithmetic, pure so the tests can state it -----------------------------------------------------------

    /// <summary><paramref name="ids"/> with <paramref name="added"/> inserted at <paramref name="position"/>, or appended.</summary>
    public static long[] Insert(IReadOnlyList<long> ids, IReadOnlyList<long> added, int? position)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(added);
        int at = Math.Clamp(position ?? ids.Count, 0, ids.Count);
        return [.. ids.Take(at), .. added, .. ids.Skip(at)];
    }

    /// <summary><paramref name="ids"/> without the items at <paramref name="positions"/>.</summary>
    public static long[] Remove(IReadOnlyList<long> ids, IReadOnlyList<int> positions)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(positions);
        var gone = positions.ToHashSet();
        return [.. ids.Where((_, i) => !gone.Contains(i))];
    }

    /// <summary><paramref name="ids"/> in <paramref name="order"/>, which must be a permutation of their positions.</summary>
    public static long[] Permute(IReadOnlyList<long> ids, IReadOnlyList<int> order)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(order);
        if (order.Count != ids.Count || !order.Order().SequenceEqual(Enumerable.Range(0, ids.Count)))
        {
            throw new ArgumentException("order must be a permutation of the list's positions", nameof(order));
        }

        return [.. order.Select(i => ids[i])];
    }

    /// <summary>
    /// <paramref name="ids"/> with the selected items moved one place by <paramref name="delta"/>, as a block: each selected
    /// item swaps with the unselected neighbour on that side, so a selection already against the end stays where it is.
    /// </summary>
    public static long[] Shift(IReadOnlyList<long> ids, IReadOnlyList<int> positions, int delta)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(positions);
        long[] result = [.. ids];
        int step = Math.Sign(delta);
        if (step == 0)
        {
            return result;
        }

        var selected = new bool[ids.Count];
        foreach (int p in positions.Where(p => p >= 0 && p < ids.Count))
        {
            selected[p] = true;
        }

        // Walk from the side the block moves towards, so a run of selected items moves together.
        IEnumerable<int> walk = step < 0 ? Enumerable.Range(0, ids.Count) : Enumerable.Range(0, ids.Count).Reverse();
        foreach (int i in walk)
        {
            int j = i + step;
            if (selected[i] && j >= 0 && j < ids.Count && !selected[j])
            {
                (result[i], result[j]) = (result[j], result[i]);
                (selected[i], selected[j]) = (false, true);
            }
        }

        return result;
    }

    /// <summary>Where each of <paramref name="positions"/> is after <see cref="Shift"/> by <paramref name="delta"/>, so a selection can follow its rows.</summary>
    public static int[] ShiftedPositions(int count, IReadOnlyList<int> positions, int delta)
    {
        ArgumentNullException.ThrowIfNull(positions);
        long[] moved = Shift([.. Enumerable.Range(0, count).Select(i => (long)i)], positions, delta);
        return [.. positions.Select(p => Array.IndexOf(moved, (long)p))];
    }

    private static string Describe(string verb, int count) => count == 1 ? verb + " 1 track" : $"{verb} {count} tracks";

    private Task<bool> EditAsync(string label, Func<long[], long[]> change, CancellationToken ct) => LockedAsync(async () =>
    {
        if (Playlist is not { } playlist)
        {
            return false;
        }

        long[] before = [.. Tracks.Select(t => t.Id)];
        long[] wanted = change(before);
        if (wanted.AsSpan().SequenceEqual(before))
        {
            return false; // a move against the end, a drop back where it started: nothing to write or to undo
        }

        await _playlists.ReplaceTracksAsync(playlist.Id, wanted, ct).ConfigureAwait(false);
        await ReadAsync(playlist.Id, ct).ConfigureAwait(false);
        Push(new Step(label, before, [.. Tracks.Select(t => t.Id)]));
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    });

    private void Push(Step step)
    {
        _undo.AddFirst(step);
        if (_undo.Count > Depth)
        {
            _undo.RemoveLast();
        }
    }

    private async Task<bool> ReadAsync(long playlistId, CancellationToken ct)
    {
        PlaylistDetailDto? detail = await _playlists.GetDetailAsync(playlistId, ct).ConfigureAwait(false);
        Playlist = detail?.Playlist;
        Tracks = detail?.Tracks ?? [];
        if (detail is null)
        {
            _undo.Clear();
            _redo.Clear();
        }

        return detail is not null;
    }

    private async Task<bool> LockedAsync(Func<Task<bool>> work)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record Step(string Label, long[] Before, long[] After);
}
