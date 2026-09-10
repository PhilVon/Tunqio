using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Playback;
using Tunqio.Library.Database;

namespace Tunqio.Library.Repositories;

/// <summary>
/// <see cref="IQueueStateRepository"/> over the single <c>queue_state</c> row (E1-S10). The items live in the row's
/// JSON rather than in a table of their own: the queue is written whole and read whole, and a table would buy
/// nothing but the cost of keeping it in step with a list that changes on every skip.
/// </summary>
public sealed class SqliteQueueStateRepository : IQueueStateRepository
{
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly LibraryDatabase _db;

    public SqliteQueueStateRepository(LibraryDatabase db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<QueueState?> LoadAsync(CancellationToken ct = default)
    {
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, "SELECT items_json, current_index, position_ms, shuffle, repeat_mode, saved_at FROM queue_state WHERE id = 1");
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        StoredQueue? stored = Parse(reader.GetString(0));
        if (stored?.Items is null)
        {
            return null;
        }

        int? currentIndex = reader.Int(1);
        long? positionMs = reader.Long(2);
        bool shuffle = reader.GetInt64(3) != 0;
        RepeatMode repeat = ParseRepeat(reader.GetString(4));
        long savedAt = reader.GetInt64(5);

        QueueItem[] added = [.. stored.Items.Select(item => new QueueItem(item.T, item.I))];
        QueueItem[] play = PlayOrder(added, stored.Play);
        QueueItem? current = currentIndex is int at && at >= 0 && at < play.Length ? play[at] : null;

        IReadOnlySet<long> live = await LiveTrackIdsAsync(connection, added, ct).ConfigureAwait(false);
        added = [.. added.Where(item => live.Contains(item.TrackId))];
        play = [.. play.Where(item => live.Contains(item.TrackId))];
        current = current is not null && live.Contains(current.TrackId) ? current : null;

        int? restoredIndex = current is null ? null : Array.IndexOf(play, current);
        return new QueueState(added, play, restoredIndex, positionMs is long ms ? TimeSpan.FromMilliseconds(ms) : null, shuffle, repeat, savedAt);
    }

    public async Task SaveAsync(QueueState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var stored = new StoredQueue(
            [.. state.AddedOrder.Select(item => new StoredItem(item.TrackId, item.InstanceId))],
            Permutation(state.AddedOrder, state.Items));

        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, """
            INSERT INTO queue_state(id, items_json, current_index, position_ms, shuffle, repeat_mode, saved_at)
            VALUES (1, $items, $index, $position, $shuffle, $repeat, $savedAt)
            ON CONFLICT(id) DO UPDATE SET
                items_json = excluded.items_json,
                current_index = excluded.current_index,
                position_ms = excluded.position_ms,
                shuffle = excluded.shuffle,
                repeat_mode = excluded.repeat_mode,
                saved_at = excluded.saved_at
            """);
        command.Add("$items", JsonSerializer.Serialize(stored, Json));
        command.Add("$index", state.CurrentIndex);
        command.Add("$position", state.Position is TimeSpan position ? (long)position.TotalMilliseconds : null);
        command.Add("$shuffle", state.Shuffle ? 1L : 0L);
        command.Add("$repeat", RepeatName(state.Repeat));
        command.Add("$savedAt", state.SavedAt);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        using IDisposable lease = await _db.AcquireWriterAsync(ct).ConfigureAwait(false);
        await using SqliteConnection connection = await _db.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using SqliteCommand command = Sql.Command(connection, "DELETE FROM queue_state WHERE id = 1");
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static string RepeatName(RepeatMode repeat) => repeat switch
    {
        RepeatMode.All => "all",
        RepeatMode.One => "one",
        _ => "off",
    };

    internal static RepeatMode ParseRepeat(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "all" => RepeatMode.All,
        "one" => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    /// <summary>The play order as indices into the added order, or null when the two are the same list.</summary>
    private static int[]? Permutation(IReadOnlyList<QueueItem> added, IReadOnlyList<QueueItem> play)
    {
        Dictionary<Guid, int> positions = added
            .Select((item, index) => (item.InstanceId, index))
            .ToDictionary(pair => pair.InstanceId, pair => pair.index);

        var order = new int[play.Count];
        bool identical = play.Count == added.Count;
        for (int i = 0; i < play.Count; i++)
        {
            order[i] = positions.TryGetValue(play[i].InstanceId, out int at) ? at : -1;
            identical &= order[i] == i;
        }

        return identical ? null : order;
    }

    /// <summary>
    /// The stored permutation applied, or the added order when there is none. A permutation that does not name
    /// every item exactly once is a corrupt row: the added order is used instead, which costs the shuffle but
    /// keeps the queue.
    /// </summary>
    private static QueueItem[] PlayOrder(QueueItem[] added, int[]? permutation)
    {
        if (permutation is null || permutation.Length != added.Length)
        {
            return added;
        }

        var play = new QueueItem[added.Length];
        var seen = new bool[added.Length];
        for (int i = 0; i < permutation.Length; i++)
        {
            int at = permutation[i];
            if (at < 0 || at >= added.Length || seen[at])
            {
                return added;
            }

            seen[at] = true;
            play[i] = added[at];
        }

        return play;
    }

    private static StoredQueue? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<StoredQueue>(json, Json);
        }
        catch (JsonException)
        {
            return null; // a queue is a convenience; a corrupt row loses it rather than failing the launch
        }
    }

    private static async Task<IReadOnlySet<long>> LiveTrackIdsAsync(SqliteConnection connection, QueueItem[] items, CancellationToken ct)
    {
        var wanted = items.Select(item => item.TrackId).Distinct().ToArray();
        var live = new HashSet<long>();
        for (int start = 0; start < wanted.Length; start += 500)
        {
            long[] chunk = wanted[start..Math.Min(start + 500, wanted.Length)];
            string names = string.Join(", ", chunk.Select((_, i) => "$id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            await using SqliteCommand command = Sql.Command(connection, "SELECT id FROM track WHERE id IN (" + names + ")");
            for (int i = 0; i < chunk.Length; i++)
            {
                command.Add("$id" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), chunk[i]);
            }

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                live.Add(reader.GetInt64(0));
            }
        }

        return live;
    }

    private sealed record StoredItem(
        [property: JsonPropertyName("t")] long T,
        [property: JsonPropertyName("i")] Guid I);

    private sealed record StoredQueue(
        [property: JsonPropertyName("items")] IReadOnlyList<StoredItem> Items,
        [property: JsonPropertyName("play")] int[]? Play);
}
