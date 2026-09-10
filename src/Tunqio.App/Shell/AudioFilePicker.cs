using Tunqio.Core.Library;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Tunqio.App.Shell;

/// <summary>The two pickers E2-S4 owes: choose files, or choose a folder, to play.</summary>
/// <remarks>
/// An interface rather than the picker itself because a picker cannot be opened in a test host, and what the
/// shell does with the answer — the empty state, the notice, the queue — is worth testing without one.
/// </remarks>
public interface IAudioFilePicker
{
    /// <summary>Files to play, in the order the user chose them; empty when the picker was cancelled.</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken ct = default);

    /// <summary>A folder to play, or null when the picker was cancelled.</summary>
    Task<string?> PickFolderAsync(CancellationToken ct = default);
}

/// <summary>
/// <see cref="IAudioFilePicker"/> over the system pickers, parented to the main window. The file picker is
/// filtered to the formats the app can actually play (<see cref="AudioFormats.Extensions"/>) rather than to a
/// hand-written list, so a format added to the scanner is offered here without anyone remembering to.
/// </summary>
public sealed class WinUiAudioFilePicker : IAudioFilePicker
{
    public async Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken ct = default)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            ViewMode = PickerViewMode.List,
        };
        foreach (string extension in AudioFormats.Extensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        // A WinUI 3 desktop app has no CoreWindow; the picker needs the owner HWND (WinRT.Interop).
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync().AsTask(ct);
        return [.. files.Select(f => f.Path).Where(p => p.Length > 0)];
    }

    public async Task<string?> PickFolderAsync(CancellationToken ct = default)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.MusicLibrary,
            ViewMode = PickerViewMode.List,
        };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        StorageFolder? folder = await picker.PickSingleFolderAsync().AsTask(ct);
        return folder?.Path is { Length: > 0 } path ? path : null;
    }
}
