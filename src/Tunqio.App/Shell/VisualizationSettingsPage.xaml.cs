using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;
using Tunqio.App.Library;

namespace Tunqio.App.Shell;

/// <summary>Code-behind for Settings › Visualization; see the XAML for the design notes.</summary>
public sealed partial class VisualizationSettingsPage : Page
{
    public VisualizationSettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<VisualizationSettingsViewModel>();
        InitializeComponent();
    }

    public VisualizationSettingsViewModel ViewModel { get; }

    /// <summary>Collapsed while <paramref name="condition"/> holds.</summary>
    public static Visibility Unless(bool condition) => condition ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>An error notice is an error bar; everything else is a report.</summary>
    public static InfoBarSeverity SeverityOf(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Informational;

    /// <summary>
    /// The catalogue is read on every arrival rather than once in the constructor: the page is cached
    /// (<c>NavigationCacheMode="Enabled"</c>), and the renderer may have come up - or gone - since it was last
    /// on screen.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Load();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => ViewModel.Refresh();

    private void OnReset(object sender, RoutedEventArgs e) => ViewModel.ResetParameters();

    private void OnNoticeClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.ClearNotice();

    private void OnOpenLibrarySettings(object sender, RoutedEventArgs e) =>
        Frame?.Navigate(typeof(LibrarySettingsPage));

    /// <summary>
    /// Opens the user preset directory in Explorer, creating it first: "drop a preset in here" is not an
    /// instruction anyone can follow when the folder is only named and not reachable.
    /// </summary>
    private void OnOpenPresetFolder(object sender, RoutedEventArgs e) =>
        OpenPresetFolderAsync().Forget("Open the preset folder");

    private async Task OpenPresetFolderAsync()
    {
        try
        {
            Directory.CreateDirectory(ViewModel.UserPresetDirectory);
            Windows.Storage.StorageFolder folder =
                await Windows.Storage.StorageFolder.GetFolderFromPathAsync(ViewModel.UserPresetDirectory);
            await Windows.System.Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Serilog.Log.Warning(ex, "The preset folder {Path} could not be opened", ViewModel.UserPresetDirectory);
        }
    }
}
