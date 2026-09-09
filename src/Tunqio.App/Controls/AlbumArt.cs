using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Tunqio.Core.Library;

namespace Tunqio.App.Controls;

/// <summary>
/// Album art for XAML (E3-S7): turns an <c>art_hash</c> into an <see cref="ImageSource"/> over the cache's file
/// for the size. A <see cref="BitmapImage"/> with a file URI decodes off the UI thread; when the file is not
/// there (the cache was cleared, or the hash is a placeholder from a synthetic database) the image fails
/// quietly and whatever sits under it, the <see cref="PlaceholderArt"/> tile, stays visible. No I/O happens
/// here, so the call is safe inside a recycled item template.
/// </summary>
public static class AlbumArt
{
    /// <summary>Set by the app once the host is up; <c>null</c> (no host, the spike modes) means placeholders only.</summary>
    public static IArtCache? Cache { get; set; }

    /// <summary>The 300 px grid tile.</summary>
    public static ImageSource? Tile(string? hash) => Source(hash, ArtSize.Tile);

    /// <summary>The 1000 px rendering for Now Playing and the album header.</summary>
    public static ImageSource? Large(string? hash) => Source(hash, ArtSize.Large);

    /// <summary>The 96 px rendering for list rows.</summary>
    public static ImageSource? Thumbnail(string? hash) => Source(hash, ArtSize.Thumbnail);

    private static BitmapImage? Source(string? hash, ArtSize size)
    {
        string? path = Cache?.PathFor(hash, size);
        return path is null ? null : new BitmapImage(new Uri(path));
    }
}
