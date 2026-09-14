using Microsoft.Extensions.Logging;
using Tunqio.App.Playback;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Notifications;

/// <summary>
/// The now-playing toast's rules (E7-S4, AC-157, ADR-006). With <c>ui.toastOnTrackChange</c> on, a new track starting to play
/// shows one toast with its title, artist, album and art, and Previous, Play/Pause and Next buttons; the toast carries one tag
/// and group, so the next replaces it. No toast while the shell is in Focus mode, and none while a Tunqio window is the
/// foreground window: a window hidden to the tray, minimised or behind another window is not in the foreground, so it gets one.
/// </summary>
/// <remarks>
/// <para>
/// A track change is the current queue item changing. The toast waits until that item is playing with its row loaded: the
/// queue restored at launch, or a Next pressed while paused, says nothing until the music starts. Each item is decided once.
/// </para>
/// <para>
/// Registration follows the setting. Tunqio registers at start-up only while it is on, and when it is turned on. Turning it off
/// calls <see cref="IToastNotifier.Unregister"/>, which stops this process receiving presses, and removes the toast; it does not
/// remove the user-level registration (the SDK's <c>UnregisterAll</c>). That would delete the notification identity Windows
/// keeps the user's own choices for Tunqio under (Settings, System, Notifications), so turning toasts back on would come back
/// as a new app with those choices forgotten; and with nothing shown and no activations received, what is left registered does
/// nothing. <c>Tunqio.exe --unregister-notifications</c> removes it for good.
/// </para>
/// <para>
/// "In the foreground" means any visible, not minimised window of this process: the mini player counts, because it already
/// shows the track. The two rules are read at the moment a toast would be shown, like the tray's settings.
/// </para>
/// </remarks>
public sealed class ToastController : IDisposable
{
    /// <summary>The buttons, in the order they appear.</summary>
    public static readonly IReadOnlyList<ToastButton> Buttons =
    [
        new("Previous", ToastAction.Previous),
        new("Play/Pause", ToastAction.TogglePlayPause),
        new("Next", ToastAction.Next),
    ];

    private readonly object _gate = new();
    private readonly IPlaybackSessionSource _source;
    private readonly IToastNotifier _notifier;
    private readonly ISettingsStore _settings;
    private readonly IArtCache? _art;
    private readonly string _logoPath;
    private readonly Func<bool> _inFocusMode;
    private readonly Func<bool> _inForeground;
    private readonly ILogger _log;

    private PlaybackSession? _session;
    private IDisposable? _subscription;
    private bool _disposed;
    private bool _registered;
    private QueueItem? _lastItem;
    private QueueItem? _pendingItem;

    /// <param name="source">Where the session comes from; it may not exist yet, and may never.</param>
    /// <param name="notifier">The toasts. The controller owns it from here: disposing the controller removes the toast and disposes it.</param>
    /// <param name="settings">Where <c>ui.toastOnTrackChange</c> is read and followed.</param>
    /// <param name="art">The album art cache; null when the app has none, and every toast shows the logo.</param>
    /// <param name="logoPath">The picture for a track with no art (a file dropped from outside the library, D-24).</param>
    /// <param name="inFocusMode">True while the shell is in Focus mode.</param>
    /// <param name="inForeground">True while a Tunqio window is the foreground window.</param>
    /// <param name="log">Where each decision is recorded.</param>
    public ToastController(
        IPlaybackSessionSource source,
        IToastNotifier notifier,
        ISettingsStore settings,
        IArtCache? art,
        string logoPath,
        Func<bool> inFocusMode,
        Func<bool> inForeground,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(notifier);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(logoPath);
        ArgumentNullException.ThrowIfNull(inFocusMode);
        ArgumentNullException.ThrowIfNull(inForeground);
        ArgumentNullException.ThrowIfNull(log);
        _source = source;
        _notifier = notifier;
        _settings = settings;
        _art = art;
        _logoPath = logoPath;
        _inFocusMode = inFocusMode;
        _inForeground = inForeground;
        _log = log;

        settings.Changed += OnSettingChanged;
        if (IsEnabled)
        {
            Register();
        }

        source.SessionReady += OnSessionReady;
        if (source.Session is { } ready)
        {
            Attach(ready);
        }
    }

    /// <summary><c>ui.toastOnTrackChange</c>, read now.</summary>
    public bool IsEnabled => _settings.GetValue(SettingsKeys.UiToastOnTrackChange, SettingsKeys.Defaults.UiToastOnTrackChange);

