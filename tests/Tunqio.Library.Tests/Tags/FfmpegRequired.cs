using System.Diagnostics;

namespace Tunqio.Library.Tests.Tags;

/// <summary>
/// Marks a test that shells out to ffmpeg or ffprobe, and skips it when neither is on PATH — unless
/// <c>TUNQIO_REQUIRE_FFMPEG</c> is set, in which case the test runs and fails.
/// <para>
/// Both halves matter. ffmpeg is already a prerequisite for regenerating the fixtures
/// (docs/build-test-release.md), but it is not a prerequisite for having an opinion about the tag writer, and a
/// developer without it should not be stopped by three red tests they cannot act on. The variable is what stops
/// that convenience from eating the assertion: ffprobe is not incidental to AC-105, it *is* AC-105 — "readable
/// by another tagger" is a claim about a reader that shares no code with the one we wrote, so a silent skip
/// would retire the only check of it on the one machine everyone reads the result from. CI installs ffmpeg and
/// sets the variable, so a broken install step comes back red rather than quietly green. It is the same rule
/// <c>Tunqio.Benchmarks.PerfGate</c> applies when it fails a run that gated nothing.
/// </para>
/// </summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute() => Skip = FfmpegAvailability.SkipReason;
}

/// <inheritdoc cref="FfmpegFactAttribute"/>
public sealed class FfmpegTheoryAttribute : TheoryAttribute
{
    public FfmpegTheoryAttribute() => Skip = FfmpegAvailability.SkipReason;
}

internal static class FfmpegAvailability
{
    /// <summary>Set by ci.yml, after the step that installs ffmpeg. Absent on a developer machine.</summary>
    private const string RequireVariable = "TUNQIO_REQUIRE_FFMPEG";

    private static readonly Lazy<string?> Reason = new(Resolve);

    /// <summary>Null when the test should run: either the tools are there, or we are told to demand them.</summary>
    public static string? SkipReason => Reason.Value;

    private static string? Resolve()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RequireVariable)))
        {
            return null;
        }

        return OnPath("ffprobe") && OnPath("ffmpeg")
            ? null
            : $"ffmpeg and ffprobe are not on PATH. Install them to run this, or set {RequireVariable} to make their absence a failure (which is what CI does).";
    }

    /// <summary>Asks the process launcher the same question the test will, rather than parsing PATH ourselves.</summary>
    private static bool OnPath(string exe)
    {
        try
        {
            using Process? probe = Process.Start(new ProcessStartInfo(exe, "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (probe is null)
            {
                return false;
            }

            probe.WaitForExit(10_000);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
