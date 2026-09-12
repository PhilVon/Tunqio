using System.Buffers.Binary;

namespace Tunqio.LatencyRunner;

/// <summary>
/// The audio this harness plays by default: one continuous tone, written to a per-process scratch directory
/// and deleted when the run ends.
/// </summary>
/// <remarks>
/// WHY NOT THE FIXTURE LIBRARY, which is what the soak runner uses. Its tracks are about a second each, and a
/// track that ends is a measurement that stops: the analysis publishes nothing, the renderer redraws the last
/// frame it had, and every picture after that reports an error that grows by a millisecond a millisecond. The
/// first attempt at this harness measured exactly that and reported one distinct analysis frame over fifteen
/// seconds. Restarting the track instead would put a guard fade and a window restart into the middle of the
/// distribution, which is a real thing a player does but is not what this run is about.
///
/// AND NOT SOMEBODY'S MUSIC. -source takes a file or a directory for anyone who wants to measure against real
/// audio, but the DEFAULT must not be a library: nothing in this tool should read what somebody is listening
/// to in order to produce a number that does not depend on it. Positions are what is measured here; a sine is
/// as good as a symphony and costs nobody their privacy.
/// </remarks>
internal static class ToneFixture
{
    public static string Write(string directory, double seconds, int sampleRate = 48000, double frequencyHz = 440.0)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "latency-tone.wav");
        const int channels = 2;
        const int bytesPerSample = 2;
        int frames = (int)(seconds * sampleRate);
        int dataBytes = frames * channels * bytesPerSample;

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        Span<byte> header = stackalloc byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 36 + dataBytes);
        "WAVEfmt "u8.CopyTo(header[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(header[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], sampleRate * channels * bytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(header[32..], channels * bytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(header[34..], bytesPerSample * 8);
        "data"u8.CopyTo(header[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], dataBytes);
        file.Write(header);

        // A tone with a one-hertz tremolo. The steady part is what the analysis measures; the tremolo is so
        // that anyone who runs this at a volume above the default zero can hear at once that it is alive.
        var block = new byte[sampleRate * channels * bytesPerSample];
        int written = 0;
        while (written < frames)
        {
            int count = Math.Min(sampleRate, frames - written);
            for (int i = 0; i < count; i++)
            {
                double t = (written + i) / (double)sampleRate;
                double envelope = 0.6 + 0.4 * Math.Sin(2 * Math.PI * t);
                double value = 0.2 * envelope * Math.Sin(2 * Math.PI * frequencyHz * t);
                var sample = (short)Math.Round(value * short.MaxValue);
                for (int c = 0; c < channels; c++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(block.AsSpan((i * channels + c) * bytesPerSample), sample);
                }
            }

            file.Write(block, 0, count * channels * bytesPerSample);
            written += count;
        }

        return path;
    }
}
