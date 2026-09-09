using System.Globalization;

namespace Tunqio.Core.Library;

/// <summary>Art hashes for one scanned file, as the art cache assigns them (<c>track.art_hash</c> and <c>album.art_hash</c>).</summary>
/// <param name="TrackArtHash">The embedded picture's hash, or <c>null</c> when the file carries none.</param>
/// <param name="AlbumArtHash">The embedded picture's hash when there is one, else the folder image's, else <c>null</c>; the album row takes the first track's.</param>
public sealed record ArtHashes(string? TrackArtHash, string? AlbumArtHash)
{
    public static ArtHashes None { get; } = new(null, null);
}

/// <summary>The rendered sizes of one cached image (docs/library-and-data.md "Storage layout"); the value is the long edge in pixels.</summary>
public enum ArtSize
{
    /// <summary><c>1000.jpg</c>: Now Playing and the album header.</summary>
    Large = 1000,

    /// <summary><c>300.jpg</c>: the grid tile.</summary>
    Tile = 300,

    /// <summary><c>96.jpg</c>: list rows and the SMTC thumbnail.</summary>
    Thumbnail = 96,
}

/// <summary>One palette entry: an sRGB colour, the share of the image's pixels it stands for, and its relative luminance.</summary>
/// <param name="Population">0..1, the fraction of pixels quantised into this colour's box; the five sum to 1 for a real image, and a filled slot has 0.</param>
/// <param name="Luminance">Relative luminance (WCAG, linearised sRGB, 0 black .. 1 white).</param>
public sealed record PaletteColor(byte R, byte G, byte B, double Population, double Luminance)
{
    /// <summary><c>#rrggbb</c>.</summary>
    public string Hex => string.Create(CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}");

    /// <summary>The WCAG relative luminance of an sRGB colour.</summary>
    public static double RelativeLuminance(byte r, byte g, byte b)
    {
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);

        static double Linear(byte channel)
        {
            double c = channel / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }
}

/// <summary>
/// The five dominant colours of an image (median cut over the 300 px rendering), most populous first. Stored as
/// <c>palette.json</c> beside the sizes and read by audio-reactive theming (E4-S6) and the "accent source = art"
/// setting. An image with fewer than five distinct colours has its remaining slots filled with darker shades
/// of the dominant colour (population 0) so consumers can index all five.
/// </summary>
public sealed record ArtPalette(IReadOnlyList<PaletteColor> Colors)
{
    public const int Size = 5;

    /// <summary>The most populous colour.</summary>
    public PaletteColor Dominant => Colors[0];

    /// <summary>The first colour whose luminance is under <paramref name="maxLuminance"/>, else the darkest.</summary>
    public PaletteColor Darkest(double maxLuminance = 0.25) =>
        Colors.FirstOrDefault(c => c.Population > 0 && c.Luminance <= maxLuminance) ?? Colors.MinBy(c => c.Luminance)!;

    /// <summary>The first colour whose luminance is over <paramref name="minLuminance"/>, else the lightest.</summary>
    public PaletteColor Lightest(double minLuminance = 0.5) =>
        Colors.FirstOrDefault(c => c.Population > 0 && c.Luminance >= minLuminance) ?? Colors.MaxBy(c => c.Luminance)!;
}

/// <summary>
/// The album art cache (E3-S7; docs/library-and-data.md "Storage layout" and the scanner's ExtractArt stage).
/// Images are keyed by the SHA-256 of their source bytes and rendered once into the three <see cref="ArtSize"/>s
/// plus a <see cref="ArtPalette"/>; the same picture embedded in ten tracks costs one decode. Reads are by hash:
/// the repositories hand views <c>art_hash</c>, views ask for a path or the palette.
/// </summary>
public interface IArtCache
{
    /// <summary>
    /// The scanner's ExtractArt stage: stores the embedded picture the tag reader found, else the folder image
    /// beside the audio file (<c>cover.*</c>, <c>folder.*</c>, <c>front.*</c>, then any <c>.jpg</c>), and returns
    /// the hashes to record. Never throws for a bad image: an undecodable one gives <see cref="ArtHashes.None"/>.
    /// </summary>
    Task<ArtHashes> StoreAsync(EmbeddedPicture? picture, string audioPath, CancellationToken ct = default);

    /// <summary>
    /// The file holding <paramref name="size"/> of the image <paramref name="hash"/>. No I/O: the path is where
    /// the file is when the cache has the image, so a consumer that loads it treats a failure as "no art"
    /// (the cache may have been cleared since the row was written). <c>null</c> for a value that is not a hash.
    /// </summary>
    string? PathFor(string? hash, ArtSize size);

    /// <summary>The palette stored with the image, or <c>null</c> when the cache does not have it.</summary>
    Task<ArtPalette?> LoadPaletteAsync(string? hash, CancellationToken ct = default);

    /// <summary>Deletes every cached image (Settings &gt; Library &gt; Regenerate art; a forced rescan fills it again).</summary>
    Task ClearAsync(CancellationToken ct = default);
}
