using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;
using System.Text;

namespace Tunqio.FixtureGen;

/// <summary>Deterministic PNG cover art: a two-colour diagonal split derived from the album title, 300 x 300, RGB.</summary>
public static class ArtGenerator
{
    public static byte[] Png(string albumTitle, int size = 300)
    {
        uint seed = Crc32.HashToUInt32(Encoding.UTF8.GetBytes(albumTitle));
        (byte r1, byte g1, byte b1) = ((byte)(seed >> 24), (byte)(seed >> 16), (byte)(seed >> 8));
        (byte r2, byte g2, byte b2) = ((byte)(255 - r1), (byte)(255 - g1), (byte)(255 - b1));

        var raw = new byte[size * (1 + size * 3)];
        for (int y = 0; y < size; y++)
        {
            int row = y * (1 + size * 3);
            raw[row] = 0; // filter: none
            for (int x = 0; x < size; x++)
            {
                bool upper = x + y < size;
                int p = row + 1 + x * 3;
                raw[p] = upper ? r1 : r2;
                raw[p + 1] = upper ? g1 : g2;
                raw[p + 2] = upper ? b1 : b2;
            }
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, size);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), size);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // colour type RGB
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        png.Write(len);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        png.Write(typeBytes);
        png.Write(data);
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc.GetCurrentHashAsUInt32());
        png.Write(crcBytes);
    }
}
