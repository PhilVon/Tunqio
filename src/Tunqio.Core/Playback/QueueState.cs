namespace Tunqio.Core.Playback;

/// <summary>
/// A queue as it stood, for the <c>queue_state</c> row that survives a restart (E1-S10, docs/library-and-data.md).
/// Both orders are kept: <see cref="Items"/> is what was playing and <see cref="AddedOrder"/> is what turning
/// shuffle off would restore, because a shuffled queue that came back in album order would be shuffle-on in name
/// only — its next track would be the album's next track.
/// </summary>
/// <param name="AddedOrder">The queue in the order its items were added.</param>
/// <param name="Items">The queue in play order; the same list as <paramref name="AddedOrder"/> when not shuffled.</param>
/// <param name="CurrentIndex">Index into <paramref name="Items"/> of the item that was current.</param>
/// <param name="Position">How far into the current track playback had reached.</param>
/// <param name="Shuffle">Whether shuffle was on.</param>
/// <param name="Repeat">The repeat mode.</param>
/// <param name="SavedAt">When the capture was taken, Unix milliseconds UTC.</param>
public sealed record QueueState(
    IReadOnlyList<QueueItem> AddedOrder,
    IReadOnlyList<QueueItem> Items,
    int? CurrentIndex,
    TimeSpan? Position,
    bool Shuffle,
    RepeatMode Repeat,
    long SavedAt)
{
    /// <summary>The state of <paramref name="queue"/> with playback at <paramref name="position"/>.</summary>
    public static QueueState Capture(PlayQueue queue, TimeSpan? position, long savedAt)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return new QueueState(queue.AddedOrder, queue.Items, queue.CurrentIndex, position, queue.Shuffle, queue.Repeat, savedAt);
    }

    /// <summary>The queue this state describes.</summary>
    public PlayQueue ToQueue() => PlayQueue.Restore(AddedOrder, Items, CurrentIndex, Shuffle, Repeat);
}

/// <summary>
/// The one saved queue (E1-S10): a single row that the session writes on stop and reads on start. Lives with the
/// library because that is where the database is, not because a queue is library data.
/// </summary>
public interface IQueueStateRepository
{
    /// <summary>
    /// The saved queue, or null when nothing was ever saved. Items whose track has since left the library are
    /// dropped, so a restored queue never points at a file the library no longer knows about; if that removes
    /// the item that was current, the restored state has nothing current.
    /// </summary>
    Task<QueueState?> LoadAsync(CancellationToken ct = default);

    /// <summary>Replaces the saved queue.</summary>
    Task SaveAsync(QueueState state, CancellationToken ct = default);

    /// <summary>Forgets the saved queue.</summary>
    Task ClearAsync(CancellationToken ct = default);
}
