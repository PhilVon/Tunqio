using System.Globalization;
using System.Text;

namespace Tunqio.FixtureGen;

/// <summary>
/// The gapless-join fixture set (E1-S2): one continuous four-second chirp cut in half at exactly two seconds and
/// encoded, each half on its own, in every format an encoder exists for. Track a is the first half and b the
/// second, so a sample-continuous join of a then b reproduces the chirp; any gap, overlap or trimmed encoder
/// delay shows up as a lag against the reference (native/mpcore.tests/src/test_gapless.cpp regenerates the same
/// chirp from <see cref="Chirp"/>'s formula). A lossy encoder gets a signal it cannot hide a seam in: the chirp
/// has no period, so a cross-correlation against the reference has a single peak.
/// </summary>
public static class GaplessFixtureBuilder
{
    public const double Seconds = 4.0;
    public const double SplitSeconds = 2.0;
    public const double StartHz = 200.0;
    public const double EndHz = 2000.0;
    public const double Amplitude = 0.25;

    /// <summary>Formats and the sample rate each pair is generated at. The -44k pairs exercise the mixer's resampler at the join.</summary>
    public static readonly IReadOnlyList<(string Name, string Format, int SampleRate)> Pairs =
    [
        ("wav", "wav", 48000),
        ("aiff", "aiff", 48000),
        ("flac", "flac", 48000),
        ("flac-44k", "flac", 44100),
        ("mp3", "mp3", 48000),
        ("mp3-44k", "mp3", 44100),
        ("m4a", "m4a", 48000),
        ("alac", "alac", 48000),
        ("ogg", "ogg", 48000),
        ("opus", "opus", 48000),
        ("wma", "wma", 48000),
        ("wv", "wv", 48000),
    ];

    /// <summary>Linear chirp from StartHz to EndHz over Seconds, sampled at frame i of sampleRate: identical on both channels.</summary>
    public static short Chirp(long i, int sampleRate)
    {
        double t = (double)i / sampleRate;
        double phase = 2 * Math.PI * (StartHz * t + (EndHz - StartHz) * t * t / (2 * Seconds));
        return (short)Math.Round(Amplitude * Math.Sin(phase) * short.MaxValue);
    }

    public static short[] Segment(int sampleRate, double fromSeconds, double toSeconds)
    {
        long first = (long)Math.Round(fromSeconds * sampleRate);
        long last = (long)Math.Round(toSeconds * sampleRate);
        var pcm = new short[(last - first) * AudioSynth.Channels];
        for (long i = first; i < last; i++)
        {
            short s = Chirp(i, sampleRate);
            pcm[(i - first) * 2] = s;
            pcm[(i - first) * 2 + 1] = s;
        }

        return pcm;
    }

    /// <summary>Writes every pair under outputRoot/&lt;name&gt;/a.&lt;ext&gt; and b.&lt;ext&gt;; returns the names written and those skipped for want of ffmpeg.</summary>
    public static (List<string> Written, List<string> Skipped) Build(FfmpegEncoder? ffmpeg, string outputRoot, TextWriter log)
    {
        Directory.CreateDirectory(outputRoot);
        string scratch = Path.Combine(Path.GetTempPath(), "tunqio-gapless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var written = new List<string>();
        var skipped = new List<string>();
        try
        {
            foreach ((string name, string format, int rate) in Pairs)
            {
                bool native = FixturePlan.NativeFormats.Contains(format);
                if (!native && ffmpeg is null)
                {
                    skipped.Add(name);
                    continue;
                }

                string dir = Path.Combine(outputRoot, name);
                Directory.CreateDirectory(dir);
                string ext = FfmpegEncoder.Extension(format);
                Write(Segment(rate, 0, SplitSeconds), rate, format, Path.Combine(dir, "a." + ext), scratch, ffmpeg);
                Write(Segment(rate, SplitSeconds, Seconds), rate, format, Path.Combine(dir, "b." + ext), scratch, ffmpeg);
                written.Add(name);
                log.WriteLine($"  {name}: a.{ext} + b.{ext} at {rate} Hz");
            }

            File.WriteAllText(Path.Combine(outputRoot, "README.md"), Readme(written, skipped), new UTF8Encoding(false));
            return (written, skipped);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void Write(short[] pcm, int sampleRate, string format, string outputPath, string scratch, FfmpegEncoder? ffmpeg)
    {
        switch (format)
        {
            case "wav":
                File.WriteAllBytes(outputPath, AudioSynth.Wav(pcm, sampleRate));
                return;
            case "aiff":
                File.WriteAllBytes(outputPath, AudioSynth.Aiff(pcm, sampleRate));
                return;
            default:
                string wav = Path.Combine(scratch, "source.wav");
                File.WriteAllBytes(wav, AudioSynth.Wav(pcm, sampleRate));
                ffmpeg!.Encode(wav, format, outputPath);
                return;
        }
    }

    private static string Readme(List<string> written, List<string> skipped)
    {
        var sb = new StringBuilder();
        sb.Append("# Gapless-join fixtures (E1-S2)\n\n");
        sb.Append(string.Create(CultureInfo.InvariantCulture,
            $"One linear chirp, {StartHz} Hz to {EndHz} Hz over {Seconds} s at {Amplitude} full scale, cut at {SplitSeconds} s: `a` is the first half, `b` the second, each encoded on its own. "));
        sb.Append("A sample-continuous join of `a` then `b` reproduces the chirp; `native/mpcore.tests/src/test_gapless.cpp` measures the lag and the residual at the seam against the same chirp regenerated in C++.\n\n");
        sb.Append("Do not edit the files by hand. Regenerate with ffmpeg on PATH:\n\n```bash\ndotnet run -c Release -p:Platform=x64 --project tools/FixtureGen -- gapless -out tests/fixtures/gapless\n```\n\n");
        sb.Append("| Pair | Codec arguments |\n|------|-----------------|\n");
        foreach ((string name, string format, int rate) in Pairs)
        {
            string recipe = FixturePlan.NativeFormats.Contains(format) ? "(written by FixtureGen)" : "`" + FfmpegEncoder.CodecArguments(format) + "`";
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"| {name} ({rate} Hz) | {recipe} |\n"));
        }

        if (skipped.Count > 0)
        {
            sb.Append("\nSkipped (no ffmpeg): ").Append(string.Join(", ", skipped)).Append('\n');
        }

        return sb.ToString();
    }
}
