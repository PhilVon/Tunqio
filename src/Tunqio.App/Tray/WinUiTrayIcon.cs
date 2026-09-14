using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml.Controls;
using Tunqio.Core;

namespace Tunqio.App.Tray;

/// <summary>
/// The real <see cref="ITrayIcon"/> (E7-S3, ADR-006): H.NotifyIcon.WinUI's <c>TaskbarIcon</c> with a <c>MenuFlyout</c> for
/// its menu. Built and used on the XAML thread.
/// </summary>
/// <remarks>
/// <para>
/// The menu is <see cref="ContextMenuMode.PopupMenu"/>: the library turns the flyout's items into a native popup menu each
/// time it opens, reading their current text and enabled state, and runs the chosen item's command. It needs no second XAML
/// window, which matters here: a second window alive or torn down out of order at shutdown is what T-188 chased.
/// </para>
/// <para>
/// <c>ForceCreate(enablesEfficiencyMode: false)</c>: the library's default would put the whole process into Windows' efficiency
/// mode, which lowers its priority and would throttle the audio and the visualizer.
/// </para>
/// </remarks>
internal sealed class WinUiTrayIcon : ITrayIcon
{
    private readonly TaskbarIcon _icon;
    private readonly MenuFlyoutItem _playPause;
    private readonly MenuFlyoutItem _next;
    private readonly MenuFlyoutItem _previous;
    private bool _disposed;

    /// <param name="iconPath">The .ico file shown in the notification area.</param>
    public WinUiTrayIcon(string iconPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(iconPath);
        _playPause = Item(TrayController.PlayText, TrayCommand.PlayPause);
        _next = Item("Next", TrayCommand.Next);
        _previous = Item("Previous", TrayCommand.Previous);
        var menu = new MenuFlyout();
        menu.Items.Add(_playPause);
        menu.Items.Add(_next);
        menu.Items.Add(_previous);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Show " + Identity.ProductName, TrayCommand.Show));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Exit", TrayCommand.Exit));

        _icon = new TaskbarIcon
        {
            ToolTipText = Identity.ProductName,
            ContextMenuMode = ContextMenuMode.PopupMenu,
            MenuActivation = PopupActivationMode.RightClick,
            ContextFlyout = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(() => Raise(TrayCommand.Show)),
        };
        try
        {
            _icon.Icon = new System.Drawing.Icon(iconPath);
            _icon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch
        {
            _icon.Dispose();
            throw;
        }
    }

    public event EventHandler<TrayCommand>? CommandInvoked;

    public bool IsVisible => !_disposed && _icon.IsCreated;

    public void SetToolTip(string text) => _icon.ToolTipText = text;

    public void SetPlayPauseText(string text) => _playPause.Text = text;

    public void SetTransportEnabled(bool enabled)
    {
        _playPause.IsEnabled = enabled;
        _next.IsEnabled = enabled;
        _previous.IsEnabled = enabled;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // TaskbarIcon.Dispose removes the icon from the notification area (TrayIcon.TryRemove) and disposes the Icon.
        _icon.Dispose();
    }

    private MenuFlyoutItem Item(string text, TrayCommand command) =>
        new() { Text = text, Command = new RelayCommand(() => Raise(command)) };

    private void Raise(TrayCommand command)
    {
        if (!_disposed)
        {
            CommandInvoked?.Invoke(this, command);
        }
    }
}
