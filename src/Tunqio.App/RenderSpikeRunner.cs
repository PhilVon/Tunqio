using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Tunqio.Core.Visualization;
using Windows.Graphics;

namespace Tunqio.App;

/// <summary>
/// E0-S5 measurement mode: <c>Tunqio.exe --render-spike [--seconds N] [--resizes N] [--warp] [--out FILE]</c>.
/// Sizes the client area to 1920×1080, renders for N seconds, then performs N rapid resizes (alternating sizes and
/// the original), settles, writes the renderer statistics as JSON and exits with 0 when no missed refresh, no device
/// loss and a 60 fps average were observed in the steady phase.
/// </summary>
internal sealed class RenderSpikeRunner
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    private readonly MainWindow _window;
    private readonly ILogger _logger;
    private readonly int _seconds;
    private readonly int _resizes;
    private readonly string _outputPath;

    public RenderSpikeRunner(MainWindow window, ILogger logger, string[] args, string defaultOutput)
    {
        _window = window;
        _logger = logger;
        _seconds = IntArg(args, "--seconds", 10);
        _resizes = IntArg(args, "--resizes", 100);
        _outputPath = StringArg(args, "--out") ?? defaultOutput;
    }

    public static bool IsRequested(string[] args) => args.Contains("--render-spike", StringComparer.Ordinal);

    public static bool WantsWarp(string[] args) => args.Contains("--warp", StringComparer.Ordinal);

    public void Start()
    {
        DispatcherQueue queue = _window.DispatcherQueue;
        _window.AppWindow.ResizeClient(new SizeInt32(1920, 1080));
        _logger.LogInformation("render spike: {Seconds}s steady at 1920x1080, then {Resizes} rapid resizes", _seconds, _resizes);

        var phaseTimer = queue.CreateTimer();
        phaseTimer.Interval = TimeSpan.FromSeconds(2); // let the swap chain settle after the initial resize
        phaseTimer.IsRepeating = false;
        phaseTimer.Tick += (_, _) => RunSteadyPhase(queue);
        phaseTimer.Start();
    }

    private void RunSteadyPhase(DispatcherQueue queue)
    {
        NativeRendererSnapshot? start = Snapshot();
        var steady = queue.CreateTimer();
        steady.Interval = TimeSpan.FromSeconds(_seconds);
        steady.IsRepeating = false;
        steady.Tick += (_, _) =>
        {
            NativeRendererSnapshot? end = Snapshot();
            RunResizeStorm(queue, start, end);
        };
        steady.Start();
    }

    private void RunResizeStorm(DispatcherQueue queue, NativeRendererSnapshot? steadyStart, NativeRendererSnapshot? steadyEnd)
    {
        int step = 0;
        SizeInt32[] sizes = [new(1280, 720), new(1920, 1080), new(800, 600), new(1600, 900), new(1024, 768), new(1920, 1080)];
        var storm = queue.CreateTimer();
        storm.Interval = TimeSpan.FromMilliseconds(40);
        storm.IsRepeating = true;
        storm.Tick += (_, _) =>
        {
            if (step >= _resizes)
            {
                storm.Stop();
                _window.AppWindow.ResizeClient(new SizeInt32(1920, 1080));
                var settle = queue.CreateTimer();
                settle.Interval = TimeSpan.FromSeconds(2);
                settle.IsRepeating = false;
                settle.Tick += (_, _) => Finish(steadyStart, steadyEnd);
                settle.Start();
                return;
            }

            _window.AppWindow.ResizeClient(sizes[step % sizes.Length]);
            step++;
        };
        storm.Start();
    }

    private void Finish(NativeRendererSnapshot? steadyStart, NativeRendererSnapshot? steadyEnd)
    {
        NativeRendererSnapshot? final = Snapshot();
        double steadyFps = steadyStart is not null && steadyEnd is not null
            ? (steadyEnd.Stats.Frames - steadyStart.Stats.Frames) / (steadyEnd.Elapsed - steadyStart.Elapsed).TotalSeconds
            : 0;
        long steadyMissed = steadyStart is not null && steadyEnd is not null ? steadyEnd.Stats.DxgiMissedRefreshes - steadyStart.Stats.DxgiMissedRefreshes : -1;
        bool pass = final is not null && !final.Stats.DeviceLost && steadyFps >= 59.0 && steadyMissed == 0;

        var report = new
        {
            Machine = Environment.MachineName,
            Adapter = final?.Stats.Adapter,
            Warp = final?.Stats.Warp,
            SteadySeconds = _seconds,
            SteadyFps = Math.Round(steadyFps, 2),
            SteadyMissedRefreshes = steadyMissed,
            SteadyFrameMaxMs = steadyEnd is null ? 0 : Math.Round(steadyEnd.Stats.FrameMax.TotalMilliseconds, 2),
            Resizes = _resizes,
            ResizesApplied = final?.Stats.Resizes,
            DeviceLost = final?.Stats.DeviceLost,
            FinalStats = final?.Stats,
            HistogramEdgesMs = RenderStats.HistogramEdgesMs,
            Pass = pass,
        };
        string json = JsonSerializer.Serialize(report, ReportOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        File.WriteAllText(_outputPath, json);
        _logger.LogInformation("render spike {Result}: steady {Fps:F1} fps, {Missed} missed refreshes, {Resizes} resizes applied; report {Path}",
            pass ? "PASS" : "FAIL", steadyFps, steadyMissed, final?.Stats.Resizes, _outputPath);
        Environment.ExitCode = pass ? 0 : 1;
        _window.Close();
    }

    private NativeRendererSnapshot? Snapshot() =>
        _window.Renderer is null ? null : new NativeRendererSnapshot(_window.Renderer.GetStats(), TimeSpan.FromTicks(Environment.TickCount64 * TimeSpan.TicksPerMillisecond));

    private static int IntArg(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    }

    private static string? StringArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private sealed record NativeRendererSnapshot(RenderStats Stats, TimeSpan Elapsed);
}
