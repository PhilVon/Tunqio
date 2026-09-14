# Windows Platform Integration

> **Status: foundation document, partially superseded.** WinForms NotifyIcon, WPF HwndSource/WindowInteropHelper, WM_APPCOMMAND hooks, DesktopNotificationManagerCompat and direct registry writes are replaced by SMTC, H.NotifyIcon.WinUI, Windows App SDK AppNotifications and manifest-declared associations (ADR-006). Windows Hello and premium features are removed (ADR-008). The feature list itself stands. All product names, identifiers and title/tooltip formats come from [identity.md](identity.md). See [decisions.md](decisions.md) for the record and [README.md](README.md) for the current reading order.

This document details Windows-specific integration features that provide seamless desktop experience including file associations, system tray functionality, media key support, and modern Windows 11 features.

## File Association Management

### Audio Format Registration

**As built (E7-S1, T-74).** The manifest sample below is the original design and its extension list is out of date; the
handler class and the context-menu registry code further down are superseded outright (ADR-006: no registry writes).
What ships:

- **Manifest.** `src/Tunqio.App/Package.appxmanifest` declares one `uap:FileTypeAssociation Name="tunqio-audio"` (display
  name `Tunqio audio file`) with a `uap:FileType` for every extension `Tunqio.Core.Library.AudioFormats` gives the
  library scanner: `.mp3 .flac .m4a .mp4 .aac .ogg .oga .opus .wav .aif .aiff .aifc .wma .wv .ape` (MPC left 1.0 with
  Q-26). Beside it, `uap:Protocol Name="tunqio"` and `uap5:AppExecutionAlias` with `tunqio.exe`;
  `desktop6:FileSystemWriteVirtualization` stays `disabled` with the `unvirtualizedResources` capability. Names come from
  [identity.md](identity.md). There is no `uap:Logo`: the association uses the app's icon until the artwork task adds
  `Assets/FileAssociation.png`.
- **Checked twice.** `Tunqio.Core.Tests` (`IdentityTests`) compares the source manifest with `AudioFormats.Extensions` and
  the `Identity` constants, so adding a format without an association fails a test. `tools/check-package.ps1 -Msix`
  reads `AppxManifest.xml` out of the built package (what an install registers from) and asserts the association (every
  scanner extension, nothing extra), the scheme, the alias and the unvirtualised writes, taking the expected values from
  `AudioFormats.cs` and `Identity.cs` rather than a list of its own.
- **Activation.** A file, `tunqio://` URI or command line reaches `CommandRouter` (`Tunqio.App/Activation`), described
  under "Protocol activation" below; single instance is in [solution-structure.md](solution-structure.md), "Startup
  sequence".
- **Proven unpackaged; waits on an installed package.** The router, the redirection and the command line are proven
  without installing anything, by unit tests and `tools/check-single-instance.ps1`. What Windows itself does for an
  installed package (Explorer's double-click and multi-select Open, a browser following a `tunqio://` link, the real
  write path under `%LocalAppData%\Tunqio`) needs the package installed, which needs its self-signed certificate
  trusted; Phil chose to leave that until the package has a real signature (Q-105), so those checks are on T-80 (E8-S1).

### Protocol activation (as built, E7-S1)

`CommandRouter` turns one activation's input, always a list of strings, into session commands:

| Input | Command |
|-------|---------|
| one audio file (Explorer, `Tunqio.exe song.flac`) | play it now, inserted after the current item with the queue kept (flow 2); window to the foreground |
| several files, or a folder | replace the queue with all of them in the order given, and play; window to the foreground |
| `tunqio://play?path=<url-encoded path>` (repeatable) | replace the queue and play; window to the foreground |
| `tunqio://queue?path=<url-encoded path>` | append to the queue |
| `tunqio://toggle`, `tunqio://next`, `tunqio://previous` | transport |
| `tunqio://show`, or a second launch with no arguments | window to the foreground |

