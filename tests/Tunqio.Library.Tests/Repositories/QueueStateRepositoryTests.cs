using FluentAssertions;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Library.Repositories;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>
/// E1-S10a: the one saved queue. Both orders survive, so a queue restored with shuffle on can still be
/// unshuffled; items whose track has left the library do not come back with it.
/// </summary>
public sealed class QueueStateRepositoryTests : IAsyncLifetime
{
    private const long SavedAt = 1_757_376_000_000;

    private LibrarySeed _seed = null!;
    private long[] _trackIds = null!;

    public async Task InitializeAsync()
    {
        _seed = await LibrarySeed.CreateAsync(withExtras: false);
        _trackIds = [.. (await _seed.Tracks.ListAsync(new TrackQuery(PageSize: 8))).Select(t => t.Id)];
    }

    public Task DisposeAsync()
    {
        _seed.Dispose();
        return Task.CompletedTask;
    }

    private IQueueStateRepository Queue => _seed.Service.QueueState;

    [Fact]
    public async Task Nothing_was_ever_saved_Async()
    {
        (await Queue.LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A_plain_queue_round_trips_Async()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow(_trackIds, startIndex: 2).WithRepeat(RepeatMode.All);

        await Queue.SaveAsync(QueueState.Capture(queue, TimeSpan.FromSeconds(41.5), SavedAt));
        QueueState restored = (await Queue.LoadAsync())!;

        restored.Items.Should().Equal(queue.Items);
        restored.AddedOrder.Should().Equal(queue.AddedOrder);
        restored.CurrentIndex.Should().Be(2);
        restored.Position.Should().Be(TimeSpan.FromMilliseconds(41_500));
        restored.Shuffle.Should().BeFalse();
        restored.Repeat.Should().Be(RepeatMode.All);
        restored.SavedAt.Should().Be(SavedAt);
        restored.ToQueue().Items.Should().Equal(queue.Items);
    }

    [Fact]
    public async Task A_shuffled_queue_comes_back_shuffled_and_can_still_be_unshuffled_Async()
    {
        PlayQueue shuffled = PlayQueue.Empty.PlayNow(_trackIds, startIndex: 1).ToggleShuffle(new Random(11));
        shuffled.Items.Should().NotEqual(shuffled.AddedOrder, "the seed actually shuffles this queue");

        await Queue.SaveAsync(QueueState.Capture(shuffled, TimeSpan.Zero, SavedAt));
        PlayQueue restored = (await Queue.LoadAsync())!.ToQueue();

        restored.Items.Should().Equal(shuffled.Items);
        restored.Shuffle.Should().BeTrue();
        restored.Current.Should().Be(shuffled.Current);
        restored.ToggleShuffle(new Random(11)).Items.Should().Equal(shuffled.AddedOrder);
    }

    [Fact]
    public async Task The_same_track_twice_survives_as_two_items_Async()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow([_trackIds[0], _trackIds[0]]);

        await Queue.SaveAsync(QueueState.Capture(queue, null, SavedAt));
        QueueState restored = (await Queue.LoadAsync())!;

        restored.Items.Should().Equal(queue.Items);
        restored.Position.Should().BeNull();
    }

    [Fact]
    public async Task Saving_replaces_the_one_row_Async()
    {
        await Queue.SaveAsync(QueueState.Capture(PlayQueue.Empty.PlayNow(_trackIds.Take(3)), null, SavedAt));
        await Queue.SaveAsync(QueueState.Capture(PlayQueue.Empty.PlayNow(_trackIds.Take(2)), null, SavedAt + 1));

        (await RowCountAsync()).Should().Be(1);
        QueueState restored = (await Queue.LoadAsync())!;
        restored.Items.Should().HaveCount(2);
        restored.SavedAt.Should().Be(SavedAt + 1);
    }

