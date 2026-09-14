namespace Tunqio.IconGen;

/// <summary>
/// Review images for the small sizes (T-191): each size rendered exactly as the assets are, laid over a light and a dark
/// taskbar colour and enlarged 8x with nearest-neighbour, so every pixel of the real icon is visible. Not assets; they go to
/// the directory given (artifacts\icon-previews by convention, which git ignores).
/// </summary>
internal static class Previews
{
    private const int Zoom = 8;

    /// <summary>The Windows 11 light and dark taskbar colours.</summary>
    private static readonly (string Name, byte R, byte G, byte B)[] Backgrounds = [("light", 0xF3, 0xF3, 0xF3), ("dark", 0x20, 0x20, 0x20)];

    private static readonly int[] Sizes = [16, 20, 24, 32, 48];

    public static void Write(string directory, Func<int, byte[]> render)
    {
        Directory.CreateDirectory(directory);
        foreach (int size in Sizes)
        {
            byte[] rgba = render(size);
            foreach ((string name, byte r, byte g, byte b) in Backgrounds)
            {
                int side = size * Zoom;
                byte[] zoomed = new byte[side * side * 4];
                for (int y = 0; y < side; y++)
                {
                    for (int x = 0; x < side; x++)
                    {
                        int s = (((y / Zoom) * size) + (x / Zoom)) * 4;
                        int d = ((y * side) + x) * 4;
                        int alpha = rgba[s + 3];
                        zoomed[d] = Over(rgba[s], r, alpha);
                        zoomed[d + 1] = Over(rgba[s + 1], g, alpha);
                        zoomed[d + 2] = Over(rgba[s + 2], b, alpha);
                        zoomed[d + 3] = 255;
                    }
                }

                string file = Path.Combine(directory, $"tunqio-{size}px-on-{name}-x{Zoom}.png");
                File.WriteAllBytes(file, Png.Encode(zoomed, side, side));
                Console.WriteLine($"  preview    {file}");
            }
        }
    }

    private static byte Over(byte source, byte background, int alpha) =>
        (byte)(((source * alpha) + (background * (255 - alpha)) + 127) / 255);
}
