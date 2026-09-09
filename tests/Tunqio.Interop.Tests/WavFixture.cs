namespace Tunqio.Interop.Tests;

/// <summary>Writes a small 16-bit PCM sine WAV (same shape as the native tests' wav_fixture.h) until E0-S7 provides fixtures.</summary>
internal static class WavFixture
{
    public static string WriteSine(string stem, double seconds = 1.0, int sampleRate = 48000, int channels = 2, double frequencyHz = 440.0, double amplitude = 0.1)
    {
        string path = Path.Combine(Path.GetTempPath(), $"tunqio-interop-{stem}.wav");
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
