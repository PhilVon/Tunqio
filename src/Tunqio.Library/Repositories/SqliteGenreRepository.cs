using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.Library.Database;

namespace Tunqio.Library.Repositories;

/// <summary><see cref="IGenreRepository"/>: every genre with its present-track count, for the tag cloud.</summary>
public sealed class SqliteGenreRepository : IGenreRepository
{
    private readonly LibraryDatabase _db;

    public SqliteGenreRepository(LibraryDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<IReadOnlyList<GenreDto>> ListAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, """
            SELECT g.id, g.name, COUNT(t.id)
            FROM genre g
            LEFT JOIN track_genre tg ON tg.genre_id = g.id
            LEFT JOIN track t ON t.id = tg.track_id AND t.missing = 0
            GROUP BY g.id
            ORDER BY g.name COLLATE NOCASE, g.id
            """);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<GenreDto>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new GenreDto(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return rows;
    }
}
