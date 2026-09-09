using Windows.Storage;
using Windows.Storage.Pickers;

namespace Tunqio.App.Library;

/// <summary><see cref="ILibraryFolderPicker"/> over the system folder picker, parented to the main window.</summary>
public sealed class WinUiFolderPicker : ILibraryFolderPicker
{
    public async Task<string?> PickFolderAsync(CancellationToken ct = default)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add("*");
        // A WinUI 3 desktop app has no CoreWindow; the picker needs the owner HWND (WinRT.Interop).
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        StorageFolder? folder = await picker.PickSingleFolderAsync().AsTask(ct);
        return folder?.Path is { Length: > 0 } path ? path : null;
    }
}
