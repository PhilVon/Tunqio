using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.Core.Audio;
using Tunqio.Core.Library;

namespace Tunqio.Core.Playback;

/// <summary>What the transport is doing.</summary>
public enum PlaybackState
{
    /// <summary>Nothing is loaded, or the queue ran out.</summary>
    Stopped,

    Playing,

    /// <summary>Held at a position, either by the user or because the device went (E1-S7).</summary>
    Paused,
}

/// <summary>
/// Everything the shell needs to draw the transport, Now Playing and the queue, published at 10 Hz (E1-S10). A
/// value, so a view model can diff two of them rather than watch a mutable store.
/// </summary>
/// <remarks>
/// The <see cref="PlayQueue"/> is carried whole rather than as the handful of numbers the transport reads off it,
/// because the queue panel (E2-S5) needs the items and a panel that fetched them from the session separately would
/// be drawing a queue from one instant against a current index from another. It costs nothing to carry: the queue
/// is immutable and replaced on every mutation, so this is a reference, and reference equality is exactly the test
/// for "has the queue changed since the last snapshot".
/// </remarks>
public sealed record PlaybackSnapshot(
    PlaybackState State,
    QueueItem? Current,
    TrackDto? Track,
    TimeSpan Position,
    TimeSpan Duration,
    PlayQueue Queue,
    float Volume,
    OutputStatus Output)
{
    /// <summary>Nothing playing, empty queue, nothing to say about the output.</summary>
    public static PlaybackSnapshot Idle { get; } = new(
        PlaybackState.Stopped, null, null, TimeSpan.Zero, TimeSpan.Zero, PlayQueue.Empty, 1f, OutputStatus.Ok);

    /// <summary>Where the current item sits in the play order, or null when nothing is current.</summary>
    public int? QueueIndex => Queue.CurrentIndex;

    /// <summary>How many items are queued.</summary>
    public int QueueCount => Queue.Count;

    /// <summary>Whether the play order is shuffled.</summary>
    public bool Shuffle => Queue.Shuffle;

    /// <summary>What the end of the queue does.</summary>
    public RepeatMode Repeat => Queue.Repeat;
}

/// <summary>
/// The single owner of playback state (ADR-008) and the only caller of <see cref="IAudioEngine"/>: it holds the
/// <see cref="PlayQueue"/>, opens and closes the track handles, applies the per-boundary join
/// (<see cref="CrossfadePolicy"/>) and per-track gain (<see cref="ReplayGainPolicy"/>), and publishes
/// <see cref="Snapshots"/> for the shell to render. The shell holds no playback state of its own.
/// </summary>
/// <remarks>
/// <para>
/// Engine events arrive on the interop pump thread and only ever <em>record</em> here — a join to commit, a track
/// that ended with nothing behind it. <see cref="PollAsync"/> is what acts on them, under the same lock as every
/// command, so opening a file never happens on the pump thread. It is the same shape the native side uses for
/// device notifications, and for the same reason.
/// </para>
/// <para>
/// A gapless join is announced up to one output buffer before it is audible, so the now-playing item does not
/// change when <see cref="EngineEventType.TrackStarted"/> arrives: it changes when
/// <see cref="PlaybackClock.HasPlayed"/> says the join position has left the output buffer.
/// </para>
/// </remarks>
public sealed class PlaybackSession : IPlaybackCommands, IPreviewPlayer, IAsyncDisposable
{
    /// <summary>How often <see cref="Snapshots"/> is published while anything is loaded.</summary>
    public static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(100);

    private readonly IAudioEngine _engine;
    private readonly ITrackRepository _tracks;
    private readonly IPlayHistoryRepository _history;
    private readonly IQueueStateRepository _queueStore;
    private readonly ISettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly Random _rng;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly BehaviorSubject<PlaybackSnapshot> _snapshots = new(PlaybackSnapshot.Idle);
    private readonly Subject<string> _errors = new();
    private readonly List<string> _pendingErrors = [];
    private readonly IDisposable _events;
    private readonly ITimer? _timer;

    private PlayQueue _queue = PlayQueue.Empty;
    private LoadedTrack? _current;
    private Listen? _listen;
    private LoadedTrack? _next;
    private PendingJoin? _join;
    private bool _ended;
    private PlaybackState _state = PlaybackState.Stopped;
    private TimeSpan _position;
    private float _volume = 1f;
    private OutputStatus _output = OutputStatus.Ok;
    private bool _resumeWhenOutputReturns;
    private bool _disposed;

