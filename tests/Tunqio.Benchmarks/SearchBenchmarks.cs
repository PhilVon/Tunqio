using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Perfolizer.Mathematics.OutlierDetection;
using Tunqio.Core.Library;
using Tunqio.Library.Database;
using Tunqio.Library.Repositories;

namespace Tunqio.Benchmarks;

/// <summary>
/// E3-S9 gate: a three-character query on the 100k database under 50 ms p95 (docs/product-scope.md). This is
/// where that claim is asserted (<c>--gate</c>, see <see cref="PerfGate"/>); the xunit companion in
/// <c>Tunqio.Library.Tests</c> measures the same searches beside the rest of the suite, where only the median
/// survives the contention. Uses <c>tests/fixtures/library-100k.db</c> when it exists (see
/// tests/fixtures/README.md), else generates the same database into a temporary folder. Measured 2026-09-09 on
/// the dev machine (Release, in-process): "ren" 13.5 ms, "the" 12.9 ms, "kalo" 13.9 ms, "the mi" 18.4 ms mean,
/// about 45 KB allocated per search. Most of the floor is fixed cost (a connection from the pool and three
/// statement compilations), not the FTS walk, which the SQL-level probes put at 1 to 4 ms per group.
/// </summary>
[Config(typeof(LatencyConfig))]
public class SearchBenchmarks
{
    private string? _temporary;
    private LibraryDatabase _db = null!;
    private SqliteSearchService _search = null!;

    [Params("ren", "the", "kalo", "the mi")]
    public string Text { get; set; } = "ren";

    [GlobalSetup]
    public void Setup()
    {
        (string path, _temporary) = BenchmarkLibrary.Acquire(Console.Out);
        _db = LibraryDatabase.Open(path);
        _search = new SqliteSearchService(_db);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        BenchmarkLibrary.Delete(_temporary);
    }

    [Benchmark(Description = "ISearchService.SearchAsync, default limits")]
    [Budget(50)]
    public Task<SearchResults> SearchAsync() => _search.SearchAsync(Text, SearchLimits.Default);


    /// <summary>
    /// A p95 of one search, not of a batch average: the default throughput strategy times many invocations per
    /// iteration and reports percentiles over those means, which is exactly the tail this claim is about. So one
    /// invocation per iteration (<see cref="RunStrategy.Monitoring"/>) over 100 iterations, with the outliers
    /// kept — discarding them would discard the claim. In-process so the native DLLs copied next to this
    /// executable are the ones under test (a generated benchmark project would not have them).
    /// </summary>
    private sealed class LatencyConfig : ManualConfig
    {
        public LatencyConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessEmitToolchain.Instance)
                .WithStrategy(RunStrategy.Monitoring)
                .WithWarmupCount(5)
                .WithIterationCount(100)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithOutlierMode(OutlierMode.DontRemove));
            AddDiagnoser(MemoryDiagnoser.Default);
            AddColumn(StatisticColumn.Median, StatisticColumn.P95, StatisticColumn.Max);
        }
    }
}
