using Microsoft.Extensions.Logging;
using Tunqio.App.Controls;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Playback;

/// <summary>
/// The system media transport controls for the app's one <see cref="PlaybackSession"/> (E7-S2, ADR-006): what the
/// Windows volume flyout, the lock screen and the hardware media keys see and press. It follows
/// <see cref="PlaybackSession.Snapshots"/> the way the transport panel does and writes to an
/// <see cref="ISystemMediaControls"/>; presses come back through the session's own commands.
/// </summary>
/// <remarks>
/// <para>
/// Snapshots arrive at 10 Hz and almost none of them say anything new to Windows, so every write here is on a
/// change: the status and the buttons when they differ, the music properties when the track does (a display
/// update re-reads the thumbnail, and ten a second would be ten decodes a second), and the timeline on a track
/// change, a state change, a seek, and otherwise every <see cref="TimelineInterval"/> while playing. The flyout
/// extrapolates a playing timeline from its last position itself, so a modest rate is all a moving bar needs.
/// </para>
/// <para>
/// Presses are raised on a Windows thread pool thread. They are handed to the pool again rather than run inline, so
/// the session's lock is never taken on the thread Windows raised the event on, and they run one at a time, so two
/// quick presses of Play read the state the first one left rather than both toggling from the same state.
/// </para>
/// <para>
/// Media keys reach the session only this way. The shell's shortcut table has no media key, and
/// <see cref="Shell.KeyChord"/> will not bind one, so a key pressed while the window has focus is not handled a second
/// time by the shell.
/// </para>
/// </remarks>
public sealed class SmtcBridge : IDisposable
{
    /// <summary>How often the timeline is rewritten while a track plays with nothing else changing.</summary>
    public static readonly TimeSpan TimelineInterval = TimeSpan.FromSeconds(5);

    /// <summary>How far the position may be from where the last write says it should be before it counts as a seek.</summary>
    public static readonly TimeSpan SeekTolerance = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly IPlaybackSessionSource _source;
    private readonly ISystemMediaControls _controls;
    private readonly IArtCache? _art;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    private PlaybackSession? _session;
    private IDisposable? _subscription;
    private bool _disposed;

    private SmtcStatus? _status;
    private SmtcButtons? _buttons;
    private bool _trackWritten;
    private TrackDto? _trackDto;
    private SmtcTrack? _track;
    private QueueItem? _timelineItem;
    private SmtcTimeline? _timeline;
    private SmtcStatus _timelineStatus;
    private DateTimeOffset _timelineAt;

    /// <param name="source">Where the session comes from; it may not exist yet, and may never.</param>
    /// <param name="controls">The controls to drive. The bridge owns them from here and disposes them.</param>
    /// <param name="art">The album art cache for the thumbnail; null when the app has none, and the flyout shows no art.</param>
    /// <param name="time">The clock the timeline's rate is measured on.</param>
    /// <param name="log">Where presses and failures are recorded.</param>
    public SmtcBridge(IPlaybackSessionSource source, ISystemMediaControls controls, IArtCache? art, TimeProvider time, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(controls);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _source = source;
        _controls = controls;
        _art = art;
        _time = time;
        _log = log;
        controls.SetEnabled(false);
        controls.ButtonPressed += OnButtonPressed;
        controls.PositionChangeRequested += OnPositionChangeRequested;
        source.SessionReady += OnSessionReady;
        if (source.Session is { } ready)
        {
            Attach(ready);
        }
    }

    // ---- snapshots to the controls -----------------------------------------------------------------------------------

    private void OnSessionReady(object? sender, PlaybackSession session) => Attach(session);

    private void Attach(PlaybackSession session)
    {
        lock (_gate)
        {
            if (_disposed || _session is not null)
            {
                return;
            }

            _session = session;
            _controls.SetEnabled(true);
        }

        // Outside the gate: a BehaviorSubject hands the current snapshot over inside Subscribe, and Apply takes the gate.
        IDisposable subscription = session.Snapshots.Subscribe(Apply);
        lock (_gate)
        {
            if (_disposed)
            {
                subscription.Dispose();
                return;
            }

            _subscription = subscription;
        }

        _log.LogInformation("Media controls attached to the playback session");
    }