    [Fact]
    public async Task Clearing_forgets_the_queue_Async()
    {
        await Queue.SaveAsync(QueueState.Capture(PlayQueue.Empty.PlayNow(_trackIds), null, SavedAt));

        await Queue.ClearAsync();

        (await Queue.LoadAsync()).Should().BeNull();
        (await RowCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Tracks_that_have_left_the_library_do_not_come_back_with_the_queue_Async()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow(_trackIds.Take(4), startIndex: 1);
        await Queue.SaveAsync(QueueState.Capture(queue, TimeSpan.FromSeconds(5), SavedAt));

        await DeleteTrackAsync(_trackIds[2]);
        QueueState restored = (await Queue.LoadAsync())!;

        restored.Items.Select(i => i.TrackId).Should().Equal(_trackIds[0], _trackIds[1], _trackIds[3]);
        restored.CurrentIndex.Should().Be(1, "the item that was current is still there, one place further back is not");
    }

    [Fact]
    public async Task Losing_the_current_track_leaves_the_restored_queue_with_nothing_current_Async()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow(_trackIds.Take(3), startIndex: 1);
        await Queue.SaveAsync(QueueState.Capture(queue, TimeSpan.FromSeconds(5), SavedAt));

        await DeleteTrackAsync(_trackIds[1]);
        QueueState restored = (await Queue.LoadAsync())!;

        restored.Items.Should().HaveCount(2);
        restored.CurrentIndex.Should().BeNull();
        restored.ToQueue().Current.Should().BeNull();
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"play\":[0,1]}")]
    public async Task A_corrupt_row_loses_the_queue_rather_than_failing_the_launch_Async(string json)
    {
        await Queue.SaveAsync(QueueState.Capture(PlayQueue.Empty.PlayNow(_trackIds), null, SavedAt));
        await SetItemsJsonAsync(json);

        (await Queue.LoadAsync()).Should().BeNull();
    }

    [Fact]
    public async Task A_permutation_that_is_not_one_costs_the_shuffle_and_keeps_the_queue_Async()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow(_trackIds.Take(3));
        await Queue.SaveAsync(QueueState.Capture(queue, null, SavedAt));
        string items = await ItemsJsonAsync();
        await SetItemsJsonAsync(items.TrimEnd('}') + ",\"play\":[1,1,1]}");

        QueueState restored = (await Queue.LoadAsync())!;

        restored.Items.Should().Equal(queue.AddedOrder);
    }

    [Theory]
    [InlineData(RepeatMode.Off)]
    [InlineData(RepeatMode.All)]
    [InlineData(RepeatMode.One)]
    public async Task Every_repeat_mode_round_trips_Async(RepeatMode repeat)
    {
        await Queue.SaveAsync(QueueState.Capture(PlayQueue.Empty.PlayNow(_trackIds).WithRepeat(repeat), null, SavedAt));

        (await Queue.LoadAsync())!.Repeat.Should().Be(repeat);
    }

    [Fact]
    public async Task An_unreadable_repeat_mode_reads_as_off_Async()
    {
        await Queue.SaveAsync(QueueState.Capture(PlayQueue.Empty.PlayNow(_trackIds).WithRepeat(RepeatMode.One), null, SavedAt));
        await ExecuteAsync("UPDATE queue_state SET repeat_mode = 'whatever' WHERE id = 1");

        (await Queue.LoadAsync())!.Repeat.Should().Be(RepeatMode.Off);
    }

    [Fact]
    public async Task A_repository_needs_a_database_and_a_state_Async()
    {
        FluentActions.Invoking(() => new SqliteQueueStateRepository(null!)).Should().Throw<ArgumentNullException>();
        await FluentActions.Awaiting(() => Queue.SaveAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    private async Task<int> RowCountAsync() => (int)(long)(await ScalarAsync("SELECT COUNT(*) FROM queue_state"))!;

    private async Task<string> ItemsJsonAsync() => (string)(await ScalarAsync("SELECT items_json FROM queue_state WHERE id = 1"))!;

    private Task SetItemsJsonAsync(string json) =>
        ExecuteAsync("UPDATE queue_state SET items_json = $json WHERE id = 1", ("$json", json));

    private Task DeleteTrackAsync(long id) => ExecuteAsync("DELETE FROM track WHERE id = $id", ("$id", id));

    private async Task<object?> ScalarAsync(string sql)
    {
        await using SqliteConnection connection = await _seed.Db.OpenConnectionAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using SqliteConnection connection = await _seed.Db.OpenConnectionAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
