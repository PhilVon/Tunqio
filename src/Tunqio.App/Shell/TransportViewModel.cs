using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.App.Playback;
using Tunqio.Core.Playback;

namespace Tunqio.App.Shell;

/// <summary>
/// The transport panel's state and commands (E2-S2). It owns the one piece of playback state the shell is allowed
/// to hold — the position while the user is dragging the scrubber — and reflects <see cref="PlaybackSnapshot"/>
/// for everything else (ADR-008: the session is the single owner).
/// </summary>
/// <remarks>
/// <para>
/// The scrub is why this is a view model rather than bindings straight onto the snapshot. Snapshots arrive at
/// 10 Hz; a slider bound directly to them fights the thumb the user is holding, and the thumb jumps back every
/// 100 ms. So a drag takes the position over: <see cref="BeginScrub"/> stops snapshots writing it,
/// <see cref="ScrubTo"/> moves it, and <see cref="CommitScrubAsync"/> seeks and hands it back (AC-71).
/// </para>
/// <para>
/// Snapshots arrive on the session's timer thread, so every update is posted to the UI context the shell passes
/// in. Nothing here touches a XAML type, which is what lets the whole of AC-71 and AC-72 be tested against a real
/// session over a fake engine rather than by dragging a slider.
/// </para>
/// </remarks>
public sealed partial class TransportViewModel : ObservableObject, IDisposable
{
    private readonly SynchronizationContext? _ui;
    private readonly IPlaybackSessionSource _source;
    private IDisposable? _subscription;
    private PlaybackSession? _session;
    private float _volumeBeforeMute = 1f;
    private bool _disposed;

    [ObservableProperty]
    public partial PlaybackSnapshot Snapshot { get; set; } = PlaybackSnapshot.Idle;

    [ObservableProperty]
    public partial double PositionSeconds { get; set; }

    [ObservableProperty]
    public partial double DurationSeconds { get; set; }

    [ObservableProperty]
    public partial bool IsScrubbing { get; set; }

    [ObservableProperty]
    public partial bool ShowRemaining { get; set; }

    [ObservableProperty]
    public partial float Volume { get; set; } = 1f;

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    /// <param name="source">Where the session comes from; it may not exist yet, and may never.</param>
    /// <param name="ui">The XAML thread's context. Null runs updates inline, which is what the tests want.</param>
    public TransportViewModel(IPlaybackSessionSource source, SynchronizationContext? ui = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _ui = ui;
        if (source.Session is { } ready)
        {
            Attach(ready);
        }
        else
        {
            source.SessionReady += OnSessionReady;
        }
    }

    /// <summary>True once there is a session to command; the panel is visible but inert before that.</summary>
    public bool IsReady => _session is not null;

    /// <summary>Whether the transport can do anything: there is a session and something is loaded.</summary>
    public bool HasTrack => _session is not null && Snapshot.Current is not null;

    /// <summary>What the play button does next.</summary>
    public bool IsPlaying => Snapshot.State == PlaybackState.Playing;

    /// <summary>Elapsed time, or what is left when the user has toggled the label (AC-71's readout).</summary>
    public string PositionText => ShowRemaining
        ? "-" + Clock(Math.Max(0, DurationSeconds - PositionSeconds))
        : Clock(PositionSeconds);

    /// <summary>The track length, always as elapsed-style text.</summary>
    public string DurationText => Clock(DurationSeconds);

    /// <summary>Shuffle as the snapshot has it (AC-72).</summary>
    public bool IsShuffled => Snapshot.Shuffle;

    /// <summary>Repeat as the snapshot has it (AC-72).</summary>
    public RepeatMode Repeat => Snapshot.Repeat;

    /// <summary>The glyph the repeat button shows: off and all share one, repeat-one has its own.</summary>
    public string RepeatGlyph => Repeat == RepeatMode.One ? "" : "";

    /// <summary>What Narrator reads for the repeat button, which is the state and not the glyph.</summary>
    public string RepeatLabel => Repeat switch
    {
        RepeatMode.All => "Repeat all",
        RepeatMode.One => "Repeat one",
        _ => "Repeat off",
    };

    /// <summary>What Narrator reads for the play button.</summary>
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    /// <summary>What Narrator reads for the shuffle button.</summary>
    public string ShuffleLabel => IsShuffled ? "Shuffle on" : "Shuffle off";

    /// <summary>What Narrator reads for the mute button.</summary>
    public string MuteLabel => IsMuted ? "Unmute" : "Mute";

    /// <summary>
    /// The scrubber's value announced as a time rather than as a number of seconds, which is what a slider would
    /// otherwise read out.
    /// </summary>
    public string ScrubberLabel => string.Create(
        CultureInfo.InvariantCulture, $"Seek. {Clock(PositionSeconds)} of {DurationText}");

