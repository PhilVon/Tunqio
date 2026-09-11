namespace Tunqio.Benchmarks;

/// <summary>
/// A small 16-bit PCM sine WAV in the temp directory, for the benchmarks that need the engine to be making
/// audio rather than sitting idle. The committed fixtures under <c>tests/fixtures</c> are library databases;
/// this is the audio equivalent, and it is generated rather than committed because it is two seconds of a sine.
/// </summary>
internal static class SineWav
{
    public static string Write(string stem, double seconds = 2.0, int sampleRate = 48000, int channels = 2,
                               double frequencyHz = 1000.0, double amplitude = 0.5)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tunqio-bench-{stem}.wav");
        int frames = (int)(seconds * sampleRate);
        int blockAlign = channels * 2;
        int dataBytes = frames * blockAlign;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write((short)blockAlign);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (int i = 0; i < frames; i++)
        {
            var sample = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate) * 32767);
            for (int c = 0; c < channels; c++)
            {
                writer.Write(sample);
            }
        }

        return path;
    }
}
