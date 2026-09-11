using System.Diagnostics;
using Tunqio.Core.Library;
using Tunqio.Library.Repositories;
using Xunit.Abstractions;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>
/// E3-S9 (docs/product-scope.md, "Performance targets"): a three-character query on the 100k database returns
/// in under 50 ms at p95.
/// <para>
/// That claim is asserted by <c>Tunqio.Benchmarks</c> (<c>--gate</c>, a CI step), which owns the machine while
/// it measures and carries <c>TUNQIO_PERF_SLACK</c> for hardware that is not the reference machine. It is not
/// asserted here, and the history of this file is why. The p95 went first: measured 2026-09-10 on the dev
/// machine, alone it ran p95 15 to 22 ms, and during <c>dotnet test Tunqio.Managed.slnf</c> the same unchanged
/// query ran 36 to 58 ms, failing about half the time. It was moved to the median on the reasoning that the
/// tail here is the scheduler's and the median "does not move when the machine is busy". The median moved:
/// 2026-09-11, Debug, this project running alone, "the mi" came back with a median of 55.4 ms against the
/// 50 ms bound (min 19.7, p95 113.2, max 160.1), one run in six (T-110).
/// </para>
/// <para>
/// So what is left here is a smoke bound, chosen to be one rather than a budget in disguise — the same
/// division 187609f drew for the upsert and first-Tracks-page bounds. Against a dev median of 13 to 18 ms and
/// a worst observed sample of 160 ms on a loaded machine, two seconds says something has gone wrong rather
/// than something is busy. The measurement itself is still printed, because a developer reading it is the
/// point of running this without the gate.
/// </para>
/// </summary>
[Collection(Library100kFixture.Collection)]
public sealed class SearchPerformanceTests
{
    private const int Runs = 100;

    /// <summary>
    /// Not the product's budget — that is 50 ms at p95 and it is gated in <c>Tunqio.Benchmarks</c>. This is the
    /// value a query has to reach before it is broken rather than unlucky, roughly two orders of magnitude
    /// above the dev-machine median and an order above the worst sample seen on a loaded one.
    /// </summary>
    private const double SmokeBoundMs = 2000;

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
    public async Task A_query_on_the_100k_database_returns_rows_and_stays_far_inside_a_smoke_bound_Async(string text)
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

        median.Should().BeLessThan(SmokeBoundMs, measured + " (the 50 ms budget is the benchmark's: dotnet run -c Release -p:Platform=x64 --project tests/Tunqio.Benchmarks -- --gate --filter *SearchBenchmarks*)");
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
