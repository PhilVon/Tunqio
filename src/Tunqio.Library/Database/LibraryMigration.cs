using Tunqio.Library.Schema;

namespace Tunqio.Library.Database;

/// <summary>
/// One schema step. <see cref="Sql"/> runs inside its own transaction and the runner then records
/// <c>(version, applied_at)</c> in <c>schema_version</c>, so a crash mid-migration leaves the previous version intact.
/// </summary>
public sealed record LibraryMigration(int Version, string Name, string Sql);

/// <summary>
/// Every migration the library database has ever shipped, in order. Append only: a shipped migration is frozen
/// (its DDL is snapshotted in <c>tests/fixtures/schema/v{N}.sql</c> and the migration test compares the two), so a
/// schema change is always a new entry that upgrades the previous version in place.
/// </summary>
public static class LibraryMigrations
{
    public static IReadOnlyList<LibraryMigration> All { get; } =
    [
        new(1, "initial schema", LibrarySchema.V1),
    ];

    /// <summary>The schema version this build creates and expects.</summary>
    public static int Latest => All[^1].Version;
}
