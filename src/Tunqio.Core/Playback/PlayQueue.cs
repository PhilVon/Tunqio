namespace Tunqio.Core.Playback;

/// <summary>What happens when the queue reaches its end (or its start, going back).</summary>
public enum RepeatMode
{
    /// <summary>The end of the queue stops playback.</summary>
    Off,

    /// <summary>The end of the queue wraps to the first item.</summary>
    All,

    /// <summary>A track that ends on its own replays; a manual Next still advances.</summary>
    One,
}

/// <summary>
/// One entry in the queue. <see cref="InstanceId"/> is what lets the same track sit in the queue twice and be
/// removed, moved and played as two independent items — an index would shift under every mutation and a track id
/// would name both copies at once.
/// </summary>
public sealed record QueueItem(long TrackId, Guid InstanceId)
{
    /// <summary>A fresh instance of <paramref name="trackId"/>, distinct from every other instance of it.</summary>
    public static QueueItem For(long trackId) => new(trackId, Guid.NewGuid());
}

/// <summary>
/// The play queue (E1-S9, per docs/library-and-data.md, "Play queue model"): an immutable value that
/// <c>PlaybackSession</c> (E1-S10) owns and replaces on every mutation, so the UI can diff two versions instead of
/// watching a mutable list. It holds track ids and ordering only — no files, no database, no engine — which is why
/// every rule here is testable without either.
/// </summary>
/// <remarks>
/// <para>
/// Two orders are kept. <see cref="Items"/> is the play order, which is the shuffled order while
/// <see cref="Shuffle"/> is on; behind it the queue keeps the order the items were added in, so turning shuffle off
/// restores that order with the current track still current. Shuffling only ever touches the items *after* the
/// current one: what has already been played keeps its order, so Previous walks back through what was actually heard.
/// </para>
/// <para>
/// <see cref="Move"/> made while shuffle is on reorders the play order alone — the added order is the thing that
/// unshuffling restores, and letting a shuffled drag rewrite it would mean shuffle off never gives back the album.
/// </para>
/// </remarks>
#pragma warning disable CA1711 // "PlayQueue" is the name the design gives it; it is the user's queue of tracks, not a System.Collections queue.
public sealed class PlayQueue
{
    /// <summary>An empty queue with nothing current, shuffle off and repeat off.</summary>
    public static readonly PlayQueue Empty = new([], [], null, false, RepeatMode.Off);

    /// <summary>
    /// How far into a track Previous stops meaning "the previous track" and starts meaning "this track again"
    /// (docs/library-and-data.md): before 3 s the press is a correction, after it it is a rewind.
    /// </summary>
    public static readonly TimeSpan RestartWindow = TimeSpan.FromSeconds(3);

    private readonly QueueItem[] _items;
    private readonly QueueItem[] _addedOrder;

    private PlayQueue(QueueItem[] items, QueueItem[] addedOrder, int? currentIndex, bool shuffle, RepeatMode repeat)
    {
        _items = items;
        _addedOrder = addedOrder;
        CurrentIndex = currentIndex;
        Shuffle = shuffle;
        Repeat = repeat;
    }

    /// <summary>The queue in play order — the shuffled order while <see cref="Shuffle"/> is on.</summary>
    public IReadOnlyList<QueueItem> Items => _items;

    /// <summary>
    /// The queue in the order its items were added — what turning <see cref="Shuffle"/> off restores, and what a
    /// saved queue has to carry alongside <see cref="Items"/> for a restored shuffle to still be undoable.
    /// </summary>
    public IReadOnlyList<QueueItem> AddedOrder => _addedOrder;

    /// <summary>The index into <see cref="Items"/> of the current item, or null when nothing is current.</summary>
    public int? CurrentIndex { get; }

    /// <summary>The current item, or null when nothing is current.</summary>
    public QueueItem? Current => CurrentIndex is int index ? _items[index] : null;

    /// <summary>Whether the play order is shuffled.</summary>
    public bool Shuffle { get; }

    /// <summary>What the end of the queue does.</summary>
    public RepeatMode Repeat { get; }

    /// <summary>How many items are queued.</summary>
    public int Count => _items.Length;

    /// <summary>
    /// Replaces the queue with <paramref name="trackIds"/> and makes <paramref name="startIndex"/> current (clamped
    /// into the queue): playing an album from its fourth track keeps tracks one to three behind the current item so
    /// Previous reaches them. Shuffle and repeat survive the replacement — a user who turned shuffle on and then
    /// picked an album expects that album shuffled — so with shuffle on the items after the start are shuffled with
    /// <paramref name="rng"/> (<see cref="Random.Shared"/> when none is given). An empty <paramref name="trackIds"/>
    /// clears the queue.
    /// </summary>
    public PlayQueue PlayNow(IEnumerable<long> trackIds, int startIndex = 0, Random? rng = null)
    {
        QueueItem[] added = Materialise(trackIds);
        if (added.Length == 0)
        {
            return new PlayQueue([], [], null, Shuffle, Repeat);
        }

        int start = Math.Clamp(startIndex, 0, added.Length - 1);
        QueueItem current = added[start];
        QueueItem[] items = Shuffle ? ShuffleTail(added, start, rng ?? Random.Shared) : added;
        return new PlayQueue(items, added, Array.IndexOf(items, current), Shuffle, Repeat);
    }

