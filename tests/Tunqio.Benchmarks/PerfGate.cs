using System.Reflection;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Tunqio.Benchmarks;

/// <summary>
/// The threshold a benchmark's claim is gated on (docs/build-test-release.md, "Performance verification").
/// A benchmark without one is measured but not gated.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class BudgetAttribute : Attribute
{
    /// <param name="p95Milliseconds">The claim's p95 wall-clock budget for one invocation.</param>
    public BudgetAttribute(double p95Milliseconds) => P95Milliseconds = p95Milliseconds;

    public double P95Milliseconds { get; }
}

/// <summary>
/// Turns the <see cref="BudgetAttribute"/>s of a run into a pass or fail, so <c>--gate</c> can be a PR step
/// (docs/build-test-release.md, "Continuous integration", step 7). A run that gated nothing fails too: a
/// mistyped <c>--filter</c> must not read as a green gate.
/// </summary>
internal static class PerfGate
{
    public static int Check(IEnumerable<Summary> summaries, TextWriter log)
    {
        int gated = 0;
        int over = 0;
        log.WriteLine();
        log.WriteLine("Performance gate");

        foreach (Summary summary in summaries)
        {
            foreach (BenchmarkReport report in summary.Reports)
            {
                BudgetAttribute? budget = report.BenchmarkCase.Descriptor.WorkloadMethod.GetCustomAttribute<BudgetAttribute>();
                if (budget is null)
                {
                    continue;
                }

                gated++;
                string name = Name(report.BenchmarkCase);
                Statistics? statistics = report.ResultStatistics;
                if (statistics is null)
                {
                    log.WriteLine($"  FAIL {name}: no result (the benchmark did not complete)");
                    over++;
                    continue;
                }

                double p95 = Milliseconds(statistics.Percentiles.P95);
                bool inside = p95 < budget.P95Milliseconds;
                over += inside ? 0 : 1;
                log.WriteLine(
                    $"  {(inside ? "ok  " : "OVER")} {name}: p95 {p95:F1} ms against a budget of {budget.P95Milliseconds:F0} ms " +
                    $"(median {Milliseconds(statistics.Median):F1} ms, max {Milliseconds(statistics.Max):F1} ms, n = {statistics.N})");
            }
        }

        if (gated == 0)
        {
            log.WriteLine("  FAIL nothing was gated: no benchmark in this run carries a [Budget]");
            return 1;
        }

        log.WriteLine($"  {gated} gated, {over} over budget");
        return over == 0 ? 0 : 1;
    }

    /// <summary>The method and its parameters; <c>DisplayInfo</c> would carry the whole job description too.</summary>
    private static string Name(BenchmarkCase benchmark)
    {
        string parameters = benchmark.Parameters.DisplayInfo;
        return $"{benchmark.Descriptor.Type.Name}.{benchmark.Descriptor.WorkloadMethod.Name}{parameters}";
    }

    /// <summary>BenchmarkDotNet's statistics are nanoseconds.</summary>
    private static double Milliseconds(double nanoseconds) => nanoseconds / 1_000_000d;
}
