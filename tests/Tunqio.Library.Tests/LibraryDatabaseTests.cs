using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Tunqio.FixtureGen;
using Tunqio.Library.Database;

namespace Tunqio.Library.Tests;

/// <summary>
/// E3-S1: first launch creates library.db in WAL mode at the current schema version; an unusable file is moved
/// aside and replaced with a notice; the pool hands out configured connections; open stays inside its budget.
/// </summary>
public sealed class LibraryDatabaseTests : IDisposable
{
    private static readonly TimeProvider Clock = new LibraryMigratorTests.FixedClock(1_757_376_000_000);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunqio-db-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppPaths Paths() => new(_root);

    [Fact]
    public void First_launch_creates_the_database_in_wal_mode_at_the_current_version()
    {
        AppPaths paths = Paths();
        File.Exists(paths.DatabasePath).Should().BeFalse();

        using LibraryDatabase db = LibraryDatabase.Open(paths, Clock);

        db.OpenResult.Created.Should().BeTrue();
        db.OpenResult.Recovery.Should().BeNull();
        db.OpenResult.AppliedVersions.Should().Equal(LibraryMigrations.All.Select(m => m.Version));
        db.SchemaVersion.Should().Be(LibraryMigrations.Latest);
        File.Exists(paths.DatabasePath).Should().BeTrue();

        using SqliteConnection connection = db.OpenConnection();
        Scalar(connection, "PRAGMA journal_mode").Should().Be("wal");
        Scalar(connection, "SELECT version || '|' || applied_at FROM schema_version WHERE version = 1").Should().Be("1|1757376000000", "the migrations table records v1");
        Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'track'").Should().Be(1L);
    }

    [Fact]
    public void A_second_launch_reuses_the_file_without_migrating()
    {
        AppPaths paths = Paths();
        using (LibraryDatabase first = LibraryDatabase.Open(paths, Clock))
        {
            using SqliteConnection connection = first.OpenConnection();
            Execute(connection, "INSERT INTO setting(key, value) VALUES ('ui.theme', '\"dark\"')");
        }

        using LibraryDatabase second = LibraryDatabase.Open(paths, Clock);

        second.OpenResult.Created.Should().BeFalse();
        second.OpenResult.AppliedVersions.Should().BeEmpty();
        second.OpenResult.Recovery.Should().BeNull();
        using SqliteConnection reader = second.OpenConnection();
        Scalar(reader, "SELECT value FROM setting WHERE key = 'ui.theme'").Should().Be("\"dark\"");
    }

    [Fact]
    public void A_deliberately_corrupted_file_is_moved_aside_and_a_fresh_database_created_with_a_notice()
    {
        AppPaths paths = Paths();
        paths.EnsureCreated();
        byte[] garbage = new byte[8192];
        new Random(7).NextBytes(garbage);
        File.WriteAllBytes(paths.DatabasePath, garbage);
        File.WriteAllText(paths.DatabasePath + "-wal", "stale wal");
        File.WriteAllText(paths.DatabasePath + "-shm", "stale shm");

        using LibraryDatabase db = LibraryDatabase.Open(paths, Clock);

        DatabaseRecovery recovery = db.OpenResult.Recovery.Should().NotBeNull("the shell shows this to the user").And.Subject.As<DatabaseRecovery>();
        recovery.Problem.Should().Be(LibraryDatabaseProblem.Corrupt);
        recovery.AsidePath.Should().Be(Path.Combine(paths.DataRoot, "library.corrupt-20250909000000.db"), "docs/library-and-data.md: library.corrupt-<timestamp>.db");
        recovery.Detail.Should().NotBeNullOrWhiteSpace();
        File.ReadAllBytes(recovery.AsidePath).Should().Equal(garbage, "the damaged file is kept, not deleted");
        // The stale -wal/-shm pair is not asserted on: SQLite discards an unreadable pair itself when the failed
        // connection closes, and the fresh database's own pair is memory-mapped by the pool while it is open.
        db.OpenResult.Created.Should().BeTrue();
        db.SchemaVersion.Should().Be(LibraryMigrations.Latest);
        using SqliteConnection connection = db.OpenConnection();
        Scalar(connection, "SELECT COUNT(*) FROM track").Should().Be(0L);
    }

