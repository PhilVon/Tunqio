using FluentAssertions;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.Library.Repositories;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>
/// E3-S11: the write side of play history — one <c>play_event</c> row per listen, and the counts "Recently played"
/// and "Most played" (E3-S8) sort by moved only by a completed one, in the same transaction.
/// </summary>
public sealed class PlayHistoryRepositoryTests : IAsyncLifetime
{
    private const long Started = 1_757_376_000_000; // 2025-09-09T00:00:00Z

    private LibrarySeed _seed = null!;
    private long _trackId;
    private TimeSpan _duration;

    public async Task InitializeAsync()
    {
        // No extras: the synthetic rows carry ratings and play counts, and these tests are about moving them.
        _seed = await LibrarySeed.CreateAsync(withExtras: false);
        TrackDto track = (await _seed.Tracks.ListAsync(new TrackQuery(PageSize: 1)))[0];
        _trackId = track.Id;
        _duration = TimeSpan.FromMilliseconds(track.DurationMs);
    }

    public Task DisposeAsync()
    {
        _seed.Dispose();
        return Task.CompletedTask;
    }

    private IPlayHistoryRepository History => _seed.Service.PlayHistory;

    [Fact]
    public async Task Skipping_a_track_records_the_listen_without_counting_it_as_a_play()
    {
        bool recorded = await History.RecordAsync(PlayEvent.For(_trackId, Started, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(3)));

        recorded.Should().BeTrue();
        (await EventsAsync()).Should().ContainSingle().Which.Should().Be((Started, 10_000L, false));

        TrackDto track = (await _seed.Tracks.GetAsync(_trackId))!;
        track.PlayCount.Should().Be(0);
        track.LastPlayedAt.Should().BeNull();
    }

    [Fact]
    public async Task Listening_past_half_counts_as_a_play_and_moves_the_counts()
    {
        await History.RecordAsync(PlayEvent.For(_trackId, Started, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)));

        (await EventsAsync()).Should().ContainSingle().Which.Should().Be((Started, 120_000L, true));

        TrackDto track = (await _seed.Tracks.GetAsync(_trackId))!;
        track.PlayCount.Should().Be(1);
        track.LastPlayedAt.Should().Be(Started);
    }

    [Fact]
    public async Task Each_completed_listen_counts_once_and_the_latest_start_wins()
    {
        await History.RecordAsync(PlayEvent.For(_trackId, Started, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)));
        await History.RecordAsync(PlayEvent.For(_trackId, Started + 600_000, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)));

        TrackDto track = (await _seed.Tracks.GetAsync(_trackId))!;
        track.PlayCount.Should().Be(2);
        track.LastPlayedAt.Should().Be(Started + 600_000);
        (await EventsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_listen_recorded_out_of_order_does_not_drag_the_last_played_time_backwards()
    {
        await History.RecordAsync(PlayEvent.For(_trackId, Started, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)));
        await History.RecordAsync(PlayEvent.For(_trackId, Started - 600_000, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)));

        TrackDto track = (await _seed.Tracks.GetAsync(_trackId))!;
        track.PlayCount.Should().Be(2, "both listens happened");
        track.LastPlayedAt.Should().Be(Started, "the later one is still the most recent");
    }

    [Fact]
    public async Task A_play_for_a_track_that_is_no_longer_in_the_library_is_dropped()
    {
        long gone = await MaxTrackIdAsync() + 1;

        bool recorded = await History.RecordAsync(PlayEvent.For(gone, Started, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)));

        recorded.Should().BeFalse();
        (await EventsAsync()).Should().BeEmpty("nothing was written, and the foreign key never threw");
    }

    [Fact]
    public async Task Recently_played_and_Most_played_pick_the_track_up()
    {
        TrackDto other = (await _seed.Tracks.ListAsync(new TrackQuery(PageSize: 2)))[1];

        await History.RecordAsync(PlayEvent.For(other.Id, Started, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)));
        await History.RecordAsync(PlayEvent.For(_trackId, Started + 60_000, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)));
        await History.RecordAsync(PlayEvent.For(_trackId, Started + 120_000, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(3)));

        IReadOnlyList<TrackDto> recent = await _seed.Tracks.ListAsync(
            new TrackQuery(TrackSort.LastPlayed, Descending: true, PlayedOnly: true, Take: 500));
        recent.Select(t => t.Id).Should().Equal(_trackId, other.Id);

        IReadOnlyList<TrackDto> most = await _seed.Tracks.ListAsync(
            new TrackQuery(TrackSort.PlayCount, Descending: true, PlayedOnly: true, Take: 500));
        most.Select(t => t.Id).Should().Equal(_trackId, other.Id);
        most[0].PlayCount.Should().Be(2);
    }

    [Fact]
    public async Task A_real_track_duration_decides_the_verdict()
    {
        _duration.Should().BeGreaterThan(TimeSpan.Zero, "the fixture tracks carry a measured length");

        await History.RecordAsync(PlayEvent.For(_trackId, Started, _duration, _duration));

        (await _seed.Tracks.GetAsync(_trackId))!.PlayCount.Should().Be(1);
    }

    [Fact]
    public async Task A_repository_needs_a_database_and_an_event()
    {
        FluentActions.Invoking(() => new SqlitePlayHistoryRepository(null!)).Should().Throw<ArgumentNullException>();
        await FluentActions.Awaiting(() => History.RecordAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    private async Task<IReadOnlyList<(long StartedAt, long PlayedMs, bool Completed)>> EventsAsync()
    {
        await using SqliteConnection connection = await _seed.Db.OpenConnectionAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT started_at, played_ms, completed FROM play_event ORDER BY id";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        var rows = new List<(long, long, bool)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2) != 0));
        }

        return rows;
    }

    private async Task<long> MaxTrackIdAsync()
    {
        await using SqliteConnection connection = await _seed.Db.OpenConnectionAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(id) FROM track";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
