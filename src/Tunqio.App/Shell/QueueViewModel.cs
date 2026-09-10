using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.App.Playback;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Shell;

/// <summary>
/// One row of the queue panel: a queue item with the track it names already resolved, so the list draws without
/// touching a repository. Immutable, and identified by <see cref="InstanceId"/> rather than by track id, because
/// the same track can sit in the queue twice and the two copies are moved and removed independently (E1-S9).
/// </summary>
public sealed class QueueRow
{
    internal QueueRow(QueueItem item, TrackDto? track)
    {
        InstanceId = item.InstanceId;
        TrackId = item.TrackId;
        Title = track?.Title ?? "Unknown track";
        ArtistNames = track?.ArtistNames ?? string.Empty;
        DurationMs = track?.DurationMs ?? 0;
    }

    /// <summary>Which queue item this row is — what a remove or a move is addressed to.</summary>
    public Guid InstanceId { get; }

    /// <summary>The track behind the item; negative for a file opened from disk that the library has no row for (E2-S4).</summary>
    public long TrackId { get; }

    public string Title { get; }

    public string ArtistNames { get; }

    /// <summary>The track length, which is also what the panel's remaining total is summed from.</summary>
    public int DurationMs { get; }

    public string DurationText => Format.Duration(DurationMs);

    /// <summary>What Narrator reads for the row, which is the track and not the two columns it is drawn in.</summary>
    public string AutomationName => string.IsNullOrEmpty(ArtistNames)
        ? Title
        : Title + " by " + ArtistNames;

    /// <summary>What Narrator reads for the row's remove button, which has to say *what* it removes.</summary>
    public string RemoveLabel => "Remove " + Title + " from the queue";
}

/// <summary>
/// The queue panel's state and commands (E2-S5, docs/ui-screens-and-flows.md, "Queue"): the current track pinned,
/// what follows it in play order, and the time left. Like the transport it owns no playback state — it reflects
/// <see cref="PlaybackSnapshot"/> and asks <see cref="PlaybackSession"/> for every change (ADR-008).
/// </summary>
/// <remarks>
/// <para>
/// The queue arrives on the snapshot rather than being read off the session, which is what makes the panel
/// coherent: a list fetched separately from the current index would sometimes be a queue from one instant drawn
/// against a position from another, and the pinned row would be the wrong track for a frame. Since the queue is
/// immutable and replaced on every mutation, "has it changed" is a reference comparison, and the rows are rebuilt
/// only when it has — not ten times a second.
/// </para>
/// <para>
/// The time left is the exception: it follows the position, so it is recomputed on every snapshot from the sum the
/// last rebuild left behind. That sum is over the upcoming items only; what is left of the track playing comes from
/// the snapshot, because the engine knows the real length of the file and a tag can be wrong about it.
/// </para>
/// <para>
/// Rebuilding is asynchronous — the rows need <see cref="ITrackRepository"/> — so a rebuild that finishes after a
/// newer queue has arrived discards its own result rather than drawing a queue that has already been replaced.
/// </para>
/// </remarks>
public sealed partial class QueueViewModel : ObservableObject, IDisposable
{
    private readonly SynchronizationContext? _ui;
    private readonly IPlaybackSessionSource _source;
    private readonly ITrackRepository _tracks;
    private readonly Dictionary<long, TrackDto> _resolved = [];
    private IDisposable? _subscription;
    private PlaybackSession? _session;
    private PlayQueue _queue = PlayQueue.Empty;
    private Task _rebuild = Task.CompletedTask;
    private Task _reorder = Task.CompletedTask;
    private long _upcomingMs;
    private bool _syncing;
    private bool _disposed;

    [ObservableProperty]
    private QueueRow? _nowPlaying;

    [ObservableProperty]
    private string _remainingText = string.Empty;

    /// <param name="source">Where the session comes from; it may not exist yet, and may never.</param>
    /// <param name="tracks">Resolves queue items to rows; the transient-aware repository, so a dropped file resolves too.</param>
    /// <param name="ui">The XAML thread's context. Null runs updates inline, which is what the tests want.</param>
    public QueueViewModel(IPlaybackSessionSource source, ITrackRepository tracks, SynchronizationContext? ui = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tracks);
        _source = source;
        _tracks = tracks;
        _ui = ui;
        Upcoming.CollectionChanged += OnUpcomingChanged;

        if (source.Session is { } ready)
        {
            Attach(ready);
        }
        else
        {
            source.SessionReady += OnSessionReady;
        }
    }

    /// <summary>What follows the current track, in play order — so a shuffled queue reads in the order it will play.</summary>
    public ObservableCollection<QueueRow> Upcoming { get; } = [];

    /// <summary>True once there is a session to command; the panel is visible but inert before that.</summary>
    public bool IsReady => _session is not null;

    /// <summary>
    /// The rebuild in flight. Rows need the repository, so a queue change reaches the panel one continuation after
    /// it reaches the snapshot; anything that has to see the rows rather than the queue waits on this.
    /// </summary>
    // No JoinableTaskFactory in this app and nothing blocks on these: they are handed out to be awaited, not joined.
