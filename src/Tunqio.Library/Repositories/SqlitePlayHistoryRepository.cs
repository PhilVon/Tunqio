using System.Data.Common;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.Library.Database;

namespace Tunqio.Library.Repositories;

/// <summary>
/// <see cref="IPlayHistoryRepository"/> (E3-S11): one row per listen in <c>play_event</c>, and for a completed
/// listen the two columns "Recently played" and "Most played" (E3-S8) sort by, written in the same transaction.
/// </summary>
public sealed class SqlitePlayHistoryRepository : IPlayHistoryRepository
{
    private readonly LibraryDatabase _db;

    public SqlitePlayHistoryRepository(LibraryDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<bool> RecordAsync(PlayEvent playEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playEvent);

        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Guarded by the track's existence rather than by the foreign key: a purge between the listen and the
        // record would otherwise throw at the end of a track, and one lost play is the cheaper failure.
        int inserted;
        await using (SqliteCommand insert = Sql.Command(connection, """
            INSERT INTO play_event(track_id, started_at, played_ms, completed)
            SELECT $track, $startedAt, $playedMs, $completed
            WHERE EXISTS (SELECT 1 FROM track WHERE id = $track)
            """, (SqliteTransaction)transaction))
        {
            insert.Add("$track", playEvent.TrackId);
            insert.Add("$startedAt", playEvent.StartedAt);
            insert.Add("$playedMs", playEvent.PlayedMs);
            insert.Add("$completed", playEvent.Completed ? 1L : 0L);
            inserted = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (inserted == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        // last_played_at is the listen's start, which is what the event records and what "Recently played" reads;
        // a play that began earlier stays behind one that began later however long each of them ran.
        if (playEvent.Completed)
        {
            await using SqliteCommand update = Sql.Command(connection, """
                UPDATE track
                SET play_count = play_count + 1,
                    last_played_at = MAX(COALESCE(last_played_at, $startedAt), $startedAt)
                WHERE id = $track
                """, (SqliteTransaction)transaction);
            update.Add("$track", playEvent.TrackId);
            update.Add("$startedAt", playEvent.StartedAt);
            await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }
}
