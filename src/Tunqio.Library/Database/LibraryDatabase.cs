using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.Core;

namespace Tunqio.Library.Database;

/// <summary>
/// The library database: opens (or creates) <c>library.db</c>, switches it to WAL, applies migrations, and then
/// hands out pooled connections to the repositories (docs/library-and-data.md, "Repository layer").
/// <para>
/// Corruption handling per "Failure handling": a file SQLite refuses (corrupt, not a database) or that is not a
/// Tunqio library is renamed to <c>library.corrupt-&lt;timestamp&gt;.db</c>, a fresh database is created, and
/// <see cref="OpenResult"/> carries a <see cref="DatabaseRecovery"/> for the shell to show. A database from a
/// newer build is refused with <see cref="LibraryDatabaseException"/> rather than moved aside, because it holds
/// user data this build cannot read but a newer one can.
/// </para>
/// <para>
/// The open path deliberately runs no <c>PRAGMA quick_check</c>: on the 100k-track fixture it costs about 280 ms
/// warm against the 500 ms open budget, so damage in the middle of the file surfaces from the query that hits
/// it. <see cref="QuickCheck"/> exists for diagnostics.
/// </para>
/// </summary>
public sealed class LibraryDatabase : IDisposable
{
    /// <summary>Statements set on every connection handed out; the file-level ones (WAL) are set once at open.</summary>
    private const string PerConnectionPragmas = "PRAGMA synchronous = NORMAL;";

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private readonly string _connectionString;
    private readonly SqliteConnection? _keepAlive;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ILogger _logger;
    private bool _disposed;

    private LibraryDatabase(string path, string connectionString, SqliteConnection? keepAlive, LibraryOpenResult result, ILogger logger)
    {
        Path = path;
        _connectionString = connectionString;
        _keepAlive = keepAlive;
        OpenResult = result;
        _logger = logger;
    }

    /// <summary>The database file, or the shared-cache name for an in-memory database.</summary>
    public string Path { get; }

    /// <summary>What opening did: created or reused, migrations applied, recovery performed.</summary>
    public LibraryOpenResult OpenResult { get; }

    public int SchemaVersion => OpenResult.SchemaVersion;

    /// <summary>Opens <see cref="IAppPaths.DatabasePath"/>.</summary>
    public static LibraryDatabase Open(IAppPaths paths, TimeProvider? clock = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Open(paths.DatabasePath, clock, logger);
    }