#pragma warning disable VSTHRD003
    public Task RowsSettledAsync() => _rebuild;

    /// <summary>
    /// The reorder in flight — the session catching up with a row the list has already moved. Same reason as
    /// <see cref="RowsSettledAsync"/>: the list moves first and the session is told afterwards.
    /// </summary>
    public Task ReorderSettledAsync() => _reorder;
#pragma warning restore VSTHRD003

    /// <summary>Whether there is a current track to pin at the top.</summary>
    public bool HasNowPlaying => NowPlaying is not null;

    /// <summary>True when there is nothing queued at all: the panel's empty state.</summary>
    public bool IsEmpty => NowPlaying is null && Upcoming.Count == 0;

    /// <summary>Whether there is anything to clear, reorder or run out of.</summary>
    public bool HasUpcoming => Upcoming.Count > 0;

    /// <summary>"3 tracks" — the header's count of what is still to come.</summary>
    public string UpcomingCountText => Format.Tracks(Upcoming.Count);

    // ---- commands --------------------------------------------------------------------------------------------------

    /// <summary>Takes <paramref name="row"/> out of the queue; the pinned row is removable too, and starts the next.</summary>
    public Task RemoveAsync(QueueRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _session?.RemoveFromQueueAsync(row.InstanceId) ?? Task.CompletedTask;
    }

    /// <summary>Empties everything after the current track, which keeps playing.</summary>
    public Task ClearUpcomingAsync() => _session?.ClearUpcomingAsync() ?? Task.CompletedTask;

    // ---- the list moving a row ---------------------------------------------------------------------------------------

    /// <summary>
    /// The collection changed. When it was the panel bringing it in line with the session, there is nothing to do;
    /// when it was the list itself — a row dragged, or moved with the keyboard — the session has to be told, because
    /// the item after the current one is already open in the mixer and only the session can re-open it (AC-76).
    /// </summary>
    /// <remarks>
    /// Watching the collection rather than the list's <c>DragItemsCompleted</c> is deliberate: it is the same event
    /// however the row moved, so a keyboard reorder is not a second path that has to be remembered and wired
    /// separately — and it is a plain collection, so the whole of it is testable without a window. A reorder arrives
    /// as a remove and then an insert; the half-way state is not a permutation of the queue's upcoming items, which
    /// is what distinguishes it from a finished move and keeps a transient state from being reported as a removal.
    /// </remarks>
    private void OnUpcomingChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasUpcoming));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(UpcomingCountText));
        if (_syncing || _session is null || !IsReorderOfQueue())
        {
            return;
        }

        _reorder = ReorderAsync([.. Upcoming.Select(row => row.InstanceId)]);
    }

    /// <summary>True when the list holds exactly the queue's upcoming items, in some order.</summary>
    private bool IsReorderOfQueue()
    {
        int first = FirstUpcoming();
        if (Upcoming.Count != _queue.Count - first)
        {
            return false;
        }

        HashSet<Guid> queued = [.. _queue.Items.Skip(first).Select(item => item.InstanceId)];
        return Upcoming.All(row => queued.Contains(row.InstanceId));
    }

    /// <summary>
    /// Tells the session about the order the list is now in, one move at a time. The moves are worked out against a
    /// copy of the queue's order rather than by re-reading the session between them: the snapshot that carries each
    /// move back arrives on the UI thread's queue and need not have been delivered yet, and a loop that waited for
    /// it would either stall or re-issue the move it had just made.
    /// </summary>
    private async Task ReorderAsync(Guid[] desired)
    {
        try
        {
            int first = FirstUpcoming();
            List<Guid> order = [.. _queue.Items.Skip(first).Select(item => item.InstanceId)];
            for (int i = 0; i < desired.Length && _session is not null; i++)
            {
                if (order[i] == desired[i])
                {
                    continue;
                }

                order.RemoveAt(order.IndexOf(desired[i]));
                order.Insert(i, desired[i]);
                await _session.MoveInQueueAsync(desired[i], first + i).ConfigureAwait(true);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Serilog.Log.Error(e, "A queue reorder could not be applied");
        }
    }

    /// <summary>Where the upcoming items start in the play order: just past the current item, or the top.</summary>
    private int FirstUpcoming() => _queue.CurrentIndex is int index ? index + 1 : 0;

    // ---- snapshots -------------------------------------------------------------------------------------------------

    private void OnSessionReady(object? sender, PlaybackSession session)
    {
        _source.SessionReady -= OnSessionReady;
        Post(() => Attach(session));
    }

    private void Attach(PlaybackSession session)
    {
        if (_disposed)
        {
            return;
        }

        _session = session;
        _subscription = session.Snapshots.Subscribe(s => Post(() => Apply(s)));
        OnPropertyChanged(nameof(IsReady));
    }

    private void Apply(PlaybackSnapshot next)
    {
        if (!ReferenceEquals(next.Queue, _queue))
        {
            _queue = next.Queue;
            _rebuild = RebuildAsync(next.Queue);
        }

        UpdateRemaining(next);
    }

    /// <summary>
    /// Resolves the queue's items to rows and syncs the collection. The result is dropped if the queue has moved on
    /// while the repository was answering — the newer rebuild is already on its way and this one would draw over it.
    /// </summary>
    private async Task RebuildAsync(PlayQueue queue)
    {
        try
        {
            long[] unknown = [.. queue.Items.Select(item => item.TrackId).Distinct().Where(id => !_resolved.ContainsKey(id))];
            if (unknown.Length > 0)
            {
                foreach (TrackDto track in await _tracks.GetByIdsAsync(unknown).ConfigureAwait(true))
                {
                    _resolved[track.Id] = track;
                }
            }

            if (!ReferenceEquals(queue, _queue))
            {
                return;
            }

            int first = queue.CurrentIndex is int index ? index + 1 : 0;
            NowPlaying = queue.Current is { } current ? Row(current) : null;
            Prune(queue);
            QueueRow[] upcoming = [.. queue.Items.Skip(first).Select(Row)];
            _upcomingMs = upcoming.Sum(row => (long)row.DurationMs);
            Sync(upcoming);
            OnPropertyChanged(nameof(IsEmpty));
            if (_session is not null)
            {
                UpdateRemaining(_session.Current);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Serilog.Log.Error(e, "The queue panel could not resolve its rows");
        }

        QueueRow Row(QueueItem item) => new(item, _resolved.GetValueOrDefault(item.TrackId));
    }

    /// <summary>
    /// Forgets rows the queue no longer holds. The cache is there so a reorder does not re-query, not so the panel
    /// accumulates every track played since launch.
    /// </summary>
    private void Prune(PlayQueue queue)
    {
        if (_resolved.Count <= queue.Count)
        {
            return;
        }

        HashSet<long> live = [.. queue.Items.Select(item => item.TrackId)];
        foreach (long id in _resolved.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            _resolved.Remove(id);
        }
    }

    /// <summary>
    /// Brings <see cref="Upcoming"/> to <paramref name="next"/> by moving, inserting and removing rather than by
    /// clearing and refilling. A drag has already moved the item in this very collection by the time the session
    /// answers, so the usual case is a rebuild that has nothing to do — and a Clear would throw away the selection
    /// and the scroll position to arrive at the list that was already on the screen.
    /// </summary>
    private void Sync(IReadOnlyList<QueueRow> next)
    {
        _syncing = true;
        try
        {
            SyncCore(next);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncCore(IReadOnlyList<QueueRow> next)
    {
        HashSet<Guid> wanted = [.. next.Select(row => row.InstanceId)];
        for (int i = Upcoming.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Upcoming[i].InstanceId))
            {
                Upcoming.RemoveAt(i);
            }
        }

        for (int i = 0; i < next.Count; i++)
        {
            if (i < Upcoming.Count && Upcoming[i].InstanceId == next[i].InstanceId)
            {
                continue;
            }

            int at = IndexOf(next[i].InstanceId, i);
            if (at < 0)
            {
                Upcoming.Insert(Math.Min(i, Upcoming.Count), next[i]);
            }
            else if (at != i)
            {
                Upcoming.Move(at, i);
            }
        }

        while (Upcoming.Count > next.Count)
        {
            Upcoming.RemoveAt(Upcoming.Count - 1);
        }

        int IndexOf(Guid instanceId, int from)
        {
            for (int i = from; i < Upcoming.Count; i++)
            {
                if (Upcoming[i].InstanceId == instanceId)
                {
                    return i;
                }
            }

            return -1;
        }
    }

    /// <summary>
    /// What is left: the rest of the track playing plus every upcoming track. The current track's length comes from
    /// the snapshot — the engine has opened the file and the tag that says otherwise is the one that is wrong — and
    /// falls back to the row's length while nothing is loaded, which is the state a restored queue starts in.
    /// </summary>
    private void UpdateRemaining(PlaybackSnapshot snapshot)
    {
        double current = snapshot.Duration > TimeSpan.Zero
            ? (snapshot.Duration - snapshot.Position).TotalMilliseconds
            : NowPlaying?.DurationMs ?? 0;
        long total = _upcomingMs + (long)Math.Max(0, current);
        RemainingText = total <= 0
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"{Format.LongDuration(total)} left");
    }

    partial void OnNowPlayingChanged(QueueRow? value)
    {
        OnPropertyChanged(nameof(HasNowPlaying));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
            return;
        }

        // No JoinableTaskFactory in this app; the context is the XAML thread's DispatcherQueue one and Post never blocks the caller.
#pragma warning disable VSTHRD001
        _ui.Post(_ => action(), null);
#pragma warning restore VSTHRD001
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.SessionReady -= OnSessionReady;
        _subscription?.Dispose();
        _subscription = null;
        _session = null;
    }
}