The app's own switches (`--data-root PATH` and the spike switches) are skipped. Everything else is refused with one
warning line and never throws: an unknown command or switch, a `play` or `queue` with no `path`, a path that does not
exist or is not a supported format, and a relative path from anywhere but this process's own command line (a redirected
activation and a URI carry no working directory). Query values are percent-decoded and `+` is kept, because it is legal
in a file name. The scheme and a browser's trailing slash (`tunqio://play/?path=`) are case- and slash-tolerant.
Commands go through `SessionCommandTarget`: files resolve through `OpenFilesService` (a library row where one exists,
else a transient track, D-24), transport goes to the one `PlaybackSession`, and a command that arrives before audio is up
waits for it for up to 30 s rather than being dropped, which is what lets a cold start from Explorer play its file.

The application registers as a handler for supported audio formats through the Windows Registry and WinUI 3 package manifest:

```xml
<!-- Package.appxmanifest -->
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">

  <Applications>
    <Application Id="Tunqio">
      <Extensions>
        <!-- File type associations -->
        <uap:Extension Category="windows.fileTypeAssociation">
          <uap:FileTypeAssociation Name="tunqio-audio">
            <uap:SupportedFileTypes>
              <uap:FileType>.mp3</uap:FileType>
              <uap:FileType>.flac</uap:FileType>
              <uap:FileType>.wav</uap:FileType>
              <uap:FileType>.aac</uap:FileType>
              <uap:FileType>.ogg</uap:FileType>
              <uap:FileType>.wma</uap:FileType>
              <uap:FileType>.m4a</uap:FileType>
            </uap:SupportedFileTypes>
            <uap:DisplayName>Tunqio audio file</uap:DisplayName>
            <uap:Logo>Assets\FileAssociation.png</uap:Logo>
            <uap:InfoTip>Audio file playable by Tunqio</uap:InfoTip>
            <uap:EditFlags OpenIsSafe="true" />
          </uap:FileTypeAssociation>
        </uap:Extension>

        <!-- Protocol activation -->
        <uap:Extension Category="windows.protocol">
          <uap:Protocol Name="tunqio">
            <uap:DisplayName>Tunqio</uap:DisplayName>
          </uap:Protocol>
        </uap:Extension>
      </Extensions>
    </Application>
  </Applications>
</Package>
```

### File Association Handler Implementation

```csharp
public class FileAssociationHandler
{
    public static async Task<bool> RegisterFileAssociations()
    {
        try
        {
            // Get the app's executable path
            var packageLocation = Package.Current.InstalledLocation;
            var executable = await packageLocation.GetFileAsync("Tunqio.exe");

            // Register with Windows for supported formats
            var supportedFormats = new[] { ".mp3", ".flac", ".wav", ".aac", ".ogg", ".wma", ".m4a" };

            foreach (var format in supportedFormats)
            {
                await RegisterFormatAssociation(format, executable.Path);
            }

            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to register file associations: {ex.Message}");
            return false;
        }
    }

    private static async Task RegisterFormatAssociation(string fileExtension, string executablePath)
    {
        // Modern Windows 10/11 approach using System Settings
        var uri = new Uri($"ms-settings:defaultapps-associationsfordevelopers");

        // For programmatic registration in enterprise scenarios
        var registryKey = $@"SOFTWARE\Classes\{fileExtension}";
        using var key = Registry.CurrentUser.CreateSubKey(registryKey);
        key.SetValue("", "Tunqio.AudioFile");

        var shellKey = Registry.CurrentUser.CreateSubKey($@"{registryKey}\shell\open\command");
        shellKey.SetValue("", $"\"{executablePath}\" \"%1\"");
    }

    // Handle file activation when app is launched via file association
    public static void HandleFileActivation(string[] args)
    {
        if (args?.Length > 0)
        {
            var filePath = args[0];
            if (File.Exists(filePath) && IsSupportedAudioFile(filePath))
            {
                // Queue file for playback
                App.Current.MainWindow.ViewModel.LoadAndPlayFile(filePath);
            }
        }
    }
}
```

### Context Menu Integration

