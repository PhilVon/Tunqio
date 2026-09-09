using System.Diagnostics;
using Tunqio.Core.Library;
using Tunqio.Library.Repositories;
using Xunit.Abstractions;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>
/// E3-S9 gate (docs/product-scope.md, "Performance targets"): a three-character query on the 100k database
/// returns in under 50 ms at p95. The same measurement runs under BenchmarkDotNet in <c>Tunqio.Benchmarks</c>;
/// this is the PR-gate form, which E3-S13 points at the committed fixture with the other thresholds.
/// </summary>
[Collection(Library100kFixture.Collection)]
public sealed class SearchPerformanceTests
{
    private const int Runs = 100;

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
    public async Task A_query_on_the_100k_database_returns_within_50ms_at_p95_Async(string text)
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
        double p95 = samples[(int)Math.Ceiling(Runs * 0.95) - 1];
        double median = samples[Runs / 2];
        _output.WriteLine($"'{text}': p95 {p95:F2} ms, median {median:F2} ms, max {samples[^1]:F2} ms over {Runs} runs");
        p95.Should().BeLessThan(50, $"'{text}': p95 {p95:F1} ms, median {median:F1} ms, max {samples[^1]:F1} ms");
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
}
