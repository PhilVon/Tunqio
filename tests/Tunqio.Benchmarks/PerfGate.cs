using System.Globalization;
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
/// <para>
/// <c>TUNQIO_PERF_SLACK</c> multiplies every budget for a machine that is not the reference machine. A budget
/// in the source is the claim made to users, measured on the dev machine; a shared CI runner is not that
/// machine and does not hold still. The same <c>UpsertBenchmarks.UpsertBatchAsync</c> on identical code came
/// back with medians of 50.5, 63.7 and 173.8 ms across three <c>windows-2025-vs2026</c> runs — a 3.4× spread
/// that is the disk, not the code (docs/spikes/wal-checkpoint-during-scan.md). Widening the number in the
/// source to survive that would quietly weaken the claim everywhere; the slack lives where the weaker machine
/// is instead, set only by ci.yml, and both numbers are printed so a run creeping towards the real budget is
/// still visible.
/// </para>
/// </summary>
internal static class PerfGate
{
    /// <summary>Set by ci.yml. Absent everywhere else, so a local <c>--gate</c> checks the real budgets.</summary>
    private const string SlackVariable = "TUNQIO_PERF_SLACK";

    /// <summary>
    /// Above this a slack is not slack, it is a gate that cannot fail — far likelier a typo (40 for 4) than an
    /// intent, and a typo that would read as green forever.
    /// </summary>
    private const double MaxSlack = 10;

    public static int Check(IEnumerable<Summary> summaries, TextWriter log)
    {
        int gated = 0;
        int over = 0;
        log.WriteLine();
        log.WriteLine("Performance gate");

        if (!TryReadSlack(out double slack, out string? slackError))
        {
            log.WriteLine($"  FAIL {slackError}");
            return 1;
        }

        if (slack != 1)
        {
            log.WriteLine($"  budgets ×{slack:0.##} ({SlackVariable}): this is not the reference machine");
        }

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
                double effective = budget.P95Milliseconds * slack;
                bool inside = p95 < effective;
                over += inside ? 0 : 1;

                // The claim's own number is always the one named; the slackened one only when it is in force,
                // so a p95 that has crept past the real budget can be read off a green CI run.
                string against = slack == 1
                    ? $"a budget of {budget.P95Milliseconds:F0} ms"
                    : $"{effective:F0} ms (a budget of {budget.P95Milliseconds:F0} ms ×{slack:0.##})";
                log.WriteLine(
                    $"  {(inside ? "ok  " : "OVER")} {name}: p95 {p95:F1} ms against {against} " +
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

    /// <summary>
    /// The multiplier from <c>TUNQIO_PERF_SLACK</c>, or 1 when it is not set. Anything else set there is a
    /// failure rather than a fallback to 1: a gate that quietly ignored a value it could not read would be a
    /// gate nobody could trust, and the ways of getting this wrong all read as green.
    /// </summary>
    private static bool TryReadSlack(out double slack, out string? error)
    {
        slack = 1;
        error = null;
        string? raw = Environment.GetEnvironmentVariable(SlackVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            error = $"{SlackVariable} is '{raw}', which is not a number";
            return false;
        }

        if (!double.IsFinite(parsed) || parsed <= 0)
        {
            error = $"{SlackVariable} is {parsed}, which is not a positive multiplier";
            return false;
        }

        if (parsed > MaxSlack)
        {
            error = $"{SlackVariable} is {parsed}, above the {MaxSlack:F0}× ceiling: a budget that loose gates nothing";
            return false;
        }

        slack = parsed;
        return true;
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
