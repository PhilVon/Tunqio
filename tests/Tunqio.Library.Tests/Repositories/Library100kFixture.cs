using Microsoft.Data.Sqlite;
using Tunqio.FixtureGen;
using Tunqio.Library.Database;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>
/// The 100k-track database the performance gates run against (docs/build-test-release.md, "Performance
/// verification"), generated once per test run into a temporary profile (about ten seconds) and shared by
/// every class in the <see cref="Collection"/>. Tests may write to it; the gates measure warm, steady-state
/// operations, not the first touch of a cold file.
/// </summary>
public sealed class Library100kFixture : IDisposable
{
    public const string Collection = "library-100k";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunqio-perf-" + Guid.NewGuid().ToString("N"));

    public Library100kFixture()
    {
        Paths = new AppPaths(_root);
        Paths.EnsureCreated();
        Library100kBuilder.Build(Paths.DatabasePath, 100_000, FixtureLibraryBuilder.Seed, TextWriter.Null);
        SqliteConnection.ClearAllPools();
        Db = LibraryDatabase.Open(Paths);
    }

    public AppPaths Paths { get; }

    public LibraryDatabase Db { get; }

    public void Dispose()
    {
        Db.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}

[CollectionDefinition(Library100kFixture.Collection)]
public sealed class Library100kDatabase : ICollectionFixture<Library100kFixture>
{
}