    /// <summary>
    /// Inserts <paramref name="trackIds"/> immediately after the current item, in the given order, keeping the
    /// current item current. With nothing current they go to the front of the queue.
    /// </summary>
    public PlayQueue PlayNext(IEnumerable<long> trackIds)
    {
        QueueItem[] added = Materialise(trackIds);
        if (added.Length == 0)
        {
            return this;
        }

        QueueItem? current = Current;
        return With(
            Insert(_items, InsertPointAfter(_items, current), added),
            Insert(_addedOrder, InsertPointAfter(_addedOrder, current), added),
            current);
    }

    /// <summary>Appends <paramref name="trackIds"/> to the end of the queue.</summary>
    public PlayQueue Enqueue(IEnumerable<long> trackIds)
    {
        QueueItem[] added = Materialise(trackIds);
        if (added.Length == 0)
        {
            return this;
        }

        return With(
            Insert(_items, _items.Length, added),
            Insert(_addedOrder, _addedOrder.Length, added),
            Current);
    }

    /// <summary>
    /// Removes the item with <paramref name="instanceId"/> (an id that is not in the queue changes nothing).
    /// Removing the current item makes the item that follows it current, and leaves nothing current when the removed
    /// item was the last one.
    /// </summary>
    public PlayQueue Remove(Guid instanceId)
    {
        int index = IndexOf(instanceId);
        if (index < 0)
        {
            return this;
        }

        QueueItem removed = _items[index];
        QueueItem? current = removed == Current ? Successor(index) : Current;
        return With(Without(_items, removed), Without(_addedOrder, removed), current);
    }

    /// <summary>
    /// Drops everything after the current item, in both orders, leaving the current item current. With nothing
    /// current the whole queue goes: everything in it is upcoming.
    /// </summary>
    /// <remarks>
    /// The survivors are taken from the play order and then filtered out of the added order, rather than the added
    /// order being truncated at the same length. Under shuffle the two orders disagree about which items are
    /// "after" the current one, and truncating would keep a different set than the user just saw disappear.
    /// </remarks>
    public PlayQueue ClearUpcoming()
    {
        if (CurrentIndex is not int index)
        {
            return new PlayQueue([], [], null, Shuffle, Repeat);
        }

        QueueItem[] kept = _items[..(index + 1)];
        HashSet<QueueItem> keptSet = [.. kept];
        return new PlayQueue(kept, [.. _addedOrder.Where(keptSet.Contains)], index, Shuffle, Repeat);
    }

    /// <summary>
    /// Moves the item with <paramref name="instanceId"/> to <paramref name="toIndex"/> in play order (clamped into
    /// the queue); an id that is not in the queue changes nothing. The current item stays current wherever it lands.
    /// While shuffle is on this reorders the play order only — see the note on the class.
    /// </summary>
    public PlayQueue Move(Guid instanceId, int toIndex)
    {
        int from = IndexOf(instanceId);
        if (from < 0)
        {
            return this;
        }

        int to = Math.Clamp(toIndex, 0, _items.Length - 1);
        if (to == from)
        {
            return this;
        }

        QueueItem moved = _items[from];
        QueueItem[] items = Insert(Without(_items, moved), to, [moved]);
        return With(items, Shuffle ? _addedOrder : items, Current);
    }

    /// <summary>
    /// Turns shuffle on — shuffling the items after the current one with <paramref name="rng"/> and leaving what has
    /// already been played in place — or off, restoring the order the items were added in with the current item
    /// still current.
    /// </summary>
    public PlayQueue ToggleShuffle(Random rng)
    {
        ArgumentNullException.ThrowIfNull(rng);

        QueueItem? current = Current;
        QueueItem[] items = Shuffle ? _addedOrder : ShuffleTail(_items, CurrentIndex, rng);
        return new PlayQueue(items, _addedOrder, IndexOfOrNull(items, current), !Shuffle, Repeat);
    }

    /// <summary>Returns the queue with <paramref name="repeat"/> in effect.</summary>
    public PlayQueue WithRepeat(RepeatMode repeat) => new(_items, _addedOrder, CurrentIndex, Shuffle, repeat);

    /// <summary>
    /// Moves to the next item. <paramref name="manual"/> distinguishes a user pressing Next from a track ending on
    /// its own, which is the whole of <see cref="RepeatMode.One"/>: a track that ends replays, a user who presses
    /// Next moves on. Past the last item the queue wraps under any repeat mode and stops under
    /// <see cref="RepeatMode.Off"/> (nothing current). With nothing current already, nothing happens.
    /// </summary>
    public PlayQueue Advance(bool manual)
    {
        if (CurrentIndex is not int index)
        {
            return this;
        }

        if (Repeat == RepeatMode.One && !manual)
        {
            return this;
        }

        int next = index + 1;
        if (next < _items.Length)
        {
            return WithCurrentIndex(next);
        }

        return Repeat == RepeatMode.Off ? WithCurrentIndex(null) : WithCurrentIndex(0);
    }

