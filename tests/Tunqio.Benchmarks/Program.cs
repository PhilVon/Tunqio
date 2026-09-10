using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace Tunqio.Benchmarks;

internal static class Program
{
    /// <summary>
    /// The usual BenchmarkDotNet switcher, plus <c>--gate</c>: every benchmark carrying a
    /// <see cref="BudgetAttribute"/> is checked against its budget and a breach exits non-zero, which is what
    /// makes a benchmark a PR gate rather than a report (docs/build-test-release.md, "Performance verification").
    /// </summary>
    private static int Main(string[] args)
    {
        const string Gate = "--gate";
        bool gate = args.Any(a => string.Equals(a, Gate, StringComparison.OrdinalIgnoreCase));
        string[] forSwitcher = args.Where(a => !string.Equals(a, Gate, StringComparison.OrdinalIgnoreCase)).ToArray();

        IEnumerable<Summary> summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(forSwitcher);
        return gate ? PerfGate.Check(summaries, Console.Out) : 0;
    }
}
