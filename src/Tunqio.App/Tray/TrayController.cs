using Microsoft.Extensions.Logging;
using Tunqio.App.Controls;
using Tunqio.App.Playback;
using Tunqio.Core;
using Tunqio.Core.Playback;

namespace Tunqio.App.Tray;

/// <summary>
/// The tray icon's rules (E7-S3, ADR-006): the menu drives the app's one <see cref="PlaybackSession"/> through its own
/// commands, the tooltip follows the playing track in docs/identity.md's formats, Play/Pause is labelled for the state, and
/// it decides whether closing or minimising the main window hides it to the tray (<c>ui.closeToTray</c>,
/// <c>ui.minimizeToTray</c>). Showing and exiting are the app's: it is handed the two actions.
/// </summary>
/// <remarks>
/// <para>
/// Snapshots arrive at 10 Hz on the session's thread. Nothing is written to the icon unless the tooltip, the label or the
/// enabled state has changed, and each write is posted to the UI thread the icon belongs to.
/// </para>
/// <para>
/// The two settings are read at the moment they are needed, not cached, so a switch flipped in Settings applies to the very
/// next close or minimise. Neither hides anything while the icon is not in the notification area.
/// </para>
/// </remarks>
public sealed class TrayController : IDisposable
{
    /// <summary>The first menu item's label while nothing is playing.</summary>
    public const string PlayText = "Play";

    /// <summary>The first menu item's label while a track plays.</summary>
    public const string PauseText = "Pause";

    private readonly object _gate = new();
    private readonly IPlaybackSessionSource _source;
    private readonly ITrayIcon _icon;
    private readonly ISettingsStore _settings;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly SynchronizationContext? _ui;
    private readonly ILogger _log;

    private PlaybackSession? _session;
    private IDisposable? _subscription;
    private bool _disposed;
    private string? _toolTip;
    private string? _playPauseText;
    private bool? _transportEnabled;

    /// <param name="source">Where the session comes from; it may not exist yet, and may never.</param>
    /// <param name="icon">The icon to drive. The controller owns it from here and disposes it, which removes it.</param>
    /// <param name="settings">Where <c>ui.closeToTray</c> and <c>ui.minimizeToTray</c> are read.</param>
    /// <param name="show">Brings the main window back and to the foreground.</param>
    /// <param name="exit">Closes the main window through the app's shutdown path.</param>
    /// <param name="ui">The icon's thread; null writes inline (tests).</param>
    /// <param name="log">Where commands and failures are recorded.</param>
    public TrayController(
        IPlaybackSessionSource source,
        ITrayIcon icon,
        ISettingsStore settings,
        Action show,
        Action exit,
        SynchronizationContext? ui,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(icon);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(show);
        ArgumentNullException.ThrowIfNull(exit);
        ArgumentNullException.ThrowIfNull(log);
        _source = source;
        _icon = icon;
        _settings = settings;
        _show = show;
        _exit = exit;
        _ui = ui;
        _log = log;

        Write(Identity.TrayTooltip(null, null), PlayText, transportEnabled: false);
        icon.CommandInvoked += OnCommandInvoked;
        source.SessionReady += OnSessionReady;
        if (source.Session is { } ready)
        {
            Attach(ready);
        }
    }

    /// <summary>True once Exit has been chosen: the close that follows is a real one, whatever <c>ui.closeToTray</c> says.</summary>
    public bool IsExiting { get; private set; }

    /// <summary>The tooltip for <paramref name="snapshot"/>: <c>Tunqio</c> idle, <c>Title – Artist</c> with a track loaded.</summary>
    public static string ToolTipFor(PlaybackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Identity.TrayTooltip(snapshot.Track?.Title, snapshot.Track?.ArtistNames);
    }

    /// <summary>What the first menu item says for <paramref name="snapshot"/>: Pause while playing, Play otherwise.</summary>
    public static string PlayPauseTextFor(PlaybackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.State == PlaybackState.Playing ? PauseText : PlayText;
    }

    /// <summary>
    /// Whether a close of the main window should hide it instead: <c>ui.closeToTray</c> is on, the icon is there to bring it
    /// back, and the close is not the one Exit asked for.
    /// </summary>
    public bool ShouldHideOnClose() =>
        !IsExiting && !IsDisposed() && _icon.IsVisible && _settings.GetValue(SettingsKeys.UiCloseToTray, SettingsKeys.Defaults.UiCloseToTray);

