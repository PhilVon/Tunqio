using BenchmarkDotNet.Attributes;
using Tunqio.Core.Visualization;
using Tunqio.Interop;

namespace Tunqio.Benchmarks;

/// <summary>
/// E4-S9 AC-132's number: switching preset takes under 200 ms. Here rather than in xunit because a single
/// stopwatch reading on a machine that is also building is how T-119, T-134 and T-150 went red with nothing
/// wrong; this project has retired two single-sample xunit budgets for exactly that. BenchmarkDotNet takes many
/// samples and <c>--gate</c> checks the p95, so a run is a distribution rather than one throw of a die.
/// <para>
/// <b>What is being timed.</b> The whole switch as the settings page performs it: the native loader reads the
/// preset it already has in its catalogue, runs D3DCompile over the HLSL on this thread for both stages, builds
/// the shader objects, and hands the finished preset to the render thread. That compile is nearly all of the
/// cost, and it is deliberately on the calling thread - it is what lets a preset that will not compile come back
/// as a return value instead of arriving on the render thread with nowhere to go (AC-117).
/// </para>
/// <para>
/// <b>WARP, on purpose.</b> Headless on the software rasteriser, so the figure owes nothing to the RTX 4080 in
/// this box and is closer to what the reference laptop would report than a hardware number would be. Shader
/// compilation is CPU work in any case: D3DCompile does not touch the adapter.
/// </para>
/// <para>
/// The four shipped presets are cycled rather than one switched to itself, because the real cost is compiling a
/// preset that is not already the current one, and Ambient Glow - the one screen-covering quad whose whole
/// picture is pixel-shader arithmetic - is the most expensive of them to compile.
/// </para>
/// Measured 2026-09-12 on the dev machine (i7-9700K, Windows 10 Pro 19045, headless WARP): see the report.
/// </summary>
[InProcess]
public class PresetSwitchBenchmarks : IDisposable
{
    private NativeRenderer _renderer = null!;
    private string[] _presets = [];
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _renderer = NativeRenderer.CreateHeadless(new RendererConfig(640, 360, ForceWarp: true, VSync: false));
        // The shipped catalogue, minus the compiled-in preset: what a person actually picks between.
        _presets = [.. _renderer.EnumeratePresets().Select(p => p.Id).Where(id => id != "builtin-bars")];
        if (_presets.Length < 2)
        {
            throw new InvalidOperationException(
                $"the preset root beside this executable holds {_presets.Length} preset(s); a switch benchmark needs at least two");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _renderer?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>AC-132's budget. 200 ms is what the criterion promises a person, so 200 ms is what is gated.</summary>
    [Benchmark(Description = "mp_renderer_set_preset: compile and swap one shipped preset (headless WARP)")]
    [Budget(200)]
    public string SwitchPreset()
    {
        string id = _presets[_next];
        _next = (_next + 1) % _presets.Length;
        _renderer.SetPreset(id);
        return id;
    }

    /// <summary>
    /// What the settings page does around the switch: read the catalogue and the chosen preset's parameters.
    /// Gated far tighter than the switch because neither compiles anything - if this ever approaches the switch
    /// it means enumeration has started doing work it should not.
    /// </summary>
    [Benchmark(Description = "mp_renderer_enum_presets + mp_renderer_enum_preset_params for one preset")]
    [Budget(5)]
    public int DescribePreset()
    {
        IReadOnlyList<PresetInfo> presets = _renderer.EnumeratePresets();
        return presets.Count + _renderer.EnumerateParameters(_presets[0]).Count;
    }
}