    /// <summary>True while this process is registered for toasts.</summary>
    public bool IsRegistered
    {
        get
        {
            lock (_gate)
            {
                return _registered;
            }
        }
    }

    /// <summary>
    /// The toast for <paramref name="track"/>: its title, artist and album, the cached thumbnail when the cache has the file and
    /// the logo otherwise, and the three buttons.
    /// </summary>
    public TrackToast ToastFor(TrackDto track)
    {
        ArgumentNullException.ThrowIfNull(track);
        // A file dropped from outside the library (D-24) has no art row, so no hash and no path; a cleared cache has a path and
        // no file. Both are the logo.
        string? art = _art?.PathFor(track.ArtHash, ArtSize.Thumbnail);
        bool hasArt = art is not null && File.Exists(art);
        return new TrackToast(
            string.IsNullOrWhiteSpace(track.Title) ? Identity.ProductName : track.Title,
            track.ArtistNames ?? string.Empty,
            track.AlbumTitle ?? string.Empty,
            hasArt ? art! : _logoPath,
            hasArt,
            Buttons);
    }

    // ---- the setting -----------------------------------------------------------------------------------------------------

    private void OnSettingChanged(object? sender, string key)
    {
        if (key != SettingsKeys.UiToastOnTrackChange || IsDisposed())
        {
            return;
        }

        if (IsEnabled)
        {
            Register();
        }
        else
        {
            Unregister();
        }
    }

    private void Register()
    {
        lock (_gate)
        {
            if (_disposed || _registered)
            {
                return;
            }

            _registered = true;
        }

        try
        {
            _notifier.Register();
            _log.LogInformation("Toasts: registered for app notifications (ui.toastOnTrackChange on)");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            lock (_gate)
            {
                _registered = false;
            }

            _log.LogError(e, "Toasts: registering for app notifications failed; no toast this session until the setting is turned on again");
        }
    }

    private void Unregister()
    {
        lock (_gate)
        {
            if (!_registered)
            {
                return;
            }

            _registered = false;
        }

        try
        {
            _notifier.Remove();
            _notifier.Unregister();
            _log.LogInformation("Toasts: turned off; the toast is removed and this process no longer receives presses");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogWarning(e, "Toasts: unregistering failed");
        }
    }

    // ---- the session -----------------------------------------------------------------------------------------------------

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
    }

    private void Apply(PlaybackSnapshot snapshot)
    {
        TrackDto track;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (snapshot.Current != _lastItem)
            {
                _lastItem = snapshot.Current;
                _pendingItem = snapshot.Current;
            }

            if (_pendingItem is null || snapshot.Track is null || snapshot.State != PlaybackState.Playing)
            {
                return;
            }

            _pendingItem = null;
            track = snapshot.Track;
        }

        try
        {
            Decide(track);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A notification platform that fails must not take the snapshot stream down with it.
            _log.LogWarning(e, "Toasts: the toast for {Title} could not be shown", track.Title);
        }
    }

    private void Decide(TrackDto track)
    {
        if (!IsEnabled)
        {
            return;
        }

        if (_inFocusMode())
        {
            _log.LogInformation("Toasts: none for {Title}: the shell is in Focus mode", track.Title);
            return;
        }

        if (_inForeground())
        {
            _log.LogInformation("Toasts: none for {Title}: a Tunqio window is in the foreground", track.Title);
            return;
        }

        Register();
        if (!IsRegistered)
        {
            return;
        }

        TrackToast toast = ToastFor(track);
        _notifier.Show(toast);
        _log.LogInformation(
            "Toasts: shown for {Title} by {Artist} ({Picture}, tag {Tag}, group {Group})",
            toast.Title, toast.Artist, toast.IsAlbumArt ? "album art" : "app logo", IToastNotifier.Tag, IToastNotifier.Group);
    }

    private bool IsDisposed()
    {
        lock (_gate)
        {
            return _disposed;
        }
    }

    /// <summary>
    /// Stops following the session and the setting, removes the toast and stops receiving presses. First of the shutdown steps:
    /// a press must not reach a session being torn down, and a now-playing toast for a player that has gone would be a lie.
    /// </summary>
    public void Dispose()
    {
        bool registered;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            registered = _registered;
            _registered = false;
            _subscription?.Dispose();
            _subscription = null;
        }

        _settings.Changed -= OnSettingChanged;
        _source.SessionReady -= OnSessionReady;
        try
        {
            if (registered)
            {
                _notifier.Remove();
                _notifier.Unregister();
            }

            _notifier.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogWarning(e, "Toasts: the toast could not be removed at shutdown");
        }

        _log.LogInformation("Toasts: stopped");
    }
}
