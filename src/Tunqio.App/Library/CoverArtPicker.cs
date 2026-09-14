using Tunqio.Core.Library;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Tunqio.App.Library;

/// <summary>The image picker the tag editor's Replace art needs (T-113). An interface because a picker cannot open in a test host.</summary>
public interface ICoverArtPicker
{
    /// <summary>A JPEG or PNG the user chose, read into memory as a front cover; null when cancelled.</summary>
    Task<EmbeddedPicture?> PickAsync(CancellationToken ct = default);
}

/// <summary><see cref="ICoverArtPicker"/> over the system open picker, parented to the main window.</summary>
public sealed class WinUiCoverArtPicker : ICoverArtPicker
{
    public async Task<EmbeddedPicture?> PickAsync(CancellationToken ct = default)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail,
        };
        // The two formats every container and every other player agree on; the art cache renders either.
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        // A WinUI 3 desktop app has no CoreWindow; the picker needs the owner HWND (WinRT.Interop).
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindowHandle);
        StorageFile? file = await picker.PickSingleFileAsync().AsTask(ct);
        if (file?.Path is not { Length: > 0 } path)
        {
            return null;
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, ct);
        return new EmbeddedPicture(bytes, MimeTypeFor(path), PictureKind.FrontCover);
    }

    /// <summary>By extension, which is what the filter above allowed; the tag carries it for other readers.</summary>
    internal static string MimeTypeFor(string path) =>
        Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
}