    /// <summary>
    /// Applies Previous at <paramref name="position"/> into the current track: before <see cref="RestartWindow"/> it
    /// goes to the previous item, at or after it the current item stays current and the session restarts it. Before
    /// the first item it wraps to the last under any repeat mode, and restarts under <see cref="RepeatMode.Off"/>.
    /// </summary>
    public PlayQueue Back(TimeSpan position)
    {
        if (CurrentIndex is not int index)
        {
            return this;
        }

        if (position >= RestartWindow)
        {
            return this;
        }

        if (index > 0)
        {
            return WithCurrentIndex(index - 1);
        }

        return Repeat == RepeatMode.Off ? this : WithCurrentIndex(_items.Length - 1);
    }

    /// <summary>
    /// The item that would become current if the current track ended now — what <c>PlaybackSession</c> hands the
    /// engine to pre-open for a gapless join (E1-S3). Null when the queue would stop instead.
    /// </summary>
    public QueueItem? PeekNext()
    {
        if (CurrentIndex is not int index)
        {
            return null;
        }

        if (Repeat == RepeatMode.One)
        {
            return _items[index];
        }

        int next = index + 1;
        if (next < _items.Length)
        {
            return _items[next];
        }

        return Repeat == RepeatMode.Off ? null : _items[0];
    }

    /// <summary>
    /// Rebuilds a queue from a saved one (E1-S10). <paramref name="playOrder"/> is the order it was playing in —
    /// null, or the same list, when it was not shuffled — and must hold the same items as
    /// <paramref name="addedOrder"/>; the caller that reads a saved queue is the one that checks that, because a
    /// corrupt row is a storage problem and not a queue rule. A current index outside the queue restores as
    /// nothing current.
    /// </summary>
    public static PlayQueue Restore(
        IEnumerable<QueueItem> addedOrder,
        IEnumerable<QueueItem>? playOrder,
        int? currentIndex,
        bool shuffle,
        RepeatMode repeat)
    {
        ArgumentNullException.ThrowIfNull(addedOrder);

        QueueItem[] added = addedOrder.ToArray();
        QueueItem[] items = playOrder is null ? added : playOrder.ToArray();
        int? index = currentIndex is int at && at >= 0 && at < items.Length ? at : null;
        return new PlayQueue(items, added, index, shuffle, repeat);
    }

    private static QueueItem[] Materialise(IEnumerable<long> trackIds)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        return trackIds.Select(QueueItem.For).ToArray();
    }

    /// <summary>
    /// A Fisher-Yates shuffle of everything after <paramref name="currentIndex"/>, leaving the current item and the
    /// items before it where they are.
    /// </summary>
    private static QueueItem[] ShuffleTail(QueueItem[] items, int? currentIndex, Random rng)
    {
        QueueItem[] shuffled = (QueueItem[])items.Clone();
        int first = currentIndex is int index ? index + 1 : 0;
        for (int n = shuffled.Length - 1; n > first; n--)
        {
            int j = rng.Next(first, n + 1);
            (shuffled[n], shuffled[j]) = (shuffled[j], shuffled[n]);
        }

        return shuffled;
    }

    private static QueueItem[] Insert(QueueItem[] items, int at, QueueItem[] inserted)
    {
        QueueItem[] result = new QueueItem[items.Length + inserted.Length];
        Array.Copy(items, 0, result, 0, at);
        Array.Copy(inserted, 0, result, at, inserted.Length);
        Array.Copy(items, at, result, at + inserted.Length, items.Length - at);
        return result;
    }

    private static QueueItem[] Without(QueueItem[] items, QueueItem removed) =>
        items.Where(item => item != removed).ToArray();

    private static int InsertPointAfter(QueueItem[] items, QueueItem? current) =>
        current is null ? 0 : Array.IndexOf(items, current) + 1;

    /// <summary>
    /// Where <paramref name="current"/> ended up in <paramref name="items"/>. Every caller passes an item it has
    /// just placed in that array (or null), so there is no not-found case to defend against here.
    /// </summary>
    private static int? IndexOfOrNull(QueueItem[] items, QueueItem? current) =>
        current is null ? null : Array.IndexOf(items, current);

    private int IndexOf(Guid instanceId) => Array.FindIndex(_items, item => item.InstanceId == instanceId);

    private QueueItem? Successor(int index) => index + 1 < _items.Length ? _items[index + 1] : null;

    private PlayQueue With(QueueItem[] items, QueueItem[] addedOrder, QueueItem? current) =>
        new(items, addedOrder, IndexOfOrNull(items, current), Shuffle, Repeat);

    private PlayQueue WithCurrentIndex(int? index) => new(_items, _addedOrder, index, Shuffle, Repeat);
}
#pragma warning restore CA1711
