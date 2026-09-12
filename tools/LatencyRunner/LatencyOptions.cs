using System.Globalization;
using Tunqio.Core.Audio;

namespace Tunqio.LatencyRunner;

/// <summary>What the run was asked to do. Parsed from <c>-name value</c> arguments; anything unknown is an error
/// rather than a shrug, because a measurement that quietly measured the wrong shape is worse than none.</summary>
internal sealed record LatencyOptions(
    string? Source,
    TimeSpan Phase,
    int DeviceIndex,
    OutputMode Mode,
    int BufferMs,
    float Volume,
    string? PresetId,
    string? PresetRoot,
    int Width,
    int Height,
    bool ForceWarp,
    float OffsetMs,
    double BudgetMs,
    string? ReportPath)
{
    public static readonly string[] AudioExtensions =
        [".flac", ".mp3", ".m4a", ".ogg", ".opus", ".wav", ".aiff", ".aif", ".wv", ".ape"];

    public static LatencyOptions Parse(string[] args, string repoRoot)
    {
        string? source = null; // a tone this harness writes; see ToneFixture
        TimeSpan phase = TimeSpan.FromSeconds(20);
        int device = OutputConfig.DefaultDevice;
        OutputMode mode = OutputMode.Shared;
        int bufferMs = 40;
        float volume = 0f;
        string? presetId = null;
        string? presetRoot = Path.Combine(repoRoot, "presets");
        int width = 1280;
        int height = 720;
        bool warp = false;
        float offsetMs = 0f;
        // One refresh interval at 60 Hz, which is what AC-131 names. It is a REPORTING line and not a gate the
        // build runs: docs/spikes/e4-s8-latency-floor.md says why a bound on this number cannot be one on this
        // machine. The exit code says whether the run produced a usable measurement, not whether it was fast.
        double budgetMs = 1000.0 / 60.0;
        string? report = null;

        for (int i = 0; i < args.Length; i++)
        {
            string name = args[i];
            if (name is "-h" or "--help" or "-?")
            {
                throw new LatencyUsageException(Usage);
            }

            if (name == "-warp")
            {
                warp = true;
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new LatencyUsageException($"{name} needs a value.\n\n{Usage}");
            }

            string value = args[++i];
            switch (name)
            {
                case "-source": source = Path.GetFullPath(value); break;
                case "-seconds": phase = TimeSpan.FromSeconds(Number(name, value)); break;
                case "-device": device = (int)Number(name, value); break;
                case "-mode": mode = ParseMode(value); break;
                case "-buffer": bufferMs = (int)Number(name, value); break;
                case "-volume": volume = (float)Number(name, value); break;
                case "-preset": presetId = value; break;
                case "-preset-root": presetRoot = Path.GetFullPath(value); break;
                case "-width": width = (int)Number(name, value); break;
                case "-height": height = (int)Number(name, value); break;
                case "-offset": offsetMs = (float)Number(name, value); break;
                case "-budget": budgetMs = Number(name, value); break;
                case "-out": report = Path.GetFullPath(value); break;
                default: throw new LatencyUsageException($"Unknown argument {name}.\n\n{Usage}");
            }
        }

        if (phase <= TimeSpan.Zero)
        {
            throw new LatencyUsageException("-seconds needs a positive number.");
        }

        if (width <= 0 || height <= 0)
        {
            throw new LatencyUsageException("-width and -height need to be positive.");
        }

        return new LatencyOptions(source, phase, device, mode, bufferMs, volume, presetId, presetRoot, width, height,
            warp, offsetMs, budgetMs, report);
    }

    public const string Usage = """
        Tunqio latency runner (E4-S8): how far the picture is from the sound, on a real output device.

        It runs the SAME measurement twice - once drawing the newest analysis frame, the way every build
        before ABI 0.17 did, and once drawing the frame the listener is hearing - so what compensation does
        is a difference on the page rather than a claim. The renderer is headless: it draws into an offscreen
        texture, so the run needs no shell and no desktop. What that costs is the present-to-photon edge,
        which no measurement from inside this process can see; docs/spikes/e4-s8-latency-floor.md is the accounting.

          -source <dir|file>    audio to play. The default is a 440 Hz tone this harness writes to its own
                                scratch directory and deletes afterwards, long enough for the whole run - the
                                fixture library's tracks are about a second each, and a track that ends is a
                                measurement that stops. Nothing here reads anybody's music by default.
          -seconds <n>          how long EACH of the two phases runs (default 20)
          -device <index>       output device (default -1, the system default)
          -mode shared|exclusive
          -buffer <ms>          output buffer (default 40) - also the compensation budget
          -volume <0..1>        default 0; the mixer and the WASAPI proc run either way, and this measures
                                positions rather than content, so silence measures the same as music
          -preset <id>          which preset to draw (default: the first in the catalogue)
          -preset-root <dir>    where to scan for presets (default presets/ beside the repository root)
          -width / -height      render size in pixels (default 1280x720)
          -warp                 force the software rasteriser
          -offset <ms>          av-sync offset for the compensated phase; positive draws EARLIER, which is
                                how a caller pays for the present-to-photon edge this cannot measure
          -budget <ms>          the line the report compares p95 against (default 16.67, one 60 Hz refresh)
          -out <file.json>      write the report here as well as to the console

        Exit code is 0 when the run produced a usable measurement in both phases. It is deliberately NOT the
        budget: see the spike doc.
        """;

    private static double Number(string name, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : throw new LatencyUsageException($"{name} needs a number, not '{value}'.");

    private static OutputMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "shared" => OutputMode.Shared,
        "exclusive" => OutputMode.Exclusive,
        _ => throw new LatencyUsageException($"-mode is shared or exclusive, not '{value}'."),
    };
}

internal sealed class LatencyUsageException(string message) : Exception(message);
