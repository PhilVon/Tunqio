namespace Tunqio.Library.Database;

/// <summary>Why the library database could not be opened as it is.</summary>
public enum LibraryDatabaseProblem
{
    /// <summary>SQLite reports the file as corrupt or not a database (SQLITE_CORRUPT / SQLITE_NOTADB).</summary>
    Corrupt,

    /// <summary>A valid SQLite file that is not a Tunqio library (tables present, no <c>schema_version</c>).</summary>
    Unrecognised,

    /// <summary>The file was written by a newer build; downgrading is refused so no data is lost.</summary>
    NewerVersion,
}

/// <summary>Raised by the migration runner and <see cref="LibraryDatabase.Open(string, TimeProvider?, Microsoft.Extensions.Logging.ILogger?)"/>.</summary>
public sealed class LibraryDatabaseException : Exception
{
    public LibraryDatabaseException()
    {
    }

    public LibraryDatabaseException(string message)
        : base(message)
    {
    }

    public LibraryDatabaseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LibraryDatabaseException(LibraryDatabaseProblem problem, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Problem = problem;
    }

    public LibraryDatabaseProblem Problem { get; }
}
