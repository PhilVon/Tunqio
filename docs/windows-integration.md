# Windows Platform Integration

> **Status: foundation document, partially superseded.** WinForms NotifyIcon, WPF HwndSource/WindowInteropHelper, WM_APPCOMMAND hooks, DesktopNotificationManagerCompat and direct registry writes are replaced by SMTC, H.NotifyIcon.WinUI, Windows App SDK AppNotifications and manifest-declared associations (ADR-006). Windows Hello and premium features are removed (ADR-008). The feature list itself stands. All product names, identifiers and title/tooltip formats come from [identity.md](identity.md). See [decisions.md](decisions.md) for the record and [README.md](README.md) for the current reading order.

This document details Windows-specific integration features that provide seamless desktop experience including file associations, system tray functionality, media key support, and modern Windows 11 features.

## File Association Management

### Audio Format Registration

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

### System Tray Icon and Context Menu

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