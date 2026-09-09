using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Tunqio.Library.Art;

/// <summary>
/// One source image open in a WIC decoder (<c>Windows.Graphics.Imaging</c>, the design's decoder: JPEG, PNG,
/// BMP, GIF, TIFF, and WebP or HEIF when the codec is installed). Renders BGRA pixels at a long edge, never
/// upscaling, with EXIF orientation applied and colour managed to sRGB, and encodes pixels as JPEG at quality 85.
/// </summary>
internal sealed class ArtImage : IDisposable
{
    private const float JpegQuality = 0.85f;

    private readonly InMemoryRandomAccessStream _stream;
    private readonly BitmapDecoder _decoder;

    private ArtImage(InMemoryRandomAccessStream stream, BitmapDecoder decoder)
    {
        _stream = stream;
        _decoder = decoder;
        Width = (int)decoder.OrientedPixelWidth;
        Height = (int)decoder.OrientedPixelHeight;
        Guid codec = decoder.DecoderInformation.CodecId;
        OriginalExtension = codec == BitmapDecoder.JpegDecoderId ? "jpg" : codec == BitmapDecoder.PngDecoderId ? "png" : null;
    }

    /// <summary>Width after EXIF orientation.</summary>
    public int Width { get; }

    /// <summary>Height after EXIF orientation.</summary>
    public int Height { get; }

    /// <summary><c>jpg</c> or <c>png</c> for a source the cache keeps untouched as <c>original.*</c>; <c>null</c> for other containers.</summary>
    public string? OriginalExtension { get; }

    /// <summary>Opens the bytes in a decoder; throws for anything WIC cannot decode (the caller turns that into "no art").</summary>
    public static async Task<ArtImage> OpenAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        var stream = new InMemoryRandomAccessStream();
        try
        {
            IBuffer buffer = MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment)
                ? segment.Array!.AsBuffer(segment.Offset, segment.Count)
                : bytes.ToArray().AsBuffer();
            await stream.WriteAsync(buffer).AsTask(ct).ConfigureAwait(false);
            stream.Seek(0);
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);
            return new ArtImage(stream, decoder);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>The image scaled so its long edge is at most <paramref name="longEdge"/> (a smaller image keeps its size).</summary>
    public async Task<Pixels> RenderAsync(int longEdge, CancellationToken ct)
    {
        (int width, int height) = Fit(Width, Height, longEdge);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)width,
            ScaledHeight = (uint)height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        PixelDataProvider data = await _decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb).AsTask(ct).ConfigureAwait(false);
        return new Pixels(data.DetachPixelData(), width, height);
    }

    /// <summary>JPEG bytes of <paramref name="pixels"/>, scaled down to <paramref name="longEdge"/> when larger.</summary>
    public static async Task<byte[]> EncodeJpegAsync(Pixels pixels, int longEdge, CancellationToken ct)
    {
        using var output = new InMemoryRandomAccessStream();
        var properties = new BitmapPropertySet
        {
            ["ImageQuality"] = new BitmapTypedValue(JpegQuality, PropertyType.Single),
        };
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, properties).AsTask(ct).ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)pixels.Width, (uint)pixels.Height, 96, 96, pixels.Bgra);
        (int width, int height) = Fit(pixels.Width, pixels.Height, longEdge);
        if (width != pixels.Width || height != pixels.Height)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)width;
            encoder.BitmapTransform.ScaledHeight = (uint)height;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);
        output.Seek(0);
        using var reader = new DataReader(output);
        uint size = (uint)output.Size;
        await reader.LoadAsync(size).AsTask(ct).ConfigureAwait(false);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>The size whose long edge is <paramref name="longEdge"/> at the same aspect, or the source size when that is smaller.</summary>
    public static (int Width, int Height) Fit(int width, int height, int longEdge)
    {
        int edge = Math.Max(width, height);
        if (edge <= longEdge)
        {
            return (width, height);
        }

        double scale = longEdge / (double)edge;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    public void Dispose() => _stream.Dispose();

    /// <summary>BGRA pixels, row-major without padding.</summary>
    public sealed record Pixels(byte[] Bgra, int Width, int Height);
}
