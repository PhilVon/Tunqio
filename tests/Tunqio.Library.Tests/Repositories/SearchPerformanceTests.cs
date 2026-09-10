using System.Diagnostics;
using Tunqio.Core.Library;
using Tunqio.Library.Repositories;
using Xunit.Abstractions;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>
/// E3-S9 gate (docs/product-scope.md, "Performance targets"): a three-character query on the 100k database
/// returns in under 50 ms at p95.
/// <para>
/// The p95 half of that claim is asserted by <c>Tunqio.Benchmarks</c> (<c>--gate</c>, a CI step), which owns
/// the machine while it measures. This class measures the same searches inside the ordinary test run, where
/// three other test projects and the rest of this assembly share eight cores, and asserts the budget on the
/// <em>median</em> — because the tail here is the scheduler's, not the query's. Measured 2026-09-10 on the dev
/// machine (Release): alone, p95 15 to 22 ms and median 13 to 18 ms; during <c>dotnet test Tunqio.Managed.slnf</c>
/// the same code ran p95 36 to 58 ms with a median of 22 to 32 ms, so the old p95 assertion failed about half
/// the time on a query that had not changed. Raising the number would have hidden a regression; the median at
/// the product's own 50 ms still fails on one, and it does not move when the machine is busy.
/// </para>
/// </summary>
[Collection(Library100kFixture.Collection)]
public sealed class SearchPerformanceTests
{
    private const int Runs = 100;

    /// <summary>docs/product-scope.md: search on a 100k library is interactive.</summary>
    private const double BudgetMs = 50;

    private readonly Library100kFixture _fixture;
    private readonly ITestOutputHelper _output;

    public SearchPerformanceTests(Library100kFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// "ren" is the noisiest three-character query of the synthetic names (41k of 100k tracks); "the" hits
    /// 15k rows, all of them through the artist columns; "kalo" is a rarer term whose ranking sorts the whole
    /// candidate set; "the mi" adds a short term the index cannot answer on top of the common one.
    /// </summary>
    [Theory]
    [InlineData("ren")]
    [InlineData("the")]
    [InlineData("kalo")]
    [InlineData("the mi")]
    public async Task A_query_on_the_100k_database_keeps_its_median_inside_the_50ms_budget_Async(string text)
    {
        var search = new SqliteSearchService(_fixture.Db);
        SearchResults warm = await search.SearchAsync(text, SearchLimits.Default);
        warm.Tracks.Should().NotBeEmpty();

        var samples = new double[Runs];
        for (int i = 0; i < Runs; i++)
        {
            Stopwatch timed = Stopwatch.StartNew();
            await search.SearchAsync(text, SearchLimits.Default);
            samples[i] = timed.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        double median = Percentile(samples, 0.50);
        string measured =
            $"'{text}' over {Runs} runs: min {samples[0]:F1} ms, median {median:F1} ms, " +
            $"p95 {Percentile(samples, 0.95):F1} ms, max {samples[^1]:F1} ms";
        _output.WriteLine(measured);

        median.Should().BeLessThan(BudgetMs, measured + " (the p95 gate is the benchmark's: dotnet run -c Release -p:Platform=x64 --project tests/Tunqio.Benchmarks -- --gate --filter *SearchBenchmarks*)");
    }

    [Fact]
    public async Task Rebuilding_the_index_over_100k_tracks_is_a_repair_not_a_hang_Async()
    {
        var search = new SqliteSearchService(_fixture.Db);
        Stopwatch timed = Stopwatch.StartNew();
        int indexed = await search.RebuildIndexAsync();
        timed.Stop();

        _output.WriteLine($"rebuild of {indexed} rows: {timed.ElapsedMilliseconds} ms");
        indexed.Should().BeGreaterThanOrEqualTo(100_000);
        timed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), $"rebuild took {timed.ElapsedMilliseconds} ms");
        (await search.SearchAsync("ren", SearchLimits.Default)).Tracks.Should().NotBeEmpty();
    }

    private static double Percentile(double[] sorted, double p) => sorted[(int)Math.Ceiling(sorted.Length * p) - 1];
}
