using Tunqio.Core.Library;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Tunqio.App.Library;

/// <summary>The pickers M3U8 export and import need (E6-S2). An interface because a picker cannot open in a test host.</summary>
public interface IPlaylistFilePicker
{
    /// <summary>Where to save a playlist, suggesting <paramref name="suggestedFileName"/>; null when cancelled.</summary>
    Task<string?> PickSaveFileAsync(string suggestedFileName, CancellationToken ct = default);

    /// <summary>M3U8 or M3U files to import; empty when cancelled.</summary>
    Task<IReadOnlyList<string>> PickOpenFilesAsync(CancellationToken ct = default);
}

/// <summary><see cref="IPlaylistFilePicker"/> over the system pickers, parented to the main window.</summary>
public sealed class WinUiPlaylistFilePicker : IPlaylistFilePicker
{
    public async Task<string?> PickSaveFileAsync(string suggestedFileName, CancellationToken ct = default)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName),
            DefaultFileExtension = M3u8.Extension,
        };
        picker.FileTypeChoices.Add("M3U8 playlist", [M3u8.Extension]);
        // A WinUI 3 desktop app has no CoreWindow; the picker needs the owner HWND (WinRT.Interop).
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        StorageFile? file = await picker.PickSaveFileAsync().AsTask(ct);
        return file?.Path is { Length: > 0 } path ? path : null;
    }

    public async Task<IReadOnlyList<string>> PickOpenFilesAsync(CancellationToken ct = default)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add(M3u8.Extension);
        picker.FileTypeFilter.Add(".m3u");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync().AsTask(ct);
        return [.. files.Select(f => f.Path).Where(p => p.Length > 0)];
    }
}