    // ---- commands ------------------------------------------------------------------------------------------------

    public Task PlayPauseAsync() => _session?.TogglePlayPauseAsync() ?? Task.CompletedTask;

    public Task NextAsync() => _session?.NextAsync() ?? Task.CompletedTask;

    public Task PreviousAsync() => _session?.PreviousAsync() ?? Task.CompletedTask;

    /// <summary>Nudges the position, for the arrow-key accelerators (±5 s, ±30 s with Shift).</summary>
    public Task NudgeAsync(TimeSpan by)
    {
        if (_session is null || !HasTrack)
        {
            return Task.CompletedTask;
        }

        double target = Math.Clamp(PositionSeconds + by.TotalSeconds, 0, Math.Max(0, DurationSeconds));
        PositionSeconds = target;
        return _session.SeekAsync(TimeSpan.FromSeconds(target));
    }

    public Task ToggleShuffleAsync() => _session?.SetShuffleAsync(!IsShuffled) ?? Task.CompletedTask;

    /// <summary>Cycles off, all, one — the order the button's three states are read in.</summary>
    public Task CycleRepeatAsync() => _session?.SetRepeatAsync(Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    }) ?? Task.CompletedTask;

    /// <summary>Toggles the elapsed/remaining readout.</summary>
    public void ToggleTimeDisplay() => ShowRemaining = !ShowRemaining;

    /// <summary>Sets the output level and leaves mute if the user moved the slider off zero themselves.</summary>
    public void SetVolume(float value)
    {
        float clamped = Math.Clamp(value, 0f, 1f);
        Volume = clamped;
        if (clamped > 0)
        {
            IsMuted = false;
            _volumeBeforeMute = clamped;
        }

        _session?.SetVolume(IsMuted ? 0f : clamped);
    }

    /// <summary>Mutes, remembering the level to come back to; unmuting restores it rather than jumping to full.</summary>
    public void ToggleMute()
    {
        if (IsMuted)
        {
            IsMuted = false;
            Volume = _volumeBeforeMute;
            _session?.SetVolume(_volumeBeforeMute);
            return;
        }

        _volumeBeforeMute = Volume > 0 ? Volume : _volumeBeforeMute;
        IsMuted = true;
        _session?.SetVolume(0f);
    }

    // ---- the scrub -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The user has taken hold of the scrubber. From here until <see cref="CommitScrubAsync"/> the position is
    /// theirs, and arriving snapshots update everything else but leave it alone.
    /// </summary>
    public void BeginScrub() => IsScrubbing = true;

    /// <summary>Moves the held scrubber; the label follows so the target time is visible during the drag (AC-71).</summary>
    public void ScrubTo(double seconds)
    {
        if (IsScrubbing)
        {
            PositionSeconds = Math.Clamp(seconds, 0, Math.Max(0, DurationSeconds));
        }
    }

    /// <summary>Releases the scrubber: seek to where it was left, and let snapshots drive the position again.</summary>
    public async Task CommitScrubAsync()
    {
        if (!IsScrubbing)
        {
            return;
        }

        double target = PositionSeconds;
        IsScrubbing = false;
        if (_session is not null)
        {
            await _session.SeekAsync(TimeSpan.FromSeconds(target)).ConfigureAwait(false);
        }
    }

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
        Snapshot = next;
        DurationSeconds = next.Duration.TotalSeconds;
        if (!IsScrubbing)
        {
            PositionSeconds = next.Position.TotalSeconds;
        }

        if (!IsMuted)
        {
            Volume = next.Volume;
        }

        OnPropertyChanged(nameof(HasTrack));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(PlayPauseLabel));
        OnPropertyChanged(nameof(IsShuffled));
        OnPropertyChanged(nameof(ShuffleLabel));
        OnPropertyChanged(nameof(Repeat));
        OnPropertyChanged(nameof(RepeatGlyph));
        OnPropertyChanged(nameof(RepeatLabel));
    }

    partial void OnPositionSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(ScrubberLabel));
    }

    partial void OnDurationSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(ScrubberLabel));
    }

    partial void OnShowRemainingChanged(bool value) => OnPropertyChanged(nameof(PositionText));

    partial void OnIsMutedChanged(bool value) => OnPropertyChanged(nameof(MuteLabel));

    /// <summary>
    /// A number of seconds as the clock reads it, through the formatter the track lists already use so a duration
    /// is written the same way everywhere. Negatives are floored: a position past the end is a rounding artefact of
    /// the 10 Hz sampling, not something to render as "-0:01".
    /// </summary>
    private static string Clock(double seconds) => Controls.Format.Duration((int)(Math.Max(0, seconds) * 1000));

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
