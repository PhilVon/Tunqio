using System.Data.Common;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.Library.Database;

namespace Tunqio.Library.Repositories;

/// <summary><see cref="ILibraryFolderRepository"/>: the scanner's roots. Paths are stored full, with a trailing separator, case preserved.</summary>
public sealed class SqliteLibraryFolderRepository : ILibraryFolderRepository
{
    private readonly LibraryDatabase _db;

    public SqliteLibraryFolderRepository(LibraryDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>Full path with exactly one trailing directory separator (docs/library-and-data.md, <c>library_folder.path</c>).</summary>
    public static string Normalise(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return full + Path.DirectorySeparatorChar;
    }

    public async Task<IReadOnlyList<LibraryFolderDto>> ListAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, "SELECT id, path, enabled, last_scan_at, last_scan_status FROM library_folder ORDER BY path COLLATE NOCASE, id");
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<LibraryFolderDto>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(Read(reader));
        }

        return rows;
    }

    public async Task<LibraryFolderDto> AddAsync(string path, CancellationToken ct = default)
    {
        string normalised = Normalise(path);
        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, """
            INSERT INTO library_folder(path, enabled) VALUES ($path, 1)
            ON CONFLICT(path) DO UPDATE SET enabled = enabled
            RETURNING id, path, enabled, last_scan_at, last_scan_status
            """);
        command.Add("$path", normalised);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return Read(reader);
    }

    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, "UPDATE library_folder SET enabled = $enabled WHERE id = $id");
        command.Add("$enabled", enabled ? 1L : 0L);
        command.Add("$id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(long id, CancellationToken ct = default)
    {
        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        // The contentless FTS rows do not cascade; drop them by hand before the tracks go.
        await using (SqliteCommand fts = Sql.Command(connection, """
            INSERT INTO track_fts(track_fts, rowid, title, artists, album, album_artist)
            SELECT 'delete', t.id, t.title,
                   COALESCE((SELECT group_concat(a.name, ', ' ORDER BY ta.position, a.id) FROM track_artist ta JOIN artist a ON a.id = ta.artist_id WHERE ta.track_id = t.id AND ta.role = 'artist'), ''),
                   COALESCE(al.title, ''), COALESCE(aa.name, '')
            FROM track t
            LEFT JOIN album al ON al.id = t.album_id
            LEFT JOIN artist aa ON aa.id = al.album_artist_id
            WHERE t.folder_id = $id
            """, (SqliteTransaction)transaction))
        {
            fts.Add("$id", id);
            await fts.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (SqliteCommand delete = Sql.Command(connection, "DELETE FROM library_folder WHERE id = $id", (SqliteTransaction)transaction))
        {
            delete.Add("$id", id);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordScanAsync(long id, long scannedAt, string status, CancellationToken ct = default)
    {
        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, "UPDATE library_folder SET last_scan_at = $at, last_scan_status = $status WHERE id = $id");
        command.Add("$at", scannedAt);
        command.Add("$status", status);
        command.Add("$id", id);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static LibraryFolderDto Read(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0, r.Long(3), r.Text(4));
}
