using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Tunqio.Library.Database;

namespace Tunqio.Library.Tests;

/// <summary>
/// E3-S1: every migration runs from an empty database and from each prior version's frozen fixture
/// (tests/fixtures/schema/v{N}.sql), always ending with the schema a fresh database gets.
/// </summary>
public class LibraryMigratorTests
{
    private static readonly TimeProvider Clock = new FixedClock(1_757_376_000_000);

    private static string FixtureDirectory => RepoPaths.File("tests", "fixtures", "schema");

    public static TheoryData<int> ShippedVersions()
    {
        var data = new TheoryData<int>();
        foreach (LibraryMigration migration in LibraryMigrations.All)
        {
            data.Add(migration.Version);
        }

        return data;
    }

    [Fact]
    public void Migrations_are_contiguous_from_version_1()
    {
        LibraryMigrations.All.Select(m => m.Version).Should().Equal(Enumerable.Range(1, LibraryMigrations.All.Count));
        LibraryMigrations.Latest.Should().Be(LibraryMigrations.All.Count);
        LibraryAssembly.SchemaVersion.Should().Be(LibraryMigrations.Latest);
    }

    [Fact]
    public void An_empty_database_is_version_0_and_migrating_it_records_every_version()
    {
        using SqliteConnection connection = OpenMemory();
        LibraryMigrator.CurrentVersion(connection).Should().Be(0);

        IReadOnlyList<LibraryMigration> applied = LibraryMigrator.Apply(connection, Clock);

        applied.Select(m => m.Version).Should().Equal(LibraryMigrations.All.Select(m => m.Version));
        LibraryMigrator.CurrentVersion(connection).Should().Be(LibraryMigrations.Latest);
        Rows(connection, "SELECT version, applied_at FROM schema_version ORDER BY version")
            .Should().Equal(LibraryMigrations.All.Select(m => $"{m.Version}|1757376000000"));
        LibraryMigrator.Apply(connection, Clock).Should().BeEmpty("a current database has nothing pending");
    }

    [Theory]
    [MemberData(nameof(ShippedVersions))]
    public void Every_shipped_version_has_a_frozen_fixture_that_matches_what_the_migrations_produce(int version)
    {
        string fixture = Path.Combine(FixtureDirectory, $"v{version}.sql");
        File.Exists(fixture).Should().BeTrue($"migration {version} shipped, so tests/fixtures/schema/v{version}.sql must snapshot its schema");

        using SqliteConnection fromFixture = OpenMemory();
        Execute(fromFixture, File.ReadAllText(fixture));
        LibraryMigrator.CurrentVersion(fromFixture).Should().Be(version);

        using SqliteConnection fromMigrations = OpenMemory();
        foreach (LibraryMigration migration in LibraryMigrations.All.Where(m => m.Version <= version))
        {
            Execute(fromMigrations, migration.Sql);
        }

        Schema(fromFixture).Should().Equal(Schema(fromMigrations), $"v{version}.sql is a frozen copy of the DDL at version {version}; a shipped migration must not be edited in place");
    }

    [Theory]
    [MemberData(nameof(ShippedVersions))]
    public void Every_prior_version_fixture_migrates_to_the_latest_schema_with_its_rows_intact(int version)
    {
        using SqliteConnection fixture = OpenMemory();
        Execute(fixture, File.ReadAllText(Path.Combine(FixtureDirectory, $"v{version}.sql")));
        List<string> tables = Rows(fixture, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE 'track_fts%' ORDER BY name");
        var rowCounts = tables.ToDictionary(t => t, t => (long)Scalar(fixture, $"SELECT COUNT(*) FROM \"{t}\"")!);
        rowCounts.Values.Should().OnlyContain(n => n > 0, "the fixture seeds every table so migrations are exercised against data");

        IReadOnlyList<LibraryMigration> applied = LibraryMigrator.Apply(fixture, Clock);

        applied.Select(m => m.Version).Should().Equal(LibraryMigrations.All.Where(m => m.Version > version).Select(m => m.Version));
        LibraryMigrator.CurrentVersion(fixture).Should().Be(LibraryMigrations.Latest);
        using SqliteConnection fresh = OpenMemory();
        LibraryMigrator.Apply(fresh, Clock);
        Schema(fixture).Should().Equal(Schema(fresh), "a migrated database must end with exactly the fresh schema");
        foreach ((string table, long count) in rowCounts)
        {
            if (table != "schema_version")
            {
                Scalar(fixture, $"SELECT COUNT(*) FROM \"{table}\"").Should().Be(count, $"{table} rows survive migration");
            }
        }

        Scalar(fixture, "PRAGMA foreign_key_check").Should().BeNull("no orphan rows after migration");
    }

    [Fact]
    public void A_database_from_a_newer_build_is_refused()
    {
        using SqliteConnection connection = OpenMemory();
        LibraryMigrator.Apply(connection, Clock);
        Execute(connection, "INSERT INTO schema_version(version, applied_at) VALUES (999, 0)");

        Action apply = () => LibraryMigrator.Apply(connection, Clock);

        apply.Should().Throw<LibraryDatabaseException>().Which.Problem.Should().Be(LibraryDatabaseProblem.NewerVersion);
    }

    [Fact]
    public void A_sqlite_file_that_is_not_a_library_is_reported_as_unrecognised()
    {
        using SqliteConnection connection = OpenMemory();
        Execute(connection, "CREATE TABLE something_else (id INTEGER PRIMARY KEY)");

        Action read = () => LibraryMigrator.CurrentVersion(connection);

        read.Should().Throw<LibraryDatabaseException>().Which.Problem.Should().Be(LibraryDatabaseProblem.Unrecognised);
    }

    private static SqliteConnection OpenMemory()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        return connection;
    }

    /// <summary>Every object in sqlite_master (tables, indexes, FTS shadow tables) with whitespace-normalised DDL.</summary>
    private static string[] Schema(SqliteConnection connection)
    {
        return Rows(connection, "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_autoindex_%' ORDER BY type, name")
            .Select(row => Regex.Replace(row, @"\s+", " "))
            .ToArray();
    }

    private static List<string> Rows(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString())));
        }

        return rows;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal sealed class FixedClock(long unixMilliseconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
    }
}
