using BenchmarkDotNet.Attributes;
using Tunqio.Core.Audio;
using Tunqio.Interop;

namespace Tunqio.Benchmarks;

/// <summary>
/// E0-S9 gate: <c>mp_engine_get_clock</c> and <c>mp_analysis_try_get_latest</c> bindings under 5 µs per call.
/// Measures the whole managed round trip (wrapper, P/Invoke, native call, DTO construction). Runs in-process so
/// the native DLLs copied next to this executable are the ones under test (a generated benchmark project would not
/// have them). Measured 2026-09-09 on the dev machine: 55.7 ns and 227 ns, 0 B allocated.
/// </summary>
[InProcess]
[MemoryDiagnoser]
public class InteropCallBenchmarks
{
    private NativeEngine _engine = null!;
    private MpAnalysisFrameBuffer _frame;

    [GlobalSetup]
    public void Setup() => _engine = NativeEngine.Create();

    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();

    [Benchmark(Description = "mp_engine_get_clock via NativeEngine.GetClock")]
    public PlaybackClock GetClock() => _engine.GetClock();

    [Benchmark(Description = "mp_analysis_try_get_latest via NativeEngine.TryGetLatestAnalysis")]
    public bool TryGetLatestAnalysis() => _engine.TryGetLatestAnalysis(ref _frame);
}
