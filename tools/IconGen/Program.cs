// Tunqio.IconGen (T-191): the one way Tunqio's icon assets are made. It renders the committed source of Phil's logo,
// assets/brand/tunqio-icon.svg, straight at every pixel size assets/brand/icon-assets.json lists: the multi-size
// Assets/Tunqio.ico, the MSIX tiles and their scale and targetsize variants, the file association logo, the tray icons and
// the About page logo. Every size is a render of the vector at that size, never a downscale of a large bitmap, so 16 px is
// Skia's own antialiased rasterisation of the paths at 16 px. The art is not simplified, recoloured or redrawn anywhere here.
//
// Renderer: Svg.Skia 5.2.3 over SkiaSharp 4.148.0 (both MIT), pinned in Directory.Packages.props and restored like any
// other package. Chosen over Direct2D's built-in SVG support (ID2D1DeviceContext5::CreateSvgDocument), which needs no package
// but would mean hand-written COM interop, and whose output belongs to whichever Windows build and D2D rasteriser runs it.
// Skia's CPU raster backend is a pinned package, the same code on every machine, and Svg.Skia covers everything this SVG
// uses: a userSpaceOnUse linearGradient, a stroked circle with stroke-dasharray and round caps, rounded rects and rotate().
// Build-time only: this tool is not referenced by the app, so neither package ships and neither is in THIRD-PARTY-NOTICES.md.
//
// Deterministic: the pixels come from Skia's CPU rasteriser, and the files are written here byte by byte, not by an encoder
// that may add a timestamp: a PNG holds IHDR, one IDAT and IEND only, with a fixed per-row filter choice and zlib at
// SmallestSize, and an .ico is a header, a directory and those PNGs. Running it twice writes byte-identical files; --check
// renders in memory and fails when any committed file differs, so the proof is repeatable.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;
using Svg.Skia;

namespace Tunqio.IconGen;

internal static class Program
{
    private const string SpecPath = "assets/brand/icon-assets.json";

    private static int Main(string[] args)
    {
        bool check = false;
        string? previews = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--check":
                    check = true;
                    break;
                case "--previews" when i + 1 < args.Length:
                    previews = Path.GetFullPath(args[++i]);
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'. Usage: [--check] [--previews DIR]");
                    return 2;
            }
        }

        string repo = FindRepoRoot();
        IconSpec spec = IconSpec.Load(Path.Combine(repo, SpecPath));
        string source = Path.Combine(repo, spec.Source);
        string appRoot = Path.Combine(repo, spec.AppRoot);

        using var svg = new SKSvg();
        SKPicture picture = svg.Load(source) ?? throw new InvalidOperationException($"Svg.Skia could not load {source}");
        SKRect box = picture.CullRect;
        if (box.Width <= 0 || Math.Abs(box.Width - box.Height) > 0.01f)
        {
            throw new InvalidOperationException($"{source} is expected to be square; its picture bounds are {box}");
        }

        Console.WriteLine($"source:  {spec.Source} ({box.Width.ToString(CultureInfo.InvariantCulture)} x {box.Height.ToString(CultureInfo.InvariantCulture)} units, sha256 {Hash(File.ReadAllBytes(source))})");
        Console.WriteLine($"output:  {spec.AppRoot}/");

        var outputs = new List<(string Path, byte[] Bytes, string Detail)>();
        foreach (IconEntry icon in spec.Icons)
        {
            var frames = icon.Sizes.Select(size => (size, Png.Encode(Render(picture, size, size, size), size, size))).ToList();
            outputs.Add((icon.Path, Ico.Encode(frames), "ico " + string.Join(",", icon.Sizes)));
        }

        foreach (ImageEntry image in spec.Images)
        {
            int side = Math.Min(image.Width, image.Height);
            byte[] rgba = Render(picture, image.Width, image.Height, side);
            outputs.Add((image.Path, Png.Encode(rgba, image.Width, image.Height), $"png {image.Width}x{image.Height}"));
        }

        int differences = 0;
        foreach ((string path, byte[] bytes, string detail) in outputs)
        {
            string file = Path.Combine(appRoot, path.Replace('/', Path.DirectorySeparatorChar));
            byte[]? existing = File.Exists(file) ? File.ReadAllBytes(file) : null;
            bool same = existing is not null && existing.AsSpan().SequenceEqual(bytes);
            if (check)
            {
                differences += same ? 0 : 1;
                Console.WriteLine($"  {(same ? "same" : existing is null ? "MISSING" : "DIFFERS")}  {path}  {detail}  {Hash(bytes)}");
                continue;
            }

            if (!same)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllBytes(file, bytes);
            }

            Console.WriteLine($"  {(same ? "unchanged" : "written  ")}  {path}  {detail}  {bytes.Length} bytes  {Hash(bytes)}");
        }

        if (previews is not null)
        {
            Previews.Write(previews, size => Render(picture, size, size, size));
        }

        if (check)
        {
            Console.WriteLine(differences == 0
                ? $"IconGen --check: PASS ({outputs.Count} assets match a fresh render)"
                : $"IconGen --check: FAIL ({differences} of {outputs.Count} assets differ from a fresh render; run tools/IconGen and commit)");
            return differences == 0 ? 0 : 1;
        }

        Console.WriteLine($"IconGen: {outputs.Count} assets");
        return 0;
    }

    /// <summary>
    /// The picture drawn at <paramref name="side"/> pixels square, centred on a transparent <paramref name="width"/> by
    /// <paramref name="height"/> image, returned as straight (unpremultiplied) RGBA rows.
    /// </summary>
    internal static byte[] Render(SKPicture picture, int width, int height, int side)
    {
        SKRect box = picture.CullRect;
        var premul = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate((width - side) / 2f, (height - side) / 2f);
            canvas.Scale(side / box.Width);
            canvas.Translate(-box.Left, -box.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();
        }

        var straight = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        byte[] rgba = new byte[width * height * 4];
        GCHandle pin = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            using SKPixmap pixmap = bitmap.PeekPixels();
            if (!pixmap.ReadPixels(straight, pin.AddrOfPinnedObject(), straight.RowBytes, 0, 0))
            {
                throw new InvalidOperationException($"Skia could not read back a {width}x{height} render");
            }
        }
        finally
        {
            pin.Free();
        }

        return rgba;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Tunqio.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        dir = Directory.GetCurrentDirectory();
        return File.Exists(Path.Combine(dir, "Tunqio.sln"))
            ? dir
            : throw new InvalidOperationException("Tunqio.sln not found above " + AppContext.BaseDirectory + " or in the current directory");
    }
}

/// <summary>assets/brand/icon-assets.json.</summary>
internal sealed record IconSpec(string Source, string AppRoot, IReadOnlyList<IconEntry> Icons, IReadOnlyList<ImageEntry> Images)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IconSpec Load(string path) =>
        JsonSerializer.Deserialize<IconSpec>(File.ReadAllText(path), Options)
        ?? throw new InvalidOperationException($"{path} is empty");
}

/// <summary>An .ico and the pixel sizes of its entries.</summary>
internal sealed record IconEntry(string Path, IReadOnlyList<int> Sizes);

/// <summary>A PNG and its pixel size.</summary>
internal sealed record ImageEntry(string Path, int Width, int Height);
