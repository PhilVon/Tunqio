using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Tunqio.Library.Tests;

/// <summary>
/// ADR-005 assumes the bundled SQLite has FTS5 with the trigram tokenizer and WAL. Prove it before E3 builds on it.
/// </summary>
public class SqliteCapabilityTests
{
    private static SqliteConnection Open()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public void Bundled_sqlite_is_recent_enough_for_trigram_fts5()
    {
        using SqliteConnection connection = Open();
        string version = (string)Scalar(connection, "select sqlite_version()")!;
        Version.Parse(version).Should().BeGreaterThanOrEqualTo(new Version(3, 34, 0), "the trigram tokenizer arrived in 3.34");
    }

    [Fact]
    public void Fts5_trigram_search_matches_substrings()
    {
        using SqliteConnection connection = Open();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                create virtual table track_fts using fts5(title, artist, tokenize = 'trigram');
                insert into track_fts(title, artist) values ('Blue in Green', 'Miles Davis');
                insert into track_fts(title, artist) values ('So What', 'Miles Davis');
                insert into track_fts(title, artist) values ('Naima', 'John Coltrane');
                """;
            command.ExecuteNonQuery();
        }

        Scalar(connection, "select count(*) from track_fts where track_fts match 'ile'").Should().Be(2L);
        Scalar(connection, "select title from track_fts where track_fts match 'reen'").Should().Be("Blue in Green");
    }

    [Fact]
    public void Wal_journal_mode_is_available()
    {
        string path = Path.Combine(Path.GetTempPath(), $"tunqio-wal-{Guid.NewGuid():N}.db");
        try
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            Scalar(connection, "pragma journal_mode = wal").Should().Be("wal");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                File.Delete(path + suffix);
            }
        }
    }
}
