namespace Tunqio.Library.Database;

/// <summary>
/// What <see cref="LibraryDatabase.Open(string, TimeProvider?, Microsoft.Extensions.Logging.ILogger?)"/> did.
/// <see cref="Recovery"/> is non-null when the existing file was unusable and was moved aside: the shell must
/// tell the user (docs/library-and-data.md, "Failure handling").
/// </summary>
public sealed record LibraryOpenResult(
    string Path,
    bool Created,
    int SchemaVersion,
    IReadOnlyList<int> AppliedVersions,
    DatabaseRecovery? Recovery,
    TimeSpan Elapsed);

/// <summary>An unusable database file was renamed to <see cref="AsidePath"/> and a fresh one created in its place.</summary>
public sealed record DatabaseRecovery(string AsidePath, LibraryDatabaseProblem Problem, string Detail);
