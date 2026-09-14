using Tunqio.Core.Audio;
using Tunqio.Core.Playback;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Shell;

/// <summary>
/// The guarded reads behind every live diagnostics number: the engine's output statistics, its clock and the
/// renderer's frame statistics. Shared by the diagnostics overlay (E2-S8) and the About page's performance readout
/// (E6-S5), so the two surfaces show the same sources and neither has its own copy of the guard.
/// </summary>
/// <remarks>
/// Every read is a native call that throws on a refusal, a closed handle or a renderer mid-teardown, and every
/// caller runs from a timer: an exception out of a timer callback reaches the XAML unhandled-exception handler,
/// which logs and does not recover, so an unguarded read ended the process (T-159). A failed read is a null, and
/// the surface says so.
/// </remarks>
public static class DiagnosticsReaders
{
    /// <summary>The engine's output statistics, or null when there is no session or it cannot be read.</summary>
    public static EngineStats? EngineStats(PlaybackSession? session, string surface) =>
        Guard(() => session?.EngineStats, "engine statistics", surface);

    /// <summary>The engine's clock, for the live buffer depth, or null.</summary>
    public static PlaybackClock? Clock(PlaybackSession? session, string surface) =>
        Guard(() => session?.EngineClock, "engine clock", surface);

    /// <summary>The renderer's statistics through <paramref name="renderer"/>, or null when there is none or it cannot be read.</summary>
    public static RenderStats? Renderer(Func<RenderStats?> renderer, string surface)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        return Guard(renderer, "renderer", surface);
    }

    private static T? Guard<T>(Func<T?> read, string what, string surface)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Serilog.Log.Debug(e, "The {What} could not be read for the {Surface}", what, surface);
            return default;
        }
    }
}
