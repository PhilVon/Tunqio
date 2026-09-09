using System.Buffers.Binary;

namespace Tunqio.FixtureGen;

/// <summary>Deterministic one-second PCM sources and the two container writers that need no encoder.</summary>
public static class AudioSynth
{
    public const double Seconds = 1.0;
    public const int Channels = 2;
    public const int BitsPerSample = 16;

    /// <summary>16-bit interleaved stereo PCM, one second. The sweep is seeded so every track sounds different but identical run to run.</summary>
    public static short[] Generate(FixtureSignal signal, int sampleRate, int seed)
    {
        int frames = (int)(Seconds * sampleRate);
        var pcm = new short[frames * Channels];
        if (signal == FixtureSignal.Silence)
        {
            return pcm;
        }

        // Exponential sweep from f0 to f1 with a seed-dependent range and a short fade at both ends.
        double f0 = 110.0 + (seed % 7) * 55.0;
        double f1 = 1760.0 + (seed % 5) * 440.0;
        double amplitude = 0.25;
        double k = Math.Log(f1 / f0);
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / sampleRate;
            double phase = 2 * Math.PI * f0 * Seconds / k * (Math.Exp(k * t / Seconds) - 1);
            double fade = Math.Min(1.0, Math.Min(i, frames - 1 - i) / (0.01 * sampleRate));
            var sample = (short)Math.Round(amplitude * fade * Math.Sin(phase) * short.MaxValue);
            pcm[i * 2] = sample;
            pcm[i * 2 + 1] = (short)(sample * 0.8); // slight stereo difference
        }

        return pcm;
    }

    public static byte[] Wav(short[] pcm, int sampleRate)
    {
        int dataBytes = pcm.Length * 2;
        var buffer = new byte[44 + dataBytes];
        Span<byte> b = buffer;
        "RIFF"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(b[8..]);
        "fmt "u8.CopyTo(b[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(b[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(b[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(b[22..], Channels);
        BinaryPrimitives.WriteInt32LittleEndian(b[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(b[28..], sampleRate * Channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(b[32..], Channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(b[34..], BitsPerSample);
        "data"u8.CopyTo(b[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(b[40..], dataBytes);
        for (int i = 0; i < pcm.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(b[(44 + i * 2)..], pcm[i]);
        }

        return buffer;
    }

    /// <summary>AIFF: big-endian PCM in FORM/COMM/SSND chunks (sample rate as an 80-bit IEEE extended float).</summary>
    public static byte[] Aiff(short[] pcm, int sampleRate)
    {
        int frames = pcm.Length / Channels;
        int dataBytes = pcm.Length * 2;
        int ssndSize = 8 + dataBytes;
        int formSize = 4 + (8 + 18) + (8 + ssndSize);
        var buffer = new byte[8 + formSize];
        Span<byte> b = buffer;
        "FORM"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32BigEndian(b[4..], formSize);
        "AIFF"u8.CopyTo(b[8..]);
        "COMM"u8.CopyTo(b[12..]);
        BinaryPrimitives.WriteInt32BigEndian(b[16..], 18);
        BinaryPrimitives.WriteInt16BigEndian(b[20..], Channels);
        BinaryPrimitives.WriteInt32BigEndian(b[22..], frames);
        BinaryPrimitives.WriteInt16BigEndian(b[26..], BitsPerSample);
        WriteExtended(b[28..38], sampleRate);
        "SSND"u8.CopyTo(b[38..]);
        BinaryPrimitives.WriteInt32BigEndian(b[42..], ssndSize);
        BinaryPrimitives.WriteInt32BigEndian(b[46..], 0); // offset
        BinaryPrimitives.WriteInt32BigEndian(b[50..], 0); // block size
        for (int i = 0; i < pcm.Length; i++)
        {
            BinaryPrimitives.WriteInt16BigEndian(b[(54 + i * 2)..], pcm[i]);
        }

        return buffer;
    }

    private static void WriteExtended(Span<byte> dst, int value)
    {
        // 80-bit extended: 1 sign bit, 15-bit exponent (bias 16383), 64-bit mantissa with explicit leading 1.
        int exponent = 0;
        ulong mantissa = (ulong)value;
        while (mantissa < 0x8000000000000000UL)
        {
            mantissa <<= 1;
            exponent++;
        }

        int biased = 16383 + 63 - exponent;
        BinaryPrimitives.WriteUInt16BigEndian(dst, (ushort)biased);
        BinaryPrimitives.WriteUInt64BigEndian(dst[2..], mantissa);
    }
}