    [Fact]
    public void A_real_database_with_a_damaged_header_is_moved_aside()
    {
        AppPaths paths = Paths();
        using (LibraryDatabase.Open(paths, Clock))
        {
        }

        SqliteConnection.ClearAllPools();
        using (FileStream stream = File.Open(paths.DatabasePath, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Write(new byte[100]); // zero the SQLite header
        }

        using LibraryDatabase db = LibraryDatabase.Open(paths, Clock);

        db.OpenResult.Recovery.Should().NotBeNull();
        db.OpenResult.Recovery!.Problem.Should().Be(LibraryDatabaseProblem.Corrupt);
        db.QuickCheck().Should().Be("ok", "the replacement is a sound database");
    }

    [Fact]
    public void A_sqlite_file_that_is_not_a_library_is_moved_aside_as_unrecognised()
    {
        AppPaths paths = Paths();
        paths.EnsureCreated();
        using (var foreign = new SqliteConnection($"Data Source={paths.DatabasePath}"))
        {
            foreign.Open();
            Execute(foreign, "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)");
        }

        SqliteConnection.ClearAllPools();
        using LibraryDatabase db = LibraryDatabase.Open(paths, Clock);

        db.OpenResult.Recovery!.Problem.Should().Be(LibraryDatabaseProblem.Unrecognised);
        Directory.GetFiles(paths.DataRoot, "library.corrupt-*.db").Should().HaveCount(1);
        db.SchemaVersion.Should().Be(LibraryMigrations.Latest);
    }

    [Fact]
    public void Two_recoveries_in_the_same_second_keep_both_files()
    {
        AppPaths paths = Paths();
        paths.EnsureCreated();
        for (int i = 0; i < 2; i++)
        {
            SqliteConnection.ClearAllPools();
            File.WriteAllText(paths.DatabasePath, "not a database " + i);
            using LibraryDatabase db = LibraryDatabase.Open(paths, Clock);
            db.OpenResult.Recovery.Should().NotBeNull();
        }

        Directory.GetFiles(paths.DataRoot, "library.corrupt-*.db").Should().HaveCount(2);
    }

    [Fact]
    public void A_database_from_a_newer_build_is_refused_and_left_in_place()
    {
        AppPaths paths = Paths();
        using (LibraryDatabase db = LibraryDatabase.Open(paths, Clock))
        {
            using SqliteConnection connection = db.OpenConnection();
            Execute(connection, "INSERT INTO schema_version(version, applied_at) VALUES (999, 0)");
        }

        Action open = () => LibraryDatabase.Open(paths, Clock);

        open.Should().Throw<LibraryDatabaseException>().Which.Problem.Should().Be(LibraryDatabaseProblem.NewerVersion);
        File.Exists(paths.DatabasePath).Should().BeTrue("user data from a newer build is never moved aside");
        Directory.GetFiles(paths.DataRoot, "library.corrupt-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Pooled_connections_enforce_foreign_keys_and_use_normal_synchronous_Async()
    {
        using LibraryDatabase db = LibraryDatabase.Open(Paths(), Clock);

        using (SqliteConnection connection = db.OpenConnection())
        {
            Scalar(connection, "PRAGMA foreign_keys").Should().Be(1L);
            Scalar(connection, "PRAGMA synchronous").Should().Be(1L, "1 = NORMAL");
            Action orphan = () => Execute(connection, "INSERT INTO track_genre(track_id, genre_id) VALUES (42, 42)");
            orphan.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(19, "SQLITE_CONSTRAINT: foreign keys are enforced");
        }

        await using SqliteConnection async = await db.OpenConnectionAsync();
        Scalar(async, "PRAGMA foreign_keys").Should().Be(1L);
    }

    [Fact]
    public async Task Readers_run_beside_a_writer_and_writers_are_serialised_Async()
    {
        using LibraryDatabase db = LibraryDatabase.Open(Paths(), Clock);

        using IDisposable lease = await db.AcquireWriterAsync();
        using (SqliteConnection writer = db.OpenConnection())
        {
            using SqliteTransaction tx = writer.BeginTransaction();
            Execute(writer, "INSERT INTO genre(name) VALUES ('Jazz')", tx);
            using SqliteConnection reader = db.OpenConnection();
            Scalar(reader, "SELECT COUNT(*) FROM genre").Should().Be(0L, "WAL readers see the last committed state while a writer is open");
            tx.Commit();
        }

        Task<IDisposable> second = db.AcquireWriterAsync();
        (await Task.WhenAny(second, Task.Delay(200))).Should().NotBe(second, "the second writer waits for the lease");
        lease.Dispose();
        using IDisposable next = await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void An_in_memory_database_is_shared_between_its_connections()
    {
        using LibraryDatabase db = LibraryDatabase.OpenInMemory(clock: Clock);

        db.SchemaVersion.Should().Be(LibraryMigrations.Latest);
        using (SqliteConnection writer = db.OpenConnection())
        {
            Execute(writer, "INSERT INTO genre(name) VALUES ('Ambient')");
        }

        using SqliteConnection reader = db.OpenConnection();
        Scalar(reader, "SELECT name FROM genre").Should().Be("Ambient");
    }

    [Fact]
    public void Opening_a_large_library_stays_inside_the_500ms_budget()
    {
        // docs/build-test-release.md: "Library open < 500 ms" is a PR gate, asserted properly by
        // Tunqio.Benchmarks (--gate, a CI step) where the machine is not also running the rest of the suite.
        //
        // The first open here is not that measurement and never was. Library100kBuilder writes with
        // PRAGMA journal_mode = OFF, so the database it leaves is not in WAL, and the first Open runs
        // PRAGMA journal_mode = WAL - a one-time conversion of the whole file. That conversion was what the old
        // 500 ms assertion timed: it is storage speed on a 25k-row file, not a library open, which is why it
        // came back 506 ms and then 685 ms the first two times CI ever reached this test (2026-09-11) on a
        // database nobody had touched. No real library takes that path; one is created by Open itself, which
        // sets WAL at creation.
        //
        // So the conversion happens first and the measurement is the open a user actually waits for. The bound
        // is deliberately loose - the gate is the benchmark; this is here to catch an open that has become
        // pathological without waiting for the benchmark step.
        AppPaths paths = Paths();
        paths.EnsureCreated();
        Library100kBuilder.Build(paths.DatabasePath, 25_000, FixtureLibraryBuilder.Seed, TextWriter.Null);
        using (LibraryDatabase.Open(paths, Clock))
        {
        }

        SqliteConnection.ClearAllPools();

        Stopwatch stopwatch = Stopwatch.StartNew();
        using LibraryDatabase db = LibraryDatabase.Open(paths, Clock);
        stopwatch.Stop();

        db.OpenResult.Created.Should().BeFalse();
        db.OpenResult.AppliedVersions.Should().BeEmpty();
        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500), $"open reported {db.OpenResult.Elapsed.TotalMilliseconds:F0} ms internally");
    }

    [Fact]
    public void Dispose_releases_the_file()
    {
        AppPaths paths = Paths();
        LibraryDatabase db = LibraryDatabase.Open(paths, Clock);
        using (db.OpenConnection())
        {
        }

        db.Dispose();

        Action delete = () => File.Delete(paths.DatabasePath);
        delete.Should().NotThrow("no pooled connection holds the file after Dispose");
        Action use = () => db.OpenConnection();
        use.Should().Throw<ObjectDisposedException>();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }
}
