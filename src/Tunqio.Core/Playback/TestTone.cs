namespace Tunqio.Core.Playback;

/// <summary>
/// The Output page's "test tone" (E6-S3, AC-146): a short sine as a 16-bit PCM WAV, built in memory. The session plays
/// it on the preview stream, so it goes out through whichever device the output is open on, over music or without it.
/// </summary>
/// <remarks>
/// A WAV rather than a new native export: the preview stream already opens a file and mixes it after the analysis tap,
/// which is exactly "through the selected device, without disturbing playback", and a file costs no ABI change. Pure
/// bytes here because Core does no file I/O; the shell writes them where the engine can open them.
/// </remarks>
public static class TestTone
{
    public const int SampleRate = 48_000;
    public const int Channels = 2;
    public const double FrequencyHz = 440.0;

    /// <summary>Long enough to recognise, short enough not to be a nuisance.</summary>
    public static readonly TimeSpan Length = TimeSpan.FromSeconds(1.5);

    /// <summary>Peak amplitude, about -12 dBFS: audible without being a shock through a system turned up for quiet music.</summary>
    public const double Amplitude = 0.25;

    /// <summary>Fade at each end, so the tone starts and stops without a click.</summary>
    public static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(30);

    /// <summary>The whole file: a RIFF/WAVE header and <see cref="Length"/> of <see cref="FrequencyHz"/> on both channels.</summary>
    public static byte[] Wav()
    {
        int frames = (int)(SampleRate * Length.TotalSeconds);
        int fadeFrames = (int)(SampleRate * Fade.TotalSeconds);
        const int bytesPerSample = 2;
        int dataBytes = frames * Channels * bytesPerSample;
        byte[] wav = new byte[44 + dataBytes];
        var span = wav.AsSpan();

        WriteAscii(span, 0, "RIFF");
        WriteInt32(span, 4, 36 + dataBytes);
        WriteAscii(span, 8, "WAVE");
        WriteAscii(span, 12, "fmt ");
        WriteInt32(span, 16, 16);                                     // PCM fmt chunk size
        WriteInt16(span, 20, 1);                                      // PCM
        WriteInt16(span, 22, Channels);
        WriteInt32(span, 24, SampleRate);
        WriteInt32(span, 28, SampleRate * Channels * bytesPerSample); // byte rate
        WriteInt16(span, 32, Channels * bytesPerSample);              // block align
        WriteInt16(span, 34, bytesPerSample * 8);
        WriteAscii(span, 36, "data");
        WriteInt32(span, 40, dataBytes);

        int offset = 44;
        for (int i = 0; i < frames; i++)
        {
            double envelope = Math.Min(1.0, Math.Min(i, frames - 1 - i) / (double)Math.Max(1, fadeFrames));
            short sample = (short)Math.Round(Math.Sin(2 * Math.PI * FrequencyHz * i / SampleRate) * Amplitude * envelope * short.MaxValue);
            for (int c = 0; c < Channels; c++)
            {
                WriteInt16(span, offset, sample);
                offset += bytesPerSample;
            }
        }

        return wav;
    }

    private static void WriteAscii(Span<byte> span, int offset, string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            span[offset + i] = (byte)text[i];
        }
    }

    private static void WriteInt32(Span<byte> span, int offset, int value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[offset..], value);

    private static void WriteInt16(Span<byte> span, int offset, int value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span[offset..], (short)value);
}