```csharp
public class ContextMenuIntegration
{
    public static void RegisterShellExtension()
    {
        var supportedExtensions = new[] { ".mp3", ".flac", ".wav", ".aac", ".ogg" };

        foreach (var ext in supportedExtensions)
        {
            RegisterContextMenuForExtension(ext);
        }
    }

    private static void RegisterContextMenuForExtension(string extension)
    {
        var keyPath = $@"Software\Classes\{extension}\shell\Tunqio";

        using var shellKey = Registry.CurrentUser.CreateSubKey(keyPath);
        shellKey.SetValue("", "Play with Tunqio");
        shellKey.SetValue("Icon", $@"{AppDomain.CurrentDomain.BaseDirectory}Tunqio.exe,0");

        using var commandKey = Registry.CurrentUser.CreateSubKey($@"{keyPath}\command");
        commandKey.SetValue("", $@"""{AppDomain.CurrentDomain.BaseDirectory}Tunqio.exe"" ""%1""");

        // Add to "Play with" submenu
        var playWithKey = $@"Software\Classes\{extension}\shell\PlayWith\DropTarget";
        using var playKey = Registry.CurrentUser.CreateSubKey(playWithKey);
        playKey.SetValue("CLSID", "{C2FBB630-2971-11D1-A18C-00C04FD75D13}");
    }
}
```

## System Tray Integration

### As built (E7-S3)

The WinForms sample below is superseded by ADR-006 and kept only as the original sketch. What ships:

- **Package.** H.NotifyIcon.WinUI **2.3.2** (MIT), pinned in `Directory.Packages.props`. The 2.4 line ships a net10.0 build
  only, so 2.3.2 is the newest release a .NET 8 app can reference; it asks for Windows App SDK 1.6.250108002 or later, which
  the 1.8 pin satisfies. It brings H.NotifyIcon, H.GeneratedIcons.System.Drawing and System.Drawing.Common.