    /// <param name="engine">The engine this session drives; it is the session's alone.</param>
    /// <param name="tracks">Resolves the queue's track ids to the files and tags the engine needs.</param>
    /// <param name="history">Takes a play event for every track that stops being current (E3-S11).</param>
    /// <param name="queueStore">Holds the queue across a restart; written on stop and on dispose.</param>
    /// <param name="settings">Read for gapless, crossfade and ReplayGain at every boundary, so a change applies to the next one.</param>
    /// <param name="clock">Drives the 10 Hz snapshot timer; pass a fake and call <see cref="PollAsync"/> by hand in tests.</param>
    /// <param name="rng">The shuffle's randomness; fixed in tests.</param>
    /// <param name="logger">Optional.</param>
    /// <param name="autoPoll">False leaves the timer unstarted, so a test polls when it chooses.</param>
    public PlaybackSession(
        IAudioEngine engine,
        ITrackRepository tracks,
        IPlayHistoryRepository history,
        IQueueStateRepository queueStore,
        ISettingsStore settings,
        TimeProvider? clock = null,
        Random? rng = null,
        ILogger<PlaybackSession>? logger = null,
        bool autoPoll = true)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(queueStore);
        ArgumentNullException.ThrowIfNull(settings);

        _engine = engine;
        _tracks = tracks;
        _history = history;
        _queueStore = queueStore;
        _settings = settings;
        _time = clock ?? TimeProvider.System;
        _rng = rng ?? Random.Shared;
        _log = logger ?? (ILogger)NullLogger<PlaybackSession>.Instance;
        _events = engine.Events.Subscribe(OnEngineEvent);
        if (autoPoll)
        {
            _timer = _time.CreateTimer(
                _ => _ = PollAsync(), null, SnapshotInterval, SnapshotInterval);
        }
    }

    /// <summary>The transport as it stands, republished at 10 Hz and after every command.</summary>
    public IObservable<PlaybackSnapshot> Snapshots => _snapshots;

    /// <summary>The most recent snapshot, without subscribing.</summary>
    public PlaybackSnapshot Current => _snapshots.Value;

    /// <summary>
    /// Engine failures worth telling the user about once, as they happen (E2-S7): an exclusive-mode request a
    /// driver refused, a stream that could not be opened. Transient, which is why they are a stream and not part of
    /// the snapshot — a second failure must not overwrite the first before anyone has read it, and neither is a
    /// state the user is still in. Raised from <see cref="PollAsync"/>, so nothing reaches a subscriber on the
    /// interop pump thread.
    /// </summary>
    public IObservable<string> Errors => _errors;

    /// <summary>The queue as it stands.</summary>
    public PlayQueue Queue => _queue;

    /// <summary>
    /// What the engine reports about the open output — buffer, format, underruns (E2-S8's overlay). Read through
    /// the session because the session is the only thing that holds an <see cref="IAudioEngine"/> (ADR-008), not
    /// because it has anything to add.
    /// </summary>
    public EngineStats EngineStats => _engine.Stats;

    /// <summary>The engine's clock right now, for readouts that need the live output buffer depth (T-171).</summary>
    public PlaybackClock EngineClock => _engine.Clock;

    public Task PlayNowAsync(IReadOnlyList<long> trackIds, int startIndex = 0, bool shuffle = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        return LockedAsync(async () =>
        {
            _queue = _queue.PlayNow(trackIds, startIndex, _rng);
            if (shuffle && !_queue.Shuffle)
            {
                _queue = _queue.ToggleShuffle(_rng);
            }

            await StartCurrentAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
        });
    }

    public Task PlayNextAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        return LockedAsync(async () =>
        {
            _queue = _queue.PlayNext(trackIds);
            await RefreshPreloadAsync(ct).ConfigureAwait(false);
        });
    }

    public Task EnqueueAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        return LockedAsync(async () =>
        {
            _queue = _queue.Enqueue(trackIds);
            await RefreshPreloadAsync(ct).ConfigureAwait(false);
        });
    }

    /// <summary>Skips forward, applying the repeat rules for a deliberate skip (<see cref="PlayQueue.Advance"/>).</summary>
    public Task NextAsync(CancellationToken ct = default) => LockedAsync(async () =>
    {
        _queue = _queue.Advance(manual: true);
        await StartCurrentAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
    });

    /// <summary>
    /// Previous: before <see cref="PlayQueue.RestartWindow"/> into the track it goes back, at or after it the
    /// current track restarts (docs/library-and-data.md; Q-21).
    /// </summary>
    public Task PreviousAsync(CancellationToken ct = default) => LockedAsync(async () =>
    {
        PlayQueue moved = _queue.Back(_engine.Clock.Position);
        if (moved.Current == _queue.Current)
        {
            await SeekCoreAsync(TimeSpan.Zero).ConfigureAwait(false);
            return;
        }

        _queue = moved;
        await StartCurrentAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
    });

    public Task SeekAsync(TimeSpan position) => LockedAsync(() => SeekCoreAsync(position));

    /// <summary>Pauses if playing, resumes if paused; with nothing loaded it plays the queue's current item.</summary>
    public Task TogglePlayPauseAsync(CancellationToken ct = default) => LockedAsync(async () =>
    {
        switch (_state)
        {
            case PlaybackState.Playing:
                await _engine.PauseAsync().ConfigureAwait(false);
                _state = PlaybackState.Paused;
                break;
            case PlaybackState.Paused when _current is not null:
                await _engine.ResumeAsync().ConfigureAwait(false);
                _state = PlaybackState.Playing;
                break;
            default:
                await StartCurrentAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
                break;
        }

        Publish();
    });

    /// <summary>Stops and unloads, leaving the queue as it is.</summary>
    public Task StopAsync() => LockedAsync(async () =>
    {
        await SaveCoreAsync(default).ConfigureAwait(false);
        await _engine.StopAsync().ConfigureAwait(false);
        await UnloadAsync().ConfigureAwait(false);
        _state = PlaybackState.Stopped;
        Publish();
    });

    /// <summary>Slider position 0..1 on the engine's audio taper.</summary>
    public void SetVolume(float slider)
    {
        _volume = Math.Clamp(slider, 0f, 1f);
        _engine.SetVolume(_volume);
        Publish();
    }

    /// <summary>Turns shuffle on or off; the current track keeps playing either way.</summary>
    public Task SetShuffleAsync(bool shuffle, CancellationToken ct = default) => LockedAsync(async () =>
    {
        if (_queue.Shuffle != shuffle)
        {
            _queue = _queue.ToggleShuffle(_rng);
            await RefreshPreloadAsync(ct).ConfigureAwait(false);
        }
        else
        {
            Publish();
        }
    });

    /// <summary>Sets the repeat mode; what follows the current track is re-queued for it.</summary>
    public Task SetRepeatAsync(RepeatMode repeat, CancellationToken ct = default) => LockedAsync(async () =>
    {
        _queue = _queue.WithRepeat(repeat);
        await RefreshPreloadAsync(ct).ConfigureAwait(false);
    });

    /// <summary>Removes a queue item; removing the one that is playing starts the one that took its place.</summary>
    public Task RemoveFromQueueAsync(Guid instanceId, CancellationToken ct = default) => LockedAsync(async () =>
    {
        QueueItem? playing = _current?.Item;
        _queue = _queue.Remove(instanceId);
        if (playing is not null && playing.InstanceId == instanceId)
        {
            await StartCurrentAsync(TimeSpan.Zero, ct).ConfigureAwait(false);
            return;
        }

        await RefreshPreloadAsync(ct).ConfigureAwait(false);
    });

    /// <summary>Moves a queue item; what follows the current track is re-queued for the new order.</summary>
    public Task MoveInQueueAsync(Guid instanceId, int toIndex, CancellationToken ct = default) => LockedAsync(async () =>
    {
        _queue = _queue.Move(instanceId, toIndex);
        await RefreshPreloadAsync(ct).ConfigureAwait(false);
    });

    /// <summary>
    /// Drops everything after the current track, which keeps playing. What the engine had pre-opened goes with it —
    /// clearing the upcoming items and leaving the next one queued in the mixer would mean the queue said one thing
    /// and the speakers said another.
    /// </summary>
    public Task ClearUpcomingAsync(CancellationToken ct = default) => LockedAsync(async () =>
    {
        _queue = _queue.ClearUpcoming();
        await RefreshPreloadAsync(ct).ConfigureAwait(false);
    });

    /// <summary>
    /// The queue and position as they stand, for the row that survives a restart (E1-S10). The position belongs to
    /// the queue's current item rather than to the loaded handle, so it outlives a stop: stopping unloads the track
    /// but leaves the item current, and the save that follows the app closing has to report where the user was, not
    /// zero.
    /// </summary>
    public QueueState Capture()
    {
        if (_current is not null)
        {
            _position = _engine.Clock.Position;
        }

        return QueueState.Capture(
            _queue,
            _queue.Current is null ? null : _position,
            _time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    /// <summary>Writes <see cref="Capture"/> to the store. Done on stop and on dispose; callable in between.</summary>
    /// <summary>The level a hover preview plays at (docs/ui-screens-and-flows.md, mode table: −12 dB).</summary>
    public const float PreviewGainDb = -12f;

    /// <inheritdoc />
    /// <remarks>
    /// The handle is opened and closed around the start and not kept: the engine plays a preview on a stream of its
    /// own, opened from the file path (ABI 0.18), so nothing about the preview depends on this handle staying open,
    /// and a session holding one would be one more thing to close on every path out.
    /// </remarks>
    public Task PreviewAsync(long trackId, CancellationToken ct = default) => LockedAsync(async () =>
    {
        IReadOnlyList<TrackDto> found = await _tracks.GetByIdsAsync([trackId], ct).ConfigureAwait(false);
        if (found.Count == 0 || found[0].Missing)
        {
            return;
        }

        TrackHandle handle;
        try
        {
            handle = await _engine.OpenAsync(found[0].Path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogInformation(ex, "Preview of {Path} could not open the file", found[0].Path);
            return;
        }

        try
        {
            await _engine.StartPreviewAsync(handle, PreviewGainDb).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogInformation(ex, "Preview of {Path} did not start", found[0].Path);
        }
        finally
        {
            await _engine.CloseAsync(handle).ConfigureAwait(false);
        }
    });

    /// <summary>
    /// Settings › Output's test tone (E6-S3, AC-146): plays the WAV at <paramref name="wavPath"/> (<see cref="TestTone.Wav"/>)
    /// on the preview stream, so it sounds through the device the output is open on, over music or without it - the core
    /// starts the output when a device opens, not when a track plays. It stops by itself at the end of the file. False
    /// when it did not start, most often because a hover preview is still fading out.
    /// </summary>
    public Task<bool> PlayTestToneAsync(string wavPath, CancellationToken ct = default) => LockedAsync(async () =>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        TrackHandle handle;
        try
        {
            handle = await _engine.OpenAsync(wavPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "The test tone at {Path} could not be opened", wavPath);
            return false;
        }

        try
        {
            // 0 dB: the file is already about -12 dBFS, and the preview's own gain is not a volume the user set.
            await _engine.StartPreviewAsync(handle, 0f).ConfigureAwait(false);
            _log.LogInformation("Test tone started on the open output");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogInformation(ex, "The test tone did not start");
            return false;
        }
        finally
        {
            await _engine.CloseAsync(handle).ConfigureAwait(false);
        }
    });

    /// <inheritdoc />
    public Task StopPreviewAsync() => LockedAsync(async () =>
    {
        try
        {
            await _engine.StopPreviewAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogInformation(ex, "Stopping the preview failed");
        }
    });

    public Task SaveAsync(CancellationToken ct = default) => LockedAsync(() => SaveCoreAsync(ct));

    /// <summary>
    /// Brings back the saved queue, and — when <c>playback.resumeOnLaunch</c> is on — opens the track that was
    /// current, paused at the position it was left at (docs/solution-structure.md, start-up step d). Paused, not
    /// playing: a launch that starts making noise on its own is a launch nobody asked for. Returns false when
    /// there is nothing worth restoring, which includes a saved queue whose tracks have all left the library.
    /// </summary>
    public Task<bool> RestoreAsync(CancellationToken ct = default) => LockedAsync(async () =>
    {
        QueueState? saved = await LoadSavedAsync(ct).ConfigureAwait(false);
        if (saved is null || saved.Items.Count == 0)
        {
            return false;
        }

        await UnloadAsync().ConfigureAwait(false);
        _queue = saved.ToQueue();
        _state = PlaybackState.Stopped;

        if (_queue.Current is null || !_settings.GetValue(SettingsKeys.PlaybackResumeOnLaunch, true))
        {
            Publish();
            return true;
        }

        await StartCurrentAsync(saved.Position ?? TimeSpan.Zero, ct, paused: true).ConfigureAwait(false);
        return true;
    });

    /// <summary>
    /// Publishes a snapshot and acts on anything the engine has recorded since the last poll: a join whose audio
    /// has now been heard, or a track that ended with nothing behind it. The 10 Hz timer calls this; a test calls
    /// it by hand.
    /// </summary>
    public Task PollAsync(CancellationToken ct = default) => LockedAsync(async () =>
    {
        DrainErrors();
        Accrue();
        if (_join is PendingJoin join && _engine.Clock.HasPlayed(join.MixerBytePosition))
        {
            await CommitJoinAsync(join, ct).ConfigureAwait(false);
            return;
        }

        if (_ended)
        {
            _ended = false;
            await _engine.StopAsync(FadeMode.None).ConfigureAwait(false);
            await UnloadAsync().ConfigureAwait(false);
            _queue = _queue.Advance(manual: false);
            _state = PlaybackState.Stopped;
        }

        Publish();
    });

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _events.Dispose();
        await SaveCoreAsync(default).ConfigureAwait(false);
        await UnloadAsync().ConfigureAwait(false);
        _snapshots.Dispose();
        _errors.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// Hands on whatever the engine reported since the last poll. Off the pump thread, in order, and taking a copy
    /// under the lock so a subscriber that does something slow is not holding the thread the driver is calling on.
    /// </summary>
    private void DrainErrors()
    {
        string[] errors;
        lock (_pendingErrors)
        {
            if (_pendingErrors.Count == 0)
            {
                return;
            }

            errors = [.. _pendingErrors];
            _pendingErrors.Clear();
        }

        foreach (string error in errors)
        {
            _errors.OnNext(error);
        }
    }

    // ---- the output going and coming back (E2-S7) ------------------------------------------------------------------

    /// <summary>
    /// Reopens the output on the system default in shared mode — flow 4's "Use default device" — and carries on
    /// from where the loss parked playback. The loaded track is untouched by a device going (E1-S7), so this is a
    /// reopen and a resume, not a reload: the position is the one the user was at.
    /// </summary>
    /// <remarks>
    /// It does not write <c>output.deviceId</c>. This is a recovery from a device that is not there, not a change
    /// of mind about which device to use, and quietly rewriting the preference would mean plugging the DAC back in
    /// never brought it back.
    /// </remarks>
    public Task<bool> UseSystemDefaultOutputAsync(CancellationToken ct = default) => LockedAsync(() =>
        ReopenAsync(
            new OutputConfig(OutputConfig.DefaultDevice, OutputMode.Shared, OutputPolicy.DefaultBufferMs(OutputMode.Shared)),
            ct));

    /// <summary>
    /// Switches to the device the engine has offered back — flow 4's "Switch back" — in the mode and buffer the
    /// user's settings ask for, since this is their device returning rather than a fallback. Does nothing when
    /// nothing is being offered.
    /// </summary>
    public Task<bool> SwitchToReturnedOutputAsync(CancellationToken ct = default) => LockedAsync(() =>
    {
        if (_output.Health != OutputHealth.Returned)
        {
            return Task.FromResult(false);
        }

        OutputPreference preference = OutputPolicy.Read(_settings);
        OutputSelection selection = OutputPolicy.Resolve(
            preference with { DeviceId = _output.DeviceId }, Devices());
        return ReopenAsync(selection.Config, ct);
    });

    /// <summary>
    /// Settings › Output (E6-S3): stores <paramref name="preference"/> and reopens the output on it at once. A playing
    /// track carries on across the reopen, because the engine keeps its streams and only the device changes (E1-S6). A
    /// device named in the preference that is not connected resolves to the system default, as at launch. False when
    /// the output would not open; the preference is still stored, since it is what the user chose.
    /// </summary>
    public Task<bool> ApplyOutputAsync(OutputPreference preference, CancellationToken ct = default) => LockedAsync(async () =>
    {
        OutputPolicy.Write(_settings, preference);
        await _settings.FlushAsync(ct).ConfigureAwait(false);
        OutputSelection selection = OutputPolicy.Resolve(preference, Devices());
        _log.LogInformation(
            "Output changed in settings to {Device} ({Mode}, {BufferMs} ms)",
            selection.Device?.Name ?? "the system default",
            OutputPolicy.ModeName(selection.Config.Mode),
            selection.Config.BufferMs);
        return await ReopenAsync(selection.Config, ct).ConfigureAwait(false);
    });

    /// <summary>The devices the engine reports now, for the Output page's list; empty when they cannot be read.</summary>
    public IReadOnlyList<OutputDevice> OutputDevices() => Devices();

    private IReadOnlyList<OutputDevice> Devices()
    {
        try
        {
            return _engine.EnumerateDevices();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogWarning(e, "Output devices could not be enumerated");
            return [];
        }
    }

    /// <summary>
    /// Opens <paramref name="config"/> and, if playback was running when the output went, starts it again. A
    /// failure leaves the status where it was: the bar the user pressed the button on is still the true one.
    /// </summary>
    private async Task<bool> ReopenAsync(OutputConfig config, CancellationToken ct)
    {
        try
        {
            await _engine.InitializeAsync(config, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogWarning(e, "The output could not be reopened");
            _errors.OnNext("That output could not be opened.");
            return false;
        }

        _output = OutputStatus.Ok;
        if (_resumeWhenOutputReturns && _current is not null)
        {
            await _engine.ResumeAsync().ConfigureAwait(false);
            _state = PlaybackState.Playing;
        }

        _resumeWhenOutputReturns = false;
        Publish();
        return true;
    }

    // ---- the engine talks, the poll acts -------------------------------------------------------------------------

    private void OnEngineEvent(EngineEvent e)
    {
        switch (e.Type)
        {
            case EngineEventType.TrackStarted when _next is LoadedTrack queued && e.A == queued.Handle.Id:
                _join = new PendingJoin(queued, e.B);
                break;
            case EngineEventType.TrackEnded when _current is LoadedTrack playing && e.A == playing.Handle.Id && _next is null && _join is null:
                _ended = true;
                break;
            case EngineEventType.DeviceLost:
                _resumeWhenOutputReturns = _state == PlaybackState.Playing;
                _state = PlaybackState.Paused;
                _output = new OutputStatus(OutputHealth.Lost, e.Message, null);
                break;
            // B = 1 is Windows having moved the default under a caller who asked for the default, and playback has
            // already followed it; there is nothing to offer and nothing to recover from. B = 0 is the device
            // coming back and being offered, which is only interesting while we are on something else.
            case EngineEventType.DeviceChanged when e.B == 1:
                _output = OutputStatus.Ok;
                break;
            case EngineEventType.DeviceChanged when _output.Health == OutputHealth.Lost:
                _output = _output with { Health = OutputHealth.Returned, DeviceId = e.Message ?? _output.DeviceId };
                break;
            case EngineEventType.Error:
                _log.LogWarning("Engine error: {Message}", e.Message);
                if (!string.IsNullOrWhiteSpace(e.Message))
                {
                    lock (_pendingErrors)
                    {
                        _pendingErrors.Add(e.Message);
                    }
                }

                break;
            default:
                break;
        }
    }

    private async Task CommitJoinAsync(PendingJoin join, CancellationToken ct)
    {
        _join = null;
        _next = null;
        LoadedTrack? outgoing = _current;
        Accrue();
        await RecordListenAsync().ConfigureAwait(false);
        _queue = _queue.Advance(manual: false);
        _current = join.Track;
        _state = PlaybackState.Playing;
        BeginListen(join.Track, TimeSpan.Zero);
        if (outgoing is not null)
        {
            await _engine.CloseAsync(outgoing.Handle).ConfigureAwait(false);
        }

        await RefreshPreloadCoreAsync(ct).ConfigureAwait(false);
        Publish();
    }

    // ---- loading -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Opens and plays whatever the queue says is current, skipping past tracks the library no longer has a usable
    /// file for. With nothing current it stops.
    /// </summary>
    private async Task StartCurrentAsync(TimeSpan startAt, CancellationToken ct, bool paused = false)
    {
        await UnloadAsync().ConfigureAwait(false);

        for (int attempts = _queue.Count; attempts >= 0; attempts--)
        {
            if (_queue.Current is not QueueItem item)
            {
                await _engine.StopAsync().ConfigureAwait(false);
                _state = PlaybackState.Stopped;
                Publish();
                return;
            }

            LoadedTrack? loaded = await OpenAsync(item, ct).ConfigureAwait(false);
            if (loaded is null)
            {
                _queue = _queue.Advance(manual: true);
                continue;
            }

            _current = loaded;
            BeginListen(loaded, startAt);
            await _engine.PlayAsync(loaded.Handle, startAt, ct).ConfigureAwait(false);
            _state = PlaybackState.Playing;
            if (paused)
            {
                await _engine.PauseAsync().ConfigureAwait(false);
                _state = PlaybackState.Paused;
            }

            await RefreshPreloadCoreAsync(ct).ConfigureAwait(false);
            Publish();
            return;
        }

        _log.LogWarning("No playable track in a queue of {Count}", _queue.Count);
        _state = PlaybackState.Stopped;
        Publish();
    }

    /// <summary>The track opened and gained, or null when the library cannot give the engine a file to open.</summary>
    private async Task<LoadedTrack?> OpenAsync(QueueItem item, CancellationToken ct)
    {
        IReadOnlyList<TrackDto> found = await _tracks.GetByIdsAsync([item.TrackId], ct).ConfigureAwait(false);
        if (found.Count == 0 || found[0].Missing)
        {
            _log.LogInformation("Skipping track {TrackId}: not in the library, or its file was missing at the last scan", item.TrackId);
            return null;
        }

        TrackDto dto = found[0];
        try
        {
            TrackHandle handle = await _engine.OpenAsync(dto.Path, ct).ConfigureAwait(false);
            _engine.SetReplayGain(handle, Gain(dto).GainDb, Gain(dto).Peak);
            return new LoadedTrack(item, dto, handle);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not open {Path}", dto.Path);
            return null;
        }
    }

    private async Task RefreshPreloadAsync(CancellationToken ct)
    {
        await RefreshPreloadCoreAsync(ct).ConfigureAwait(false);
        Publish();
    }

    /// <summary>
    /// Queues whatever follows the current track, with the join <see cref="CrossfadePolicy"/> chooses for that
    /// boundary and the gain set before the queueing, so it is exact on the frame of a gapless join (E1-S5).
    /// </summary>
    private async Task RefreshPreloadCoreAsync(CancellationToken ct)
    {
        if (_current is not LoadedTrack playing)
        {
            await ClearPreloadAsync().ConfigureAwait(false);
            return;
        }

        _engine.SetCrossfade(CrossfadePolicy.Duration(_settings.GetValue(SettingsKeys.PlaybackCrossfadeMs, 0)));

        if (_queue.PeekNext() is not QueueItem peek)
        {
            await ClearPreloadAsync().ConfigureAwait(false);
            return;
        }

        if (_next is LoadedTrack queued && queued.Item == peek)
        {
            return;
        }

        LoadedTrack? loaded = await OpenAsync(peek, ct).ConfigureAwait(false);
        await ClearPreloadAsync().ConfigureAwait(false);
        if (loaded is null)
        {
            return;
        }

        _next = loaded;
        bool gapless = _settings.GetValue(SettingsKeys.PlaybackGapless, true);
        await _engine.PreloadNextAsync(loaded.Handle, CrossfadePolicy.Resolve(playing.Dto, loaded.Dto, gapless), ct).ConfigureAwait(false);
    }

    private async Task ClearPreloadAsync()
    {
        if (_next is not LoadedTrack queued)
        {
            return;
        }

        _next = null;
        await _engine.PreloadNextAsync(null).ConfigureAwait(false);
        await _engine.CloseAsync(queued.Handle).ConfigureAwait(false);
    }

    private async Task UnloadAsync()
    {
        _join = null;
        _ended = false;
        Accrue();
        await RecordListenAsync().ConfigureAwait(false);
        await ClearPreloadAsync().ConfigureAwait(false);
        if (_current is LoadedTrack playing)
        {
            _current = null;
            await _engine.CloseAsync(playing.Handle).ConfigureAwait(false);
        }
    }

    // ---- what was heard ------------------------------------------------------------------------------------------

    private void BeginListen(LoadedTrack track, TimeSpan startAt)
    {
        _position = startAt;
        _listen = new Listen(track, _time.GetUtcNow().ToUnixTimeMilliseconds(), startAt, _time.GetTimestamp());
    }

    /// <summary>
    /// Adds what has become audible since the last call. Heard time follows the <em>position</em>, capped by the
    /// wall clock: a pause advances neither, and a seek moves the position without crediting the audio it jumped
    /// over. A backward seek adds nothing for the jump itself and then counts the re-heard audio again, which is
    /// what the Last.fm rule this feeds counts too.
    /// </summary>
    private void Accrue()
    {
        if (_listen is not Listen listen)
        {
            return;
        }

        long now = _time.GetTimestamp();
        TimeSpan position = _current is null ? listen.LastPosition : _engine.Clock.Position;
        TimeSpan advanced = position - listen.LastPosition;
        if (_state == PlaybackState.Playing && advanced > TimeSpan.Zero)
        {
            TimeSpan wall = _time.GetElapsedTime(listen.LastTicks, now);
            listen.Heard += advanced < wall ? advanced : wall;
        }

        listen.LastPosition = position;
        listen.LastTicks = now;
    }

    /// <summary>
    /// Hands the finished listen to the history (E3-S11) and forgets it. A history that will not take it is logged
    /// and dropped: losing a play count is not a reason to interrupt the music.
    /// </summary>
    private async Task RecordListenAsync()
    {
        if (_listen is not Listen listen)
        {
            return;
        }

        _listen = null;
        try
        {
            await _history.RecordAsync(
                PlayEvent.For(listen.Track.Item.TrackId, listen.StartedAt, listen.Heard, listen.Track.Handle.Info.Duration))
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not record the play of track {TrackId}", listen.Track.Item.TrackId);
        }
    }

    /// <summary>A store that will not take the queue is logged: it costs the next launch its queue, not this one.</summary>
    private async Task SaveCoreAsync(CancellationToken ct)
    {
        try
        {
            await _queueStore.SaveAsync(Capture(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not save the queue");
        }
    }

    private async Task<QueueState?> LoadSavedAsync(CancellationToken ct)
    {
        try
        {
            return await _queueStore.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Could not read the saved queue");
            return null;
        }
    }

    private async Task SeekCoreAsync(TimeSpan position)
    {
        if (_current is null)
        {
            return;
        }

        await _engine.SeekAsync(position).ConfigureAwait(false);
        Publish();
    }

    private ReplayGainSetting Gain(TrackDto dto) => ReplayGainPolicy.Resolve(
        dto.ReplayGain,
        ReplayGainPolicy.ParseMode(_settings.GetValue<string?>(SettingsKeys.PlaybackReplayGain, null)),
        _settings.GetValue(SettingsKeys.PlaybackReplayGainPreampDb, 0f));

    private void Publish()
    {
        if (_current is not null)
        {
            _position = _engine.Clock.Position;
        }
        else if (_queue.Current is null)
        {
            _position = TimeSpan.Zero;
        }

        _snapshots.OnNext(new PlaybackSnapshot(
            _state,
            _current?.Item,
            _current?.Dto,
            _position,
            _current?.Handle.Info.Duration ?? TimeSpan.Zero,
            _queue,
            _volume,
            _output));
    }

    private async Task<T> LockedAsync<T>(Func<Task<T>> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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

    private async Task LockedAsync(Func<Task> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await work().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record LoadedTrack(QueueItem Item, TrackDto Dto, TrackHandle Handle);

    /// <summary>One listen in progress: when it began, what has been heard, and where the last reading left it.</summary>
    private sealed class Listen(LoadedTrack track, long startedAt, TimeSpan position, long ticks)
    {
        public LoadedTrack Track { get; } = track;

        public long StartedAt { get; } = startedAt;

        public TimeSpan Heard { get; set; }

        public TimeSpan LastPosition { get; set; } = position;

        public long LastTicks { get; set; } = ticks;
    }

    private sealed record PendingJoin(LoadedTrack Track, long MixerBytePosition);
}
