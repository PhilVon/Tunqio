namespace Tunqio.App.Playback;

/// <summary>What the system media session says the player is doing (E7-S2).</summary>
public enum SmtcStatus
{
    /// <summary>Nothing loaded and nothing queued: Windows takes the session out of the volume flyout.</summary>
    Closed,

    /// <summary>Nothing loaded, but the queue has an item Play would start.</summary>
    Stopped,

    Playing,

    Paused,
}

/// <summary>A button the system media session can press: the flyout's, a hardware media key's, or another app's.</summary>
public enum SmtcButton
{
    Play,
    Pause,
    Stop,
    Next,
    Previous,
}

/// <summary>The music properties the flyout shows for one track, and the cached art file for its thumbnail.</summary>
/// <param name="ThumbnailPath">The art cache's file for the track, or null when there is none (a transient file, D-24).</param>
public sealed record SmtcTrack(
    string Title,
    string Artist,
    string AlbumArtist,
    string AlbumTitle,
    int TrackNumber,
    string? ThumbnailPath);

/// <summary>The timeline the flyout draws: it always starts at zero and seeks anywhere up to <see cref="End"/>.</summary>
public sealed record SmtcTimeline(TimeSpan Position, TimeSpan End);

/// <summary>Which buttons the session offers.</summary>
public readonly record struct SmtcButtons(bool Play, bool Pause, bool Stop, bool Next, bool Previous)
{
    public static SmtcButtons None { get; } = default;
}

/// <summary>
/// The system media transport controls as <see cref="SmtcBridge"/> drives them (E7-S2, ADR-006): a small face over
/// the WinRT <c>SystemMediaTransportControls</c> object, so the rules about what to write and when are tested over a
/// fake rather than against the machine's one media session. <see cref="WindowsMediaControls"/> is the real one.
/// </summary>
/// <remarks>
/// The events are raised on whatever thread Windows raises them on, which is not the UI thread. An implementation
/// must accept the setters from any thread: the bridge calls them from the session's snapshot thread.
/// </remarks>
public interface ISystemMediaControls : IDisposable
{
    /// <summary>Raised for a press of <see cref="SmtcButton"/>, from the flyout, a media key or another app.</summary>
    event EventHandler<SmtcButton>? ButtonPressed;

    /// <summary>Raised when the flyout (or another app) asks for a position in the current track.</summary>
    event EventHandler<TimeSpan>? PositionChangeRequested;

    /// <summary>Whether the session exists at all. Off until there is a <c>PlaybackSession</c> to command.</summary>
    void SetEnabled(bool enabled);

    void SetStatus(SmtcStatus status);

    void SetButtons(SmtcButtons buttons);

    /// <summary>The track the flyout shows, or null to clear it.</summary>
    void SetTrack(SmtcTrack? track);

    void SetTimeline(SmtcTimeline timeline);
}