    /// <summary>
    /// Opens or creates the database at <paramref name="path"/> and brings it to the current schema version.
    /// Throws <see cref="LibraryDatabaseException"/> for a newer-version file and <see cref="SqliteException"/>
    /// for I/O failures; anything SQLite reports as corrupt is moved aside and replaced (see the class remarks).
    /// </summary>
    public static LibraryDatabase Open(string path, TimeProvider? clock = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        clock ??= TimeProvider.System;
        logger ??= NullLogger.Instance;
        path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();

        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            return OpenCore(path, connectionString, keepAlive: null, clock, logger, recovery: null, elapsed);
        }
        catch (Exception ex) when (IsUnusable(ex, out LibraryDatabaseProblem problem))
        {
            ClearPool(connectionString); // release every pooled handle before the rename
            string aside = MoveAside(path, clock);
            logger.LogError(ex, "Library database {Path} is unusable ({Problem}); moved it to {Aside} and starting a fresh one", path, problem, aside);
            var recovery = new DatabaseRecovery(aside, problem, ex.Message);
            return OpenCore(path, connectionString, keepAlive: null, clock, logger, recovery, elapsed);
        }
    }

    /// <summary>
    /// A private in-memory database shared by every connection this instance hands out (kept alive until
    /// <see cref="Dispose"/>). For repository tests seeded from fixtures.
    /// </summary>
    public static LibraryDatabase OpenInMemory(string? name = null, TimeProvider? clock = null, ILogger? logger = null)
    {
        name ??= "tunqio-" + Guid.NewGuid().ToString("N");
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = name,
            Mode = SqliteOpenMode.Memory,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();

        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        try
        {
            return OpenCore(name, connectionString, keepAlive, clock ?? TimeProvider.System, logger ?? NullLogger.Instance, recovery: null, Stopwatch.StartNew());
        }
        catch
        {
            keepAlive.Dispose();
            throw;
        }
    }

    /// <summary>A pooled connection with foreign keys on and <c>synchronous = NORMAL</c>. Dispose returns it to the pool.</summary>
    public SqliteConnection OpenConnection()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            Execute(connection, PerConnectionPragmas);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc cref="OpenConnection"/>
    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            Execute(connection, PerConnectionPragmas);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Serialises writers (WAL allows many readers beside one writer; a second writer would only block on
    /// SQLite's busy timeout). Dispose the lease when the transaction has committed.
    /// </summary>
    public async Task<IDisposable> AcquireWriterAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new WriterLease(_writer);
    }

    /// <summary>
    /// <c>PRAGMA quick_check</c>: "ok", or the first problem SQLite found. Walks the whole file (hundreds of
    /// milliseconds on a large library), so this is for diagnostics, not the open path.
    /// </summary>
    public string QuickCheck()
    {
        using SqliteConnection connection = OpenConnection();
        return (string)ExecuteScalar(connection, "PRAGMA quick_check")!;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ClearPool(_connectionString);
        _keepAlive?.Dispose();
        _writer.Dispose();
        _logger.LogDebug("Library database {Path} closed", Path);
    }

    private static LibraryDatabase OpenCore(string path, string connectionString, SqliteConnection? keepAlive, TimeProvider clock, ILogger logger, DatabaseRecovery? recovery, Stopwatch elapsed)
    {
        bool existed = keepAlive is null && File.Exists(path) && new FileInfo(path).Length > 0;
        IReadOnlyList<LibraryMigration> applied;
        int version;
        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            if (keepAlive is null)
            {
                string mode = (string)ExecuteScalar(connection, "PRAGMA journal_mode = WAL")!;
                if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Library database {Path} could not switch to WAL (journal_mode is {Mode})", path, mode);
                }
            }

            applied = LibraryMigrator.Apply(connection, clock, logger);
            version = LibraryMigrator.CurrentVersion(connection);
        }

        var result = new LibraryOpenResult(path, Created: !existed, version, applied.Select(m => m.Version).ToArray(), recovery, elapsed.Elapsed);
        logger.LogInformation(
            "Library database {Path} {Action} at schema v{Version} in {ElapsedMs} ms ({Applied} migration(s) applied)",
            path, existed ? "opened" : "created", version, elapsed.ElapsedMilliseconds, applied.Count);
        return new LibraryDatabase(path, connectionString, keepAlive, result, logger);
    }

    private static bool IsUnusable(Exception ex, out LibraryDatabaseProblem problem)
    {
        switch (ex)
        {
            case SqliteException { SqliteErrorCode: SqliteCorrupt or SqliteNotADatabase }:
                problem = LibraryDatabaseProblem.Corrupt;
                return true;
            case LibraryDatabaseException { Problem: LibraryDatabaseProblem.Unrecognised }:
                problem = LibraryDatabaseProblem.Unrecognised;
                return true;
            default:
                problem = default;
                return false;
        }
    }

    /// <summary>Renames <c>library.db</c> (and its -wal/-shm) to <c>library.corrupt-&lt;timestamp&gt;.db</c>; returns the new path.</summary>
    private static string MoveAside(string path, TimeProvider clock)
    {
        string directory = System.IO.Path.GetDirectoryName(path)!;
        string stem = System.IO.Path.GetFileNameWithoutExtension(path);
        string extension = System.IO.Path.GetExtension(path);
        string stamp = clock.GetUtcNow().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string aside = System.IO.Path.Combine(directory, $"{stem}.corrupt-{stamp}{extension}");
        for (int n = 2; File.Exists(aside); n++)
        {
            aside = System.IO.Path.Combine(directory, $"{stem}.corrupt-{stamp}-{n}{extension}");
        }

        File.Move(path, aside);
        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(path + suffix))
            {
                File.Move(path + suffix, aside + suffix, overwrite: true);
            }
        }

        return aside;
    }

    private static void ClearPool(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private sealed class WriterLease(SemaphoreSlim writer) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                writer.Release();
            }
        }
    }
}
