namespace Tunqio.App.Tray;

/// <summary>What the tray icon's menu (or a click on the icon) asks for (E7-S3).</summary>
public enum TrayCommand
{
    PlayPause,
    Next,
    Previous,
    Show,
    Exit,
}

/// <summary>
/// The notification-area icon as <see cref="TrayController"/> drives it (E7-S3, ADR-006): a small face over
/// H.NotifyIcon's <c>TaskbarIcon</c>, so the rules about the menu, the tooltip and when the window may hide are tested over
/// a fake rather than against the machine's real notification area. <see cref="WinUiTrayIcon"/> is the real one.
/// </summary>
/// <remarks>
/// The real icon is a XAML object and is only touched on the thread that created it; the controller posts its writes there.
/// Disposing it removes the icon from the notification area, so no ghost icon is left behind.
/// </remarks>
public interface ITrayIcon : IDisposable
{
    /// <summary>Raised on the UI thread when a menu item is chosen or the icon is clicked.</summary>
    event EventHandler<TrayCommand>? CommandInvoked;

    /// <summary>
    /// True while the icon is actually in the notification area. The window is only ever hidden while this is true, so
    /// Tunqio is never left running with no window and no icon to bring it back.
    /// </summary>
    bool IsVisible { get; }

    /// <summary>The tooltip, already in docs/identity.md's format and within Windows' 127 characters.</summary>
    void SetToolTip(string text);

    /// <summary>The first menu item's label: Play or Pause.</summary>
    void SetPlayPauseText(string text);

    /// <summary>Whether Play/Pause, Next and Previous can do anything: false until audio is up.</summary>
    void SetTransportEnabled(bool enabled);
}