- **The icon.** `Tray/WinUiTrayIcon.cs`: a `TaskbarIcon` built in code on the XAML thread, `ContextMenuMode.PopupMenu`
  (the library turns the `MenuFlyout`'s items into a native popup menu each time it opens, so no second XAML window exists),
  and `ForceCreate(enablesEfficiencyMode: false)`, because the library's default puts the whole process into Windows'
  efficiency mode, which would throttle audio. Menu: Play or Pause, Next, Previous, Show Tunqio, Exit. Left-click shows the
  window.
- **The icon file.** `Tray/TrayIconFiles.cs` is the one place it is chosen, from docs/identity.md's names beside the
  executable: `Assets/Tray/tunqio-16.ico` and `Assets/Tray/tunqio-32.ico`, each with a `-light` and `-dark` variant named
  for the taskbar it is drawn on (`tunqio-32-light.ico` is for a light taskbar). At 100 % scaling (small icons 16 px) it tries
  `tunqio-16-<theme>.ico`, `tunqio-16.ico`, `tunqio-32-<theme>.ico`, `tunqio-32.ico`; above 100 % the 32 px pair first. The
  theme is read once at launch from `SystemUsesLightTheme` (read only). `Assets\**\*.ico` is a `Content` item, so files the
  icon task drops in reach both build shapes. Until one exists the tray shows the executable's own icon and the log says
  "using the executable's own icon as the fallback".
- **The rules.** `Tray/TrayController.cs`, behind `ITrayIcon`, unit tested over a fake (`TrayControllerTests`): menu choices
  go to `PlaybackSession.TogglePlayPauseAsync`, `NextAsync` and `PreviousAsync` (nothing before audio is up); the tooltip is
  `Identity.TrayTooltip`, `Tunqio` idle and `Title – Artist` with a track loaded, trimmed to 127 characters title first; the
  first item says Pause while playing and Play otherwise; the icon is written only when one of those changes.
- **Close and minimise.** `App.StartTray` hooks `AppWindow.Closing` and `AppWindow.Changed`. With `ui.closeToTray` on, a close
  by any means (the close button, Alt+F4, a UIA WindowPattern close) is cancelled and the window is hidden; with
  `ui.minimizeToTray` on, a minimise hides it. Both are off by default, read at the moment of the gesture, and ignored while
  the icon is not in the notification area, so Tunqio is never left running with no window and no icon. Playback belongs to
  the session and carries on.
- **Show.** Show from the tray and every activation E7-S1 routes (a second launch, a file, `tunqio://show`) go through
  `App.BringMainWindowToForeground`, which now calls `MainWindow.ReturnFromHidden` first: a window hidden to the tray is shown,
  and a window hidden behind the mini player gets the mini player closed, whose own Closed handler shows it. Then
  `Activate` and `SetForegroundWindow`, restoring a minimised window.
- **Exit.** Exit marks the next close as a real one and closes the main window, so it runs exactly the shutdown the close
  button runs with close-to-tray off (`App.OnWindowClosed`, T-188). The tray is its first step: the Closing and Changed
  handlers are unhooked and the controller disposed, which removes the icon (`TrayIcon.TryRemove`), before the media controls,
  audio (queue and position written), playlist exports, settings and the host.
- **Proof.** `tools/check-tray.ps1` on a scratch `--data-root` with both switches seeded on: a close through UIA leaves the
  process running, the window gone and the media session still Playing with its timeline moving; `tunqio://show` from a second
  launch brings the window back; a UIA minimise hides it and `tunqio://show` restores it; then the close switch is turned off
  in Settings through UIA and a close exits with code 0, and the log shows every step and the icon removed. The tray menu
  itself lives in Explorer's notification area and is not driven by the harness; how it looks is AC-493, for a person.

### System Tray Icon and Context Menu (original sketch, superseded)

```csharp
public class SystemTrayManager : IDisposable
{
    private NotifyIcon notifyIcon;
    private ContextMenuStrip contextMenu;
    private readonly MainWindowViewModel viewModel;

    public SystemTrayManager(MainWindowViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeSystemTray();
    }

    private void InitializeSystemTray()
    {
        // Create system tray icon
        notifyIcon = new NotifyIcon
        {
            Icon = LoadIconFromResource("Assets/TrayIcon.ico"),
            Text = "Tunqio",
            Visible = true
        };

        // Create context menu
        contextMenu = new ContextMenuStrip();

        var playPauseItem = new ToolStripMenuItem();
        playPauseItem.Click += OnPlayPauseClick;

        var previousItem = new ToolStripMenuItem("Previous Track");
        previousItem.Click += (s, e) => viewModel.PlaybackController.PreviousCommand.Execute(null);

        var nextItem = new ToolStripMenuItem("Next Track");
        nextItem.Click += (s, e) => viewModel.PlaybackController.NextCommand.Execute(null);

        contextMenu.Items.AddRange(new ToolStripItem[]
        {
            playPauseItem,
            new ToolStripSeparator(),
            previousItem,
            nextItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Show Window", null, OnShowWindow),
            new ToolStripMenuItem("Exit", null, OnExit)
        });

        notifyIcon.ContextMenuStrip = contextMenu;
        notifyIcon.DoubleClick += OnTrayDoubleClick;

        // Update context menu based on playback state
        viewModel.PlaybackController.WhenAnyValue(x => x.IsPlaying)
            .Subscribe(isPlaying =>
            {
                playPauseItem.Text = isPlaying ? "Pause" : "Play";
                playPauseItem.Image = isPlaying ? Properties.Resources.PauseIcon : Properties.Resources.PlayIcon;
            });
    }

    // Rich tooltip with current track information
    public void UpdateTooltip(Track currentTrack)
    {
        if (currentTrack != null)
        {
            notifyIcon.Text = $"{currentTrack.Title} – {currentTrack.Artist}";
        }
        else
        {
            notifyIcon.Text = "Tunqio";
        }
    }

    // Balloon notifications for track changes
    public void ShowTrackChangeNotification(Track newTrack)
    {
        if (Properties.Settings.Default.ShowNotifications)
        {
            notifyIcon.ShowBalloonTip(3000,
                "Now Playing",
                $"{newTrack.Title}\nby {newTrack.Artist}",
                ToolTipIcon.Info);
        }
    }

    // Handle minimize to tray
    private void OnShowWindow(object sender, EventArgs e)
    {
        Application.Current.MainWindow.Show();
        Application.Current.MainWindow.WindowState = WindowState.Normal;
        Application.Current.MainWindow.Activate();
    }

    private void OnTrayDoubleClick(object sender, EventArgs e)
    {
        OnShowWindow(sender, e);
    }

    public void Dispose()
    {
        notifyIcon?.Dispose();
        contextMenu?.Dispose();
    }
}
```

### Windows 11 Toast Notifications

```csharp
public class ToastNotificationManager
{
    // Packaged apps need no AUMID constant: Windows derives it from the package identity (see identity.md).
    private const string APP_ID = "Tunqio";

    public static void Initialize()
    {
        // Register COM server for toast activation
        DesktopNotificationManagerCompat.RegisterAumidAndComServer<ToastActivator>(APP_ID);
        DesktopNotificationManagerCompat.RegisterActivator<ToastActivator>();
    }

    public static void ShowNowPlayingToast(Track track)
    {
        var toastContent = new ToastContentBuilder()
            .AddAppLogoOverride(track.AlbumArtUri, ToastGenericAppLogo.Circle)
            .AddText("Now Playing")
            .AddText(track.Title)
            .AddText($"by {track.Artist}")
            .AddButton(new ToastButton()
                .SetContent("Previous")
                .AddArgument("action", "previous"))
            .AddButton(new ToastButton()
                .SetContent("Play/Pause")
                .AddArgument("action", "playpause"))
            .AddButton(new ToastButton()
                .SetContent("Next")
                .AddArgument("action", "next"))
            .SetToastScenario(ToastScenario.IncomingCall)
            .GetToastContent();

        var toast = new ToastNotification(toastContent.GetXml())
        {
            ExpirationTime = DateTime.Now.AddSeconds(5)
        };

        DesktopNotificationManagerCompat.CreateToastNotifier().Show(toast);
    }

    public static void ShowVolumeToast(int volumeLevel)
    {
        var toastContent = new ToastContentBuilder()
            .AddText("Volume")
            .AddText($"{volumeLevel}%")
            .AddProgressBar(new ToastProgressBar()
            {
                Value = volumeLevel / 100.0,
                Status = $"{volumeLevel}%"
            })
            .SetToastScenario(ToastScenario.Reminder)
            .GetToastContent();

        var toast = new ToastNotification(toastContent.GetXml())
        {
            ExpirationTime = DateTime.Now.AddSeconds(2)
        };

        DesktopNotificationManagerCompat.CreateToastNotifier().Show(toast);
    }
}

// Handle toast activation
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[ComSourceInterfaces(typeof(INotificationActivationCallback))]
[Guid("23A5B06E-20BB-4E7E-A0AC-6982ED6A6041")]
public class ToastActivator : NotificationActivator
{
    public override void OnActivated(string invokedArgs, NotificationUserInput userInput, string appUserModelId)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var args = ToastArguments.Parse(invokedArgs);

            switch (args["action"])
            {
                case "previous":
                    App.Current.MainWindow.ViewModel.PlaybackController.PreviousCommand.Execute(null);
                    break;
                case "playpause":
                    App.Current.MainWindow.ViewModel.PlaybackController.PlayPauseCommand.Execute(null);
                    break;
                case "next":
                    App.Current.MainWindow.ViewModel.PlaybackController.NextCommand.Execute(null);
                    break;
            }

            // Show main window if hidden
            if (App.Current.MainWindow.WindowState == WindowState.Minimized)
            {
                App.Current.MainWindow.Show();
                App.Current.MainWindow.WindowState = WindowState.Normal;
            }
        });
    }
}
```

## Media Key Support

> **Superseded (ADR-006, E7-S2).** Media keys reach Tunqio only through SMTC, below: Windows routes the hardware
> Play/Pause, Next, Previous and Stop keys to the app's media session whether or not the window has focus. There is no
> `WM_APPCOMMAND` hook, and the shell's shortcut table cannot hold a media key (see "As built (E7-S2)").

### Global Media Key Handler

```csharp
public class MediaKeyHandler
{
    private const int WM_APPCOMMAND = 0x0319;
    private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
    private const int APPCOMMAND_MEDIA_STOP = 13;
    private const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
    private const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
    private const int APPCOMMAND_VOLUME_UP = 10;
    private const int APPCOMMAND_VOLUME_DOWN = 9;

    private readonly HwndSource hwndSource;
    private readonly PlaybackControlViewModel playbackController;

    public MediaKeyHandler(Window window, PlaybackControlViewModel controller)
    {
        playbackController = controller;

        // Hook into window message processing
        var helper = new WindowInteropHelper(window);
        hwndSource = HwndSource.FromHwnd(helper.Handle);
        hwndSource.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APPCOMMAND)
        {
            int cmd = GET_APPCOMMAND_LPARAM(lParam);
            HandleMediaCommand(cmd);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void HandleMediaCommand(int command)
    {
        switch (command)
        {
            case APPCOMMAND_MEDIA_PLAY_PAUSE:
                playbackController.PlayPauseCommand.Execute(null);
                break;

            case APPCOMMAND_MEDIA_STOP:
                playbackController.StopCommand.Execute(null);
                break;

            case APPCOMMAND_MEDIA_NEXTTRACK:
                playbackController.NextCommand.Execute(null);
                break;

            case APPCOMMAND_MEDIA_PREVIOUSTRACK:
                playbackController.PreviousCommand.Execute(null);
                break;

            case APPCOMMAND_VOLUME_UP:
                playbackController.IncreaseVolumeCommand.Execute(null);
                ToastNotificationManager.ShowVolumeToast(playbackController.Volume);
                break;

            case APPCOMMAND_VOLUME_DOWN:
                playbackController.DecreaseVolumeCommand.Execute(null);
                ToastNotificationManager.ShowVolumeToast(playbackController.Volume);
                break;
        }
    }

    private static int GET_APPCOMMAND_LPARAM(IntPtr lParam)
    {
        return (short)((lParam.ToInt32() >> 16) & 0xFFFF);
    }

    public void Dispose()
    {
        hwndSource?.RemoveHook(WndProc);
    }
}
```

### System Media Transport Controls (SMTC)

**As built (E7-S2).** The sample below is the original design and does not run in a desktop WinUI 3 app:
`GetForCurrentView` needs a `CoreWindow`, which a desktop window does not have. What ships:

- **Route.** `SystemMediaTransportControlsInterop.GetForWindow(hwnd)` on the main window's handle, in
  `WindowsMediaControls` (Tunqio.App/Playback), built in `App.OnLaunched` as soon as the window has a handle. The
  alternative, a `Windows.Media.Playback.MediaPlayer`'s controls through its `CommandManager`, belongs to a player
  instance that plays the audio; Tunqio's audio is mpcore's, so it would mean an idle `MediaPlayer` kept alive only to
  borrow its session. `GetForWindow` needs no package identity and was measured working unpackaged by
  `tools/check-smtc.ps1`. Unpackaged, the session's source app id is the executable's name; packaged it is the AUMID.
- **`SmtcBridge`** follows `PlaybackSession.Snapshots` (10 Hz) and writes to `ISystemMediaControls`, the small face over
  the WinRT object that the unit tests fake (`SmtcBridgeTests`). Everything is written on a change. Status is Playing,
  Paused, Stopped (nothing loaded but the queue has an item) or Closed (nothing queued, which takes the session out of
  the flyout). Music properties (title, artists, album artist, album, track number) and the thumbnail, the art cache's
  96 px file opened as a `StorageFile`, are written once per track; a transient file dropped from outside the library
  (D-24) has no art hash and gets no thumbnail. The timeline (start 0, end and max seek at the duration) is written on
  a track change, a state change, a seek (the position more than 2 s from where the last write puts it), and every 5 s
  while playing; the flyout moves the bar itself between writes.
- **Buttons.** Play, Pause, Stop, Next and Previous, and `PlaybackPositionChangeRequested`, arrive on a Windows thread;
  the bridge hands each to the pool and runs them one at a time through the session's own commands
  (`TogglePlayPauseAsync` only when the state needs it, so Play on a playing session does nothing, `NextAsync`,
  `PreviousAsync`, `StopAsync`, `SeekAsync` clamped to the track). Next is enabled while `PlayQueue.Advance(manual:
  true)` would move on (a later item, or repeat); Previous, Pause and Stop while a track is loaded; Play while
  something is loaded or queued.
- **Media keys are handled once.** The shell handles no media virtual key itself, and `KeyChord` refuses VK 173 to 183
  (volume, media and launch keys) in both capture and `settings.json`, so Settings › Shortcuts cannot bind one and have
  a press toggled by SMTC and toggled back by the shell.
- **Lifetime.** Disabled until the session exists; at shutdown the bridge closes the media session before
  `AudioStartup` disposes the session, so a late flyout press cannot reach a session being torn down. A machine that
  refuses a media session logs an error and runs without it.
- **Live check.** `tools/check-smtc.ps1` launches the Release build on a scratch `--data-root`, adds an album through the
  first-run welcome (three 90 s tagged FLAC tones it generates with ffmpeg, since the committed fixtures are 1 s each
  and an album of them ends before a timeline can be read twice), starts it through UIA with the output muted, and then reads and presses Tunqio's
  session from outside the process through `GlobalSystemMediaTransportControlsSessionManager`, matched by source app
  id so a browser's session is never touched: title, artist, album, track number, thumbnail, Playing, a timeline that
  moves, and `TryPauseAsync`, `TryPlayAsync`, `TrySkipNextAsync` and `TryChangePlaybackPositionAsync` each changing the
  app. That is the path the flyout and the hardware keys use, without pressing a key.

```csharp
public class SystemMediaTransportManager
{
    private readonly SystemMediaTransportControls smtc;
    private readonly PlaybackControlViewModel playbackController;

    public SystemMediaTransportManager(PlaybackControlViewModel controller)
    {
        playbackController = controller;

        // Get SMTC for the current app
        smtc = SystemMediaTransportControls.GetForCurrentView();

        // Enable the desired buttons
        smtc.IsEnabled = true;
        smtc.IsPlayEnabled = true;
        smtc.IsPauseEnabled = true;
        smtc.IsStopEnabled = true;
        smtc.IsNextEnabled = true;
        smtc.IsPreviousEnabled = true;

        // Handle button pressed events
        smtc.ButtonPressed += OnButtonPressed;

        // Update display info when track changes
        playbackController.WhenAnyValue(x => x.CurrentTrack)
            .Where(track => track != null)
            .Subscribe(UpdateDisplayInfo);

        // Update playback status
        playbackController.WhenAnyValue(x => x.IsPlaying)
            .Subscribe(isPlaying =>
            {
                smtc.PlaybackStatus = isPlaying
                    ? MediaPlaybackStatus.Playing
                    : MediaPlaybackStatus.Paused;
            });
    }

    private void OnButtonPressed(SystemMediaTransportControls sender,
                               SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                playbackController.PlayCommand.Execute(null);
                break;

            case SystemMediaTransportControlsButton.Pause:
                playbackController.PauseCommand.Execute(null);
                break;

            case SystemMediaTransportControlsButton.Stop:
                playbackController.StopCommand.Execute(null);
                break;

            case SystemMediaTransportControlsButton.Next:
                playbackController.NextCommand.Execute(null);
                break;

            case SystemMediaTransportControlsButton.Previous:
                playbackController.PreviousCommand.Execute(null);
                break;
        }
    }

    private async void UpdateDisplayInfo(Track track)
    {
        var updater = smtc.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;

        updater.MusicProperties.Title = track.Title;
        updater.MusicProperties.Artist = track.Artist;
        updater.MusicProperties.AlbumTitle = track.Album;

        // Set thumbnail if available
        if (!string.IsNullOrEmpty(track.AlbumArtPath))
        {
            var thumbnail = await GetThumbnailFromPath(track.AlbumArtPath);
            updater.Thumbnail = RandomAccessStreamReference.CreateFromStream(thumbnail);
        }

        updater.Update();
    }

    private async Task<IRandomAccessStream> GetThumbnailFromPath(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        return await file.OpenAsync(FileAccessMode.Read);
    }
}
```

## Jump List Integration

### Windows 11 Jump List Implementation

```csharp
public class JumpListManager
{
    public static async Task UpdateJumpList(IEnumerable<Track> recentTracks,
                                          IEnumerable<Playlist> pinnedPlaylists)
    {
        try
        {
            var jumpList = await JumpList.LoadCurrentAsync();
            jumpList.Items.Clear();

            // Add recent tracks
            foreach (var track in recentTracks.Take(10))
            {
                var jumpListItem = JumpListItem.CreateWithArguments(
                    $"track:{track.FilePath}",
                    $"{track.Title} - {track.Artist}");

                jumpListItem.Description = $"Play {track.Title}";
                jumpListItem.GroupName = "Recent Tracks";
                jumpListItem.Logo = new Uri("ms-appx:///Assets/MusicNote.png");

                jumpList.Items.Add(jumpListItem);
            }

            // Add pinned playlists
            foreach (var playlist in pinnedPlaylists.Take(5))
            {
                var jumpListItem = JumpListItem.CreateWithArguments(
                    $"playlist:{playlist.Id}",
                    playlist.Name);

                jumpListItem.Description = $"Play {playlist.Name}";
                jumpListItem.GroupName = "Playlists";
                jumpListItem.Logo = new Uri("ms-appx:///Assets/Playlist.png");

                jumpList.Items.Add(jumpListItem);
            }

            await jumpList.SaveAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to update jump list: {ex.Message}");
        }
    }

    public static void HandleJumpListActivation(string arguments)
    {
        if (arguments.StartsWith("track:"))
        {
            var filePath = arguments.Substring(6);
            App.Current.MainWindow.ViewModel.LoadAndPlayFile(filePath);
        }
        else if (arguments.StartsWith("playlist:"))
        {
            var playlistId = arguments.Substring(9);
            App.Current.MainWindow.ViewModel.LoadPlaylist(playlistId);
        }
    }
}
```

## Windows 11 Specific Features

### Acrylic and Mica Background Effects

```csharp
public class WindowEffectsManager
{
    public static void EnableMicaEffect(Window window)
    {
        if (IsWindows11OrGreater())
        {
            var windowHelper = new WindowInteropHelper(window);
            var hwnd = windowHelper.Handle;

            // Enable Mica backdrop
            var backdropType = 2; // DWMSBT_MAINWINDOW
            DwmSetWindowAttribute(hwnd, 38, // DWMWA_SYSTEMBACKDROP_TYPE
                                ref backdropType, sizeof(int));

            // Extend frame into client area for better integration
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
        }
    }

    public static void EnableAcrylicEffect(UIElement element, double opacity = 0.8)
    {
        if (ApiInformation.IsTypePresent("Windows.UI.Xaml.Media.AcrylicBrush"))
        {
            var acrylicBrush = new AcrylicBrush
            {
                BackgroundSource = AcrylicBackgroundSource.HostBackdrop,
                TintColor = ((SolidColorBrush)Application.Current.Resources["SystemAccentColor"]).Color,
                TintOpacity = opacity,
                FallbackColor = Colors.DarkGray
            };

            if (element is Panel panel)
                panel.Background = acrylicBrush;
            else if (element is Control control)
                control.Background = acrylicBrush;
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }
}
```

### Windows Hello Authentication

```csharp
public class WindowsHelloAuth
{
    public static async Task<bool> IsWindowsHelloAvailable()
    {
        return await UserConsentVerifier.CheckAvailabilityAsync() == UserConsentVerifierAvailability.Available;
    }

    public static async Task<bool> AuthenticateUser(string reason)
    {
        try
        {
            var result = await UserConsentVerifier.RequestVerificationAsync(reason);
            return result == UserConsentVerificationResult.Verified;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Windows Hello authentication failed: {ex.Message}");
            return false;
        }
    }

    // Use for accessing premium features or private playlists
    public static async Task<bool> AuthorizeForPremiumFeatures()
    {
        if (await IsWindowsHelloAvailable())
        {
            return await AuthenticateUser("Access premium features and private playlists");
        }

        // Fallback to PIN or password
        return await ShowFallbackAuthDialog();
    }

    private static async Task<bool> ShowFallbackAuthDialog()
    {
        var dialog = new ContentDialog
        {
            Title = "Authentication Required",
            Content = "Please enter your PIN to continue:",
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel"
        };

        // Add PIN input field
        var pinBox = new PasswordBox();
        dialog.Content = pinBox;

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary && !string.IsNullOrEmpty(pinBox.Password);
    }
}
```

This Windows integration architecture provides comprehensive platform-specific functionality that enhances the user experience while maintaining compatibility with Windows 10 and optimizing for Windows 11 features.