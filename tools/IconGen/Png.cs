using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace Tunqio.IconGen;

/// <summary>
/// A minimal, deterministic PNG writer: 8-bit RGBA, IHDR, one IDAT and IEND, nothing else (no time, gamma or text chunk). Each
/// row takes the filter whose output has the smallest sum of absolute values (the PNG specification's suggested heuristic), the
/// lowest filter type winning a tie, and the filtered rows are compressed by zlib at SmallestSize.
/// </summary>
internal static class Png
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException($"{rgba.Length} bytes is not {width}x{height} RGBA", nameof(rgba));
        }

        using var output = new MemoryStream();
        output.Write(Signature);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 6; // colour type: truecolour with alpha
        WriteChunk(output, "IHDR"u8, header);
        WriteChunk(output, "IDAT"u8, Compress(Filter(rgba, width, height)));
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static byte[] Filter(ReadOnlySpan<byte> rgba, int width, int height)
    {
        const int Bpp = 4;
        int stride = width * Bpp;
        byte[] filtered = new byte[(stride + 1) * height];
        byte[] previous = new byte[stride];
        byte[][] candidates = [new byte[stride], new byte[stride], new byte[stride], new byte[stride], new byte[stride]];
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = rgba.Slice(y * stride, stride);
            long best = long.MaxValue;
            int bestType = 0;
            for (int type = 0; type < 5; type++)
            {
                byte[] c = candidates[type];
                long sum = 0;
                for (int x = 0; x < stride; x++)
                {
                    int a = x >= Bpp ? row[x - Bpp] : 0;
                    int b = previous[x];
                    int ab = x >= Bpp ? previous[x - Bpp] : 0;
                    int predictor = type switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        _ => Paeth(a, b, ab),
                    };
                    byte value = unchecked((byte)(row[x] - predictor));
                    c[x] = value;
                    sum += value < 128 ? value : 256 - value;
                }

                if (sum < best)
                {
                    best = sum;
                    bestType = type;
                }
            }

            int offset = y * (stride + 1);
            filtered[offset] = (byte)bestType;
            candidates[bestType].CopyTo(filtered, offset + 1);
            row.CopyTo(previous);
        }

        return filtered;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static byte[] Compress(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return buffer.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        output.Write(type);
        output.Write(data);
        var crc = new Crc32();
        crc.Append(type);
        crc.Append(data);
        BinaryPrimitives.WriteUInt32BigEndian(word, crc.GetCurrentHashAsUInt32());
        output.Write(word);
    }
}

/// <summary>An .ico whose every entry is a PNG (Windows Vista and later read these at any size): ICONDIR, the directory, the images.</summary>
internal static class Ico
{
    public static byte[] Encode(IReadOnlyList<(int Size, byte[] Png)> frames)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write((ushort)0); // reserved
        writer.Write((ushort)1); // type: icon
        writer.Write((ushort)frames.Count);
        uint offset = (uint)(6 + (16 * frames.Count));
        foreach ((int size, byte[] png) in frames)
        {
            writer.Write((byte)(size >= 256 ? 0 : size)); // width; 0 means 256
            writer.Write((byte)(size >= 256 ? 0 : size)); // height
            writer.Write((byte)0); // palette colours
            writer.Write((byte)0); // reserved
            writer.Write((ushort)1); // planes
            writer.Write((ushort)32); // bits per pixel
            writer.Write((uint)png.Length);
            writer.Write(offset);
            offset += (uint)png.Length;
        }

        foreach ((_, byte[] png) in frames)
        {
            writer.Write(png);
        }

        writer.Flush();
        return buffer.ToArray();
    }
}
