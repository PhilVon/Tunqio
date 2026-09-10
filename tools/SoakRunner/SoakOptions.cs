using System.Globalization;
using Tunqio.Core.Audio;

namespace Tunqio.SoakRunner;

/// <summary>What the run was asked to do. Parsed from <c>-name value</c> arguments; anything unknown is an error
/// rather than a shrug, because a soak that quietly ran the wrong shape is worse than one that did not start.</summary>
internal sealed record SoakOptions(
    string LibraryDirectory,
    TimeSpan Duration,
    int DeviceIndex,
    OutputMode Mode,
    int BufferMs,
    float Volume,
    JoinMode Join,
    TimeSpan SeekEvery,
    TimeSpan SampleEvery,
    string? ReportPath)
{
    /// <summary>The formats <c>mpcore</c> plays; the fixture tree also holds art and a manifest.</summary>
    public static readonly string[] AudioExtensions =
        [".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav", ".aiff", ".aif", ".wv", ".ape"];

    public static SoakOptions Parse(string[] args, string repoRoot)
    {
        string library = Path.Combine(repoRoot, "tests", "fixtures", "library");
        TimeSpan duration = TimeSpan.FromHours(1);
        int device = OutputConfig.DefaultDevice;
        OutputMode mode = OutputMode.Shared;
        int bufferMs = 40;
        float volume = 0f;
        JoinMode join = JoinMode.Gapless;
        TimeSpan seekEvery = TimeSpan.Zero;
        TimeSpan sampleEvery = TimeSpan.FromSeconds(5);
        string? report = null;

        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (name is "-h" or "--help" or "-?")
            {
                throw new SoakUsageException(Usage);
            }

            if (i + 1 >= args.Length)
            {
                throw new SoakUsageException($"{name} needs a value.\n\n{Usage}");
            }

            string value = args[++i];
            switch (name)
            {
                case "-library": library = Path.GetFullPath(value); break;
                case "-minutes": duration = TimeSpan.FromMinutes(Number(name, value)); break;
                case "-seconds": duration = TimeSpan.FromSeconds(Number(name, value)); break;
                case "-device": device = (int)Number(name, value); break;
                case "-mode": mode = ParseMode(value); break;
                case "-buffer": bufferMs = (int)Number(name, value); break;
                case "-volume": volume = (float)Number(name, value); break;
                case "-join": join = ParseJoin(value); break;
                case "-seek-every": seekEvery = TimeSpan.FromSeconds(Number(name, value)); break;
                case "-sample-every": sampleEvery = TimeSpan.FromSeconds(Number(name, value)); break;
                case "-out": report = Path.GetFullPath(value); break;
                default: throw new SoakUsageException($"Unknown argument {name}.\n\n{Usage}");
            }
        }

        if (duration <= TimeSpan.Zero)
        {
            throw new SoakUsageException("The run needs a positive duration.");
        }

        if (sampleEvery <= TimeSpan.Zero)
        {
            throw new SoakUsageException("-sample-every needs a positive number of seconds.");
        }

        return new SoakOptions(library, duration, device, mode, bufferMs, volume, join, seekEvery, sampleEvery, report);
    }

    public const string Usage = """
        Tunqio soak runner (E1-S11): plays a library through a real device and reports underruns.

          -library <dir>        tracks to loop (default tests/fixtures/library)
          -minutes <n>          how long to run (default 60); -seconds <n> for a short check
          -device <index>       output device (default -1, the system default)
          -mode shared|exclusive
          -buffer <ms>          output buffer (default 40)
          -volume <0..1>        default 0 — the WASAPI proc still runs, so underruns are still real
          -join gapless|crossfade
          -seek-every <s>       seek to a random position this often (0 = never)
          -sample-every <s>     how often to read engine stats (default 5)
          -out <file.json>      write the report here as well as to the console

        Exit code is 0 only when the run finished with zero underruns.
        """;

    private static double Number(string name, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : throw new SoakUsageException($"{name} needs a number, not '{value}'.");

    private static OutputMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "shared" => OutputMode.Shared,
        "exclusive" => OutputMode.Exclusive,
        _ => throw new SoakUsageException($"-mode is shared or exclusive, not '{value}'."),
    };

    private static JoinMode ParseJoin(string value) => value.ToLowerInvariant() switch
    {
        "gapless" => JoinMode.Gapless,
        "crossfade" => JoinMode.Crossfade,
        _ => throw new SoakUsageException($"-join is gapless or crossfade, not '{value}'."),
    };
}

internal sealed class SoakUsageException(string message) : Exception(message);