    private void Apply(PlaybackSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                SmtcStatus status = StatusOf(snapshot);
                WriteTrack(snapshot);
                if (_status != status)
                {
                    _controls.SetStatus(status);
                    _status = status;
                }

                SmtcButtons buttons = ButtonsOf(snapshot);
                if (_buttons != buttons)
                {
                    _controls.SetButtons(buttons);
                    _buttons = buttons;
                }

                WriteTimeline(snapshot, status);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A media session Windows has torn down must not take the snapshot stream down with it.
                _log.LogWarning(e, "The system media controls could not be updated");
            }
        }
    }

    /// <summary>Playing and paused are the session's; stopped is split on whether Play has anything to start.</summary>
    public static SmtcStatus StatusOf(PlaybackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.State switch
        {
            PlaybackState.Playing => SmtcStatus.Playing,
            PlaybackState.Paused => SmtcStatus.Paused,
            _ when snapshot.Current is null && snapshot.Queue.Current is null => SmtcStatus.Closed,
            _ => SmtcStatus.Stopped,
        };
    }

    /// <summary>
    /// The buttons the queue can honour. Next is <see cref="PlayQueue.Advance"/> with <c>manual</c> set, which moves on
    /// while there is a later item or repeat wraps, and stops at the end of a queue that does not repeat. Previous is
    /// always honoured while a track is loaded: before three seconds it goes back, and at the first item (or after
    /// three seconds) it restarts the track, which is what a Previous press does in every player.
    /// </summary>
    public static SmtcButtons ButtonsOf(PlaybackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        bool loaded = snapshot.Current is not null;
        PlayQueue queue = snapshot.Queue;
        // Advance(manual: true) without building the queue it would return, since this runs on every snapshot.
        bool next = loaded
            && queue.CurrentIndex is int index
            && (index + 1 < queue.Count || queue.Repeat != RepeatMode.Off);
        return new SmtcButtons(
            Play: loaded || queue.Current is not null,
            Pause: loaded,
            Stop: loaded,
            Next: next,
            Previous: loaded);
    }

    private void WriteTrack(PlaybackSnapshot snapshot)
    {
        // The session hands the same TrackDto on every snapshot of one track, so the reference is the cheap test and the
        // record's value is the one that decides: a new row with nothing the flyout shows changed is not a rewrite.
        if (_trackWritten && ReferenceEquals(snapshot.Track, _trackDto))
        {
            return;
        }

        _trackDto = snapshot.Track;
        SmtcTrack? track = snapshot.Track is { } dto
            ? new SmtcTrack(
                dto.Title,
                dto.ArtistNames,
                dto.AlbumArtist ?? string.Empty,
                dto.AlbumTitle ?? string.Empty,
                dto.TrackNo ?? 0,
                // A file dropped from outside the library (D-24) has no art row: no hash, so no path, and no thumbnail.
                _art?.PathFor(dto.ArtHash, ArtSize.Thumbnail))
            : null;
        if (_trackWritten && track == _track)
        {
            return;
        }

        _controls.SetTrack(track);
        _track = track;
        _trackWritten = true;
    }

    private void WriteTimeline(PlaybackSnapshot snapshot, SmtcStatus status)
    {
        DateTimeOffset now = _time.GetUtcNow();
        TimeSpan end = snapshot.Duration < TimeSpan.Zero ? TimeSpan.Zero : snapshot.Duration;
        TimeSpan position = snapshot.Current is null ? TimeSpan.Zero : Clamp(snapshot.Position, end);

        bool due;
        if (_timeline is null || _timelineItem != snapshot.Current || _timeline.End != end || _timelineStatus != status)
        {
            due = true;
        }
        else
        {
            TimeSpan elapsed = now - _timelineAt;
            TimeSpan expected = _timeline.Position + (status == SmtcStatus.Playing ? elapsed : TimeSpan.Zero);
            due = (position - expected).Duration() > SeekTolerance
                || (status == SmtcStatus.Playing && elapsed >= TimelineInterval);
        }

        if (!due)
        {
            return;
        }

        var timeline = new SmtcTimeline(position, end);
        _controls.SetTimeline(timeline);
        _timeline = timeline;
        _timelineItem = snapshot.Current;
        _timelineStatus = status;
        _timelineAt = now;
    }

    private static TimeSpan Clamp(TimeSpan position, TimeSpan end)
    {
        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return end > TimeSpan.Zero && position > end ? end : position;
    }

    // ---- presses to the session --------------------------------------------------------------------------------------

    private void OnButtonPressed(object? sender, SmtcButton button) =>
        Task.Run(() => PressAsync(button)).Forget("Media control " + button);

    private void OnPositionChangeRequested(object? sender, TimeSpan position) =>
        Task.Run(() => SeekAsync(position)).Forget("Media control seek");

    /// <summary>
    /// What a press of <paramref name="button"/> does, through the same session commands the transport panel calls.
    /// Play and Pause are not toggles: Play on a playing session and Pause on a paused one do nothing, because the
    /// flyout sends the one it is showing and a media key's Play/Pause arrives as whichever the status implies.
    /// </summary>
    public async Task PressAsync(SmtcButton button)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Session() is not { } session)
            {
                return;
            }

            PlaybackState state = session.Current.State;
            _log.LogInformation("Media control {Button} pressed while {State}", button, state);
            Task work = button switch
            {
                SmtcButton.Play when state != PlaybackState.Playing => session.TogglePlayPauseAsync(),
                SmtcButton.Pause when state == PlaybackState.Playing => session.TogglePlayPauseAsync(),
                SmtcButton.Stop => session.StopAsync(),
                SmtcButton.Next => session.NextAsync(),
                SmtcButton.Previous => session.PreviousAsync(),
                _ => Task.CompletedTask,
            };
            await work.ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // A press that raced the shutdown: the session has gone, and there is nothing to do.
        }
        finally
        {
            _commands.Release();
        }
    }

    /// <summary>A seek from the flyout's timeline, clamped to the current track.</summary>
    public async Task SeekAsync(TimeSpan position)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Session() is not { } session || session.Current is not { Current: not null } snapshot)
            {
                return;
            }

            TimeSpan target = Clamp(position, snapshot.Duration);
            _log.LogInformation("Media control seek to {Position}", target);
            await session.SeekAsync(target).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _commands.Release();
        }
    }

    private PlaybackSession? Session()
    {
        lock (_gate)
        {
            return _disposed ? null : _session;
        }
    }

    /// <summary>Closes the media session and releases the controls. Before the session is disposed, at shutdown.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _subscription?.Dispose();
            _subscription = null;
            _source.SessionReady -= OnSessionReady;
            _controls.ButtonPressed -= OnButtonPressed;
            _controls.PositionChangeRequested -= OnPositionChangeRequested;
            try
            {
                _controls.SetStatus(SmtcStatus.Closed);
                _controls.SetEnabled(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _log.LogWarning(e, "The system media controls could not be closed");
            }

            _controls.Dispose();
        }

        // Not disposed: a press already on the pool may still be waiting on it, and it finds _disposed and leaves.
        _log.LogInformation("Media controls closed");
    }
}
