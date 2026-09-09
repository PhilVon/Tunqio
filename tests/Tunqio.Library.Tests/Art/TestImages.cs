using Tunqio.FixtureGen;
using Tunqio.Library.Art;

namespace Tunqio.Library.Tests.Art;

/// <summary>Deterministic source images for the art tests, and a reader for what the cache wrote.</summary>
internal static class TestImages
{
    /// <summary>Four flat quadrants (red, green, blue, white) over a <paramref name="width"/> x <paramref name="height"/> PNG.</summary>
    public static byte[] Quadrants(int width, int height) => ArtGenerator.Png(width, height, (x, y) =>
        x < width / 2
            ? (y < height / 2 ? ((byte)220, (byte)30, (byte)30) : ((byte)30, (byte)30, (byte)220))
            : (y < height / 2 ? ((byte)30, (byte)200, (byte)30) : ((byte)250, (byte)250, (byte)250)));

    /// <summary>A smooth two-axis gradient: hundreds of distinct colours, no flat areas.</summary>
    public static byte[] Gradient(int width, int height) => ArtGenerator.Png(width, height, (x, y) =>
        ((byte)(x * 255 / Math.Max(1, width - 1)), (byte)(y * 255 / Math.Max(1, height - 1)), (byte)(128 + (x + y) % 100)));

    public static byte[] Solid(int width, int height, byte r, byte g, byte b) => ArtGenerator.Png(width, height, (_, _) => (r, g, b));

    /// <summary>Incompressible noise: a PNG a little over 3 bytes per pixel.</summary>
    public static byte[] Noise(int width, int height, int seed = 7)
    {
        var random = new Random(seed);
        return ArtGenerator.Png(width, height, (_, _) => ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
    }

    /// <summary>The same picture as a JPEG (through the cache's own encoder).</summary>
    public static async Task<byte[]> JpegAsync(byte[] png)
    {
        using ArtImage image = await ArtImage.OpenAsync(png, CancellationToken.None);
        ArtImage.Pixels pixels = await image.RenderAsync(int.MaxValue, CancellationToken.None);
        return await ArtImage.EncodeJpegAsync(pixels, int.MaxValue, CancellationToken.None);
    }

    public static async Task<(int Width, int Height)> SizeOfAsync(string path)
    {
        using ArtImage image = await ArtImage.OpenAsync(await File.ReadAllBytesAsync(path), CancellationToken.None);
        return (image.Width, image.Height);
    }
}
