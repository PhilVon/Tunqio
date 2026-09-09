using BenchmarkDotNet.Attributes;
using Tunqio.Core.Library;
using Tunqio.FixtureGen;
using Tunqio.Library.Database;
using Tunqio.Library.Repositories;

namespace Tunqio.Benchmarks;

/// <summary>
/// E3-S9 gate: a three-character query on the 100k database under 50 ms p95 (docs/product-scope.md). Uses
/// <c>tests/fixtures/library-100k.db</c> when it exists (see tests/fixtures/README.md), else generates the same
/// database into a temporary folder. Measured 2026-09-09 on the dev machine (Release, in-process): "ren"
/// 13.5 ms, "the" 12.9 ms, "kalo" 13.9 ms, "the mi" 18.4 ms mean, about 45 KB allocated per search; the
/// xunit gate over a freshly generated database saw 15 to 21 ms at p95. Most of the floor is fixed cost
/// (a connection from the pool and three statement compilations), not the FTS walk, which the SQL-level
/// probes put at 1 to 4 ms per group.
/// </summary>
[InProcess]
[MemoryDiagnoser]
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
        string path = FixturePath();
        if (!File.Exists(path))
        {
            _temporary = Path.Combine(Path.GetTempPath(), "tunqio-bench-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporary);
            path = Path.Combine(_temporary, "library-100k.db");
            Library100kBuilder.Build(path, 100_000, FixtureLibraryBuilder.Seed, Console.Out);
        }

        _db = LibraryDatabase.Open(path);
        _search = new SqliteSearchService(_db);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (_temporary is not null && Directory.Exists(_temporary))
        {
            Directory.Delete(_temporary, recursive: true);
        }
    }

    [Benchmark(Description = "ISearchService.SearchAsync, default limits")]
    public Task<SearchResults> SearchAsync() => _search.SearchAsync(Text, SearchLimits.Default);

    private static string FixturePath()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Tunqio.sln")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return Path.Combine(dir ?? ".", "tests", "fixtures", "library-100k.db");
    }
}
