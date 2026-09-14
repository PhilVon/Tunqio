using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Tunqio.App.Shell;

/// <summary>Code-behind for Settings › About &amp; Diagnostics; see the XAML for the design notes.</summary>
public sealed partial class AboutSettingsPage : Page
{
    private SettingsOverlay? _overlay;
    private bool _loaded;
    private bool _overlayVisible = true;

    public AboutSettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<AboutSettingsViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public AboutSettingsViewModel ViewModel { get; }

    /// <summary>
    /// The licences are read on the first arrival rather than in the constructor, which DI runs when the overlay
    /// first asks for the page; the folder does not change while the app runs, so once is enough.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (ViewModel.Licences.Count == 0)
        {
            ViewModel.LoadLicences();
        }
    }

    /// <summary>
    /// The readout runs while the page is in the tree AND the overlay is visible. Leaving the section unloads the
    /// page (the frame swaps its content); closing the overlay does not, it only collapses an ancestor, so the
    /// overlay's own visibility is watched too.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        _overlay = FindOverlay();
        if (_overlay is not null)
        {
            _overlayVisible = _overlay.Visibility == Visibility.Visible;
            _overlay.VisibleChanged += OnOverlayVisibleChanged;
        }

        UpdateReadout();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        if (_overlay is not null)
        {
            _overlay.VisibleChanged -= OnOverlayVisibleChanged;
            _overlay = null;
        }

        UpdateReadout();
    }

    private void OnOverlayVisibleChanged(object? sender, bool visible)
    {
        _overlayVisible = visible;
        UpdateReadout();
    }

    private void UpdateReadout() => ViewModel.IsReadoutActive = _loaded && _overlayVisible;

    private SettingsOverlay? FindOverlay()
    {
        DependencyObject? node = this;
        while (node is not null)
        {
            if (node is SettingsOverlay overlay)
            {
                return overlay;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.ClearNotice();

    private void OnOpenLogsFolder(object sender, RoutedEventArgs e) =>
        OpenFolderAsync(ViewModel.LogsDirectory, "logs").Forget("Open the logs folder");

    private void OnOpenLicencesFolder(object sender, RoutedEventArgs e) =>
        OpenFolderAsync(ViewModel.LicencesDirectory, "licences").Forget("Open the licences folder");

    /// <summary>Opens <paramref name="path"/> in Explorer, creating the logs folder first if a fresh profile has none yet.</summary>
    private async Task OpenFolderAsync(string path, string what)
    {
        try
        {
            Directory.CreateDirectory(path);
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(path);
            await Windows.System.Launcher.LaunchFolderAsync(folder);
            Serilog.Log.Information("Opened the {What} folder {Path}", what, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Serilog.Log.Warning(ex, "The {What} folder {Path} could not be opened", what, path);
            ViewModel.SetNotice("The " + what + " folder could not be opened: " + ex.Message, error: true);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e) => ExportAsync().Forget("Export diagnostics");

    /// <summary>
    /// The save picker, owned by the main window (a WinUI 3 desktop app has no CoreWindow to own it), then the view
    /// model's export. Cancelling the picker does nothing and says nothing.
    /// </summary>
    private async Task ExportAsync()
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(ViewModel.SuggestedExportName),
            DefaultFileExtension = ".zip",
        };
        picker.FileTypeChoices.Add("Zip archive", [".zip"]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file?.Path is not { Length: > 0 } path)
        {
            return;
        }

        await ViewModel.ExportAsync(path);
    }
}
