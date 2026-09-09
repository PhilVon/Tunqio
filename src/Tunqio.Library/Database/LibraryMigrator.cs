using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tunqio.Library.Database;

/// <summary>
/// Applies <see cref="LibraryMigrations.All"/> to a connection. Version 0 is an empty database (no
/// <c>schema_version</c> table): migration 1 creates that table itself, so the runner needs no bootstrap step.
/// </summary>
public static class LibraryMigrator
{
    /// <summary>
    /// The version recorded in the database: 0 for an empty file. Throws <see cref="LibraryDatabaseException"/>
    /// (<see cref="LibraryDatabaseProblem.Unrecognised"/>) for a SQLite file with tables but no <c>schema_version</c>.
    /// </summary>
    public static int CurrentVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!TableExists(connection, "schema_version"))
        {
            long tables = (long)Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'")!;
            return tables == 0
                ? 0
                : throw new LibraryDatabaseException(LibraryDatabaseProblem.Unrecognised, "The file is a SQLite database but not a Tunqio library (no schema_version table).");
        }

        object? max = Scalar(connection, "SELECT MAX(version) FROM schema_version");
        return max is long version ? checked((int)version) : 0;
    }

    /// <summary>Migrations newer than the database's current version, in order.</summary>
    public static IReadOnlyList<LibraryMigration> Pending(SqliteConnection connection)
    {
        int current = CurrentVersion(connection);
        if (current > LibraryMigrations.Latest)
        {
            throw new LibraryDatabaseException(
                LibraryDatabaseProblem.NewerVersion,
                $"The library database is schema version {current} but this build understands up to version {LibraryMigrations.Latest}. Upgrade Tunqio, or move the file aside to start a new library.");
        }

        return LibraryMigrations.All.Where(m => m.Version > current).ToArray();
    }

    /// <summary>
    /// Brings the database to <see cref="LibraryMigrations.Latest"/>, one transaction per migration, and returns
    /// what was applied (empty when already current).
    /// </summary>
    public static IReadOnlyList<LibraryMigration> Apply(SqliteConnection connection, TimeProvider? clock = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        clock ??= TimeProvider.System;
        logger ??= NullLogger.Instance;

        IReadOnlyList<LibraryMigration> pending = Pending(connection);
        foreach (LibraryMigration migration in pending)
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            using (SqliteCommand ddl = connection.CreateCommand())
            {
                ddl.Transaction = tx;
                ddl.CommandText = migration.Sql;
                ddl.ExecuteNonQuery();
            }

            using (SqliteCommand record = connection.CreateCommand())
            {
                record.Transaction = tx;
                record.CommandText = "INSERT INTO schema_version(version, applied_at) VALUES ($v, $t)";
                record.Parameters.AddWithValue("$v", migration.Version);
                record.Parameters.AddWithValue("$t", clock.GetUtcNow().ToUnixTimeMilliseconds());
                record.ExecuteNonQuery();
            }

            tx.Commit();
            logger.LogInformation("Applied library migration {Version} ({Name})", migration.Version, migration.Name);
        }

        return pending;
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", name);
        return (long)command.ExecuteScalar()! > 0;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
