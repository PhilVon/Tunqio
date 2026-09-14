namespace Tunqio.App.Notifications;

/// <summary>What a press on the toast asks for. Each is one of the <c>tunqio://</c> commands (docs/identity.md).</summary>
public enum ToastAction
{
    /// <summary>The toast's body: bring the main window forward.</summary>
    Show,

    /// <summary>Previous track (or restart, per the session's rule).</summary>
    Previous,

    /// <summary>Play / pause.</summary>
    TogglePlayPause,

    /// <summary>Next track.</summary>
    Next,
}

/// <summary>One button on the toast.</summary>
public sealed record ToastButton(string Label, ToastAction Action);

/// <summary>
/// The now-playing toast, as data: what <see cref="ToastController"/> decided to show and what an
/// <see cref="IToastNotifier"/> turns into a Windows app notification.
/// </summary>
/// <param name="Title">The track's title, the toast's first line.</param>
/// <param name="Artist">The artist line; empty when the track has none.</param>
/// <param name="Album">The album line; empty when the track has none.</param>
/// <param name="ImagePath">A full path to the picture: the cached album art, or the app logo when there is none.</param>
/// <param name="IsAlbumArt">True when <paramref name="ImagePath"/> is the track's own art, false for the logo.</param>
/// <param name="Buttons">Previous, Play/Pause and Next, in that order.</param>
public sealed record TrackToast(string Title, string Artist, string Album, string ImagePath, bool IsAlbumArt, IReadOnlyList<ToastButton> Buttons);

/// <summary>
/// Windows app notifications as <see cref="ToastController"/> needs them (E7-S4, ADR-006), so the rules are testable over a
/// fake. Every toast carries <see cref="Tag"/> and <see cref="Group"/>, so a new one replaces the last rather than stacking.
/// </summary>
public interface IToastNotifier : IDisposable
{
    /// <summary>The tag every now-playing toast carries.</summary>
    public const string Tag = "now-playing";

    /// <summary>The group every now-playing toast carries.</summary>
    public const string Group = "tunqio";

    /// <summary>Registers this process to show toasts and receive their activations. Idempotent.</summary>
    void Register();

    /// <summary>Stops receiving activations in this process. Idempotent; leaves the user-level registration in place.</summary>
    void Unregister();

    /// <summary>Shows <paramref name="toast"/>, replacing any toast with the same tag and group.</summary>
    void Show(TrackToast toast);

    /// <summary>Removes the now-playing toast from the screen and the notification centre, if one is there.</summary>
    void Remove();
}