    /// <summary>Whether minimising the main window should hide it: <c>ui.minimizeToTray</c> is on and the icon is there.</summary>
    public bool ShouldHideOnMinimize() =>
        !IsExiting && !IsDisposed() && _icon.IsVisible && _settings.GetValue(SettingsKeys.UiMinimizeToTray, SettingsKeys.Defaults.UiMinimizeToTray);

    // ---- the session to the icon ---------------------------------------------------------------------------------------

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

        _log.LogInformation("Tray icon attached to the playback session");
    }

    private void Apply(PlaybackSnapshot snapshot) =>
        Write(ToolTipFor(snapshot), PlayPauseTextFor(snapshot), transportEnabled: true);

    /// <summary>Posts to the icon only what has changed since the last write.</summary>
    private void Write(string toolTip, string playPauseText, bool transportEnabled)
    {
        string? newToolTip;
        string? newText;
        bool? newEnabled;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            newToolTip = toolTip == _toolTip ? null : toolTip;
            newText = playPauseText == _playPauseText ? null : playPauseText;
            newEnabled = transportEnabled == _transportEnabled ? null : transportEnabled;
            _toolTip = toolTip;
            _playPauseText = playPauseText;
            _transportEnabled = transportEnabled;
        }

        if (newToolTip is null && newText is null && newEnabled is null)
        {
            return;
        }

        OnUi(() =>
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
            }

            try
            {
                if (newToolTip is not null)
                {
                    _icon.SetToolTip(newToolTip);
                }

                if (newText is not null)
                {
                    _icon.SetPlayPauseText(newText);
                }

                if (newEnabled is { } enabled)
                {
                    _icon.SetTransportEnabled(enabled);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A notification area Windows has torn down (Explorer restarting) must not take the snapshot stream with it.
                _log.LogWarning(e, "The tray icon could not be updated");
            }
        });
    }

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            // No JoinableTaskFactory in this app; the context is the XAML thread's DispatcherQueue one and Post never blocks the caller.
#pragma warning disable VSTHRD001
            _ui.Post(_ => action(), null);
#pragma warning restore VSTHRD001
        }
    }

    // ---- the menu to the session ---------------------------------------------------------------------------------------

    private void OnCommandInvoked(object? sender, TrayCommand command) => InvokeAsync(command).Forget("Tray " + command);

    /// <summary>
    /// What choosing <paramref name="command"/> does: the transport through the session's own commands (a press before audio
    /// is up does nothing), Show and Exit through the app's actions.
    /// </summary>
    public async Task InvokeAsync(TrayCommand command)
    {
        if (IsDisposed())
        {
            return;
        }

        _log.LogInformation("Tray menu: {Command}", command);
        try
        {
            switch (command)
            {
                case TrayCommand.Show:
                    _show();
                    return;
                case TrayCommand.Exit:
                    IsExiting = true;
                    _exit();
                    return;
            }

            PlaybackSession? session;
            lock (_gate)
            {
                session = _session;
            }

            if (session is null)
            {
                _log.LogInformation("Tray menu: {Command} ignored, audio is not up", command);
                return;
            }

            Task work = command switch
            {
                TrayCommand.PlayPause => session.TogglePlayPauseAsync(),
                TrayCommand.Next => session.NextAsync(),
                TrayCommand.Previous => session.PreviousAsync(),
                _ => Task.CompletedTask,
            };
            await work.ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // A press that raced the shutdown: the session has gone, and there is nothing to do.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogError(e, "Tray menu: {Command} failed", command);
        }
    }

    private bool IsDisposed()
    {
        lock (_gate)
        {
            return _disposed;
        }
    }

    /// <summary>Stops following the session and removes the icon. First of the shutdown steps, on the UI thread.</summary>
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
        }

        _source.SessionReady -= OnSessionReady;
        _icon.CommandInvoked -= OnCommandInvoked;
        try
        {
            _icon.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogWarning(e, "The tray icon could not be removed");
        }

        _log.LogInformation("Tray icon removed");
    }
}
