using BenchmarkDotNet.Attributes;
using Tunqio.Core.Audio;
using Tunqio.Core.Visualization;
using Tunqio.Interop;

namespace Tunqio.Benchmarks;

/// <summary>
/// E0-S9 and E4-S1 (AC-114) gate: <c>mp_engine_get_clock</c> and <c>mp_analysis_try_get_latest</c> bindings under
/// 5 µs per call. Measures the whole managed round trip (wrapper, P/Invoke, native call, DTO construction). Runs
/// in-process so the native DLLs copied next to this executable are the ones under test (a generated benchmark
/// project would not have them).
/// <para>
/// The analysis benchmarks play a track and render a second of audio in setup, so they measure the copy of a
/// real published frame. Measuring the empty engine would have measured the refusal path - a null check and a
/// return - and called it the cost of the binding.
/// </para>
/// Measured 2026-09-11 on the dev machine (i7-9700K, not the reference laptop): clock 143 ns,
/// <c>mp_analysis_try_get_latest</c> 72 ns and no allocation, <c>IAnalysisFrameSource.TryGetLatest</c> 1.67 µs
/// and 6256 B - the arrays of the DTO, which is what a caller keeping a frame is paying for. The clock moved
/// from the 55.7 ns measured in E0-S9 because the engine here is playing: an idle engine's get_clock returns
/// before it asks BASS anything, and timing that would have been timing the early return.
/// </summary>
[InProcess]
[MemoryDiagnoser]
public class InteropCallBenchmarks : IDisposable
{
    private NativeEngine _engine = null!;
    private NativeTrack _track = null!;
    private NativeAnalysisFrameSource _source = null!;
    private MpAnalysisFrameBuffer _frame;

    [GlobalSetup]
    public void Setup()
    {
        _engine = NativeEngine.Create();
        _engine.SetOutput(new OutputConfig(OutputConfig.NoDevice));
        _source = new NativeAnalysisFrameSource(_engine);
        _track = _engine.OpenTrack(SineWav.Write("interop-analysis", seconds: 2.0));
        _engine.Play(_track);

        var buffer = new float[480 * _engine.MixerChannels];
        for (int i = 0; i < 100; i++) // 1 s: far more hops than the analysis thread needs to have published one
        {
            _engine.Render(buffer, 480);
        }

        // The analysis thread polls the tap, so the first frame is not instant.
        for (int i = 0; i < 2000 && !_source.TryGetLatest(out _); i++)
        {
            Thread.Sleep(1);
        }

        if (!_source.TryGetLatest(out _))
        {
            throw new InvalidOperationException("no analysis frame to measure: the analysis thread published none");
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    /// <summary>Same as <see cref="Cleanup"/>: BenchmarkDotNet calls that, and CA1001 wants this.</summary>
    public void Dispose()
    {
        _source?.Dispose();
        _track?.Dispose();
        _engine?.Dispose();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "mp_engine_get_clock via NativeEngine.GetClock")]
    [Budget(0.005)]
    public PlaybackClock GetClock() => _engine.GetClock();

    [Benchmark(Description = "mp_analysis_try_get_latest via NativeEngine.TryGetLatestAnalysis")]
    [Budget(0.005)]
    public bool TryGetLatestAnalysis() => _engine.TryGetLatestAnalysis(ref _frame);

    /// <summary>AC-114's managed half: the copy a consumer of <c>IAnalysisFrameSource</c> actually pays for.</summary>
    [Benchmark(Description = "IAnalysisFrameSource.TryGetLatest (native copy plus the DTO)")]
    [Budget(0.005)]
    public bool TryGetLatestFrame() => _source.TryGetLatest(out AnalysisFrame _);
}
