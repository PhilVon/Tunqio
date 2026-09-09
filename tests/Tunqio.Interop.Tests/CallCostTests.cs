using System.Diagnostics;

namespace Tunqio.Interop.Tests;

/// <summary>
/// Smoke check for AC-32 (the measured number comes from Tunqio.Benchmarks): the two hot bindings must stay in
/// the microsecond range. Bounds are loose because this runs on shared CI hardware and in Debug.
/// </summary>
[Collection("native engine")]
public class CallCostTests
{
    [Fact]
    public void Clock_and_analysis_bindings_cost_microseconds_not_milliseconds()
    {
        using NativeEngine engine = NativeEngine.Create();
        var frame = default(MpAnalysisFrameBuffer);
        for (int i = 0; i < 2000; i++)
        {
            _ = engine.GetClock();
            _ = engine.TryGetLatestAnalysis(ref frame);
        }

        const int iterations = 50_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            _ = engine.GetClock();
        }

        double clockUs = sw.Elapsed.TotalMilliseconds * 1000 / iterations;

        sw.Restart();
        for (int i = 0; i < iterations; i++)
        {
            _ = engine.TryGetLatestAnalysis(ref frame);
        }

        double analysisUs = sw.Elapsed.TotalMilliseconds * 1000 / iterations;

        clockUs.Should().BeLessThan(25, "mp_engine_get_clock binding (target < 5 µs in Release; measured by BenchmarkDotNet)");
        analysisUs.Should().BeLessThan(25, "mp_analysis_try_get_latest binding (target < 5 µs in Release)");
    }
}
