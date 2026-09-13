using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;

namespace Tunqio.App.Library;

/// <summary>Code-behind for Settings › Library; see the XAML for the design notes.</summary>
public sealed partial class LibrarySettingsPage : Page, ILibraryRefreshable
{
    public LibrarySettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<LibrarySettingsViewModel>();
        InitializeComponent();
    }

    public LibrarySettingsViewModel ViewModel { get; }

    public static bool Not(bool value) => !value;

    public static bool Neither(bool a, bool b) => !a && !b;

    public static Visibility NeitherVisible(bool a, bool b) => a || b ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>"Purge missing" with the count it would remove.</summary>
    public static string PurgeLabel(int missing) => missing > 0
        ? "Purge " + missing.ToString("N0", CultureInfo.CurrentCulture) + " missing"
        : "Purge missing";

    public void RefreshLibrary() => ViewModel.LoadAsync().Forget("Library settings refresh");

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Attach();
        ViewModel.LoadAsync().Forget("Library settings load");
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Detach();
    }

    private void OnAddFolder(object sender, RoutedEventArgs e) => AddFolderAsync().Forget("Add library folder");

    private async Task AddFolderAsync()
    {
        AddFolderButton.IsEnabled = false; // the picker is modal to the window, but a second click queues a second picker
        try
        {
            await ViewModel.AddFolderAsync();
        }
        finally
        {
            AddFolderButton.IsEnabled = true;
        }
    }

    private void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LibraryFolderRow row)
        {
            RemoveFolderAsync(row).Forget("Remove library folder");
        }
    }

    private async Task RemoveFolderAsync(LibraryFolderRow row)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Remove " + row.Name + "?",
            Content = "The folder's tracks leave the library with their play history and playlist entries. The files are not touched. Disable the folder instead to keep the tracks without scanning it.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await ViewModel.RemoveFolderAsync(row);
        }
    }

    private void OnFolderToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: LibraryFolderRow row } toggle)
        {
            ViewModel.SetEnabledAsync(row, toggle.IsOn).Forget("Enable library folder");
        }
    }

    private void OnRescanFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LibraryFolderRow row)
        {
            ViewModel.RescanFolderAsync(row).Forget("Rescan library folder");
        }
    }

    private void OnRescanAll(object sender, RoutedEventArgs e) => ViewModel.RescanAllAsync().Forget("Rescan library");

    private void OnCancelScan(object sender, RoutedEventArgs e) => ViewModel.CancelScan();

    private void OnPurgeMissing(object sender, RoutedEventArgs e) => ViewModel.PurgeMissingAsync().Forget("Purge missing tracks");

    private void OnRebuildIndex(object sender, RoutedEventArgs e) => ViewModel.RebuildIndexAsync().Forget("Rebuild search index");

    private void OnRegenerateArt(object sender, RoutedEventArgs e) => ViewModel.RegenerateArtAsync().Forget("Regenerate art");

    private void OnImportExports(object sender, RoutedEventArgs e) => ViewModel.ImportExportsAsync().Forget("Import playlists from exports");

    private void OnImportPlaylistFiles(object sender, RoutedEventArgs e) => ViewModel.ImportFilesAsync().Forget("Import playlist files");

    private void OnExportPlaylists(object sender, RoutedEventArgs e) => ViewModel.ExportPlaylistsAsync().Forget("Export playlists");
}
