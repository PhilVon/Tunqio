using FluentAssertions;
using Tunqio.Core;
using Tunqio.Core.Playback;

namespace Tunqio.Core.Tests.Playback;

/// <summary>
/// E1-S10d: the queue and the position survive a restart. The queue always comes back;
/// <c>playback.resumeOnLaunch</c> decides whether the track that was current is opened, paused, where it was left
/// (docs/solution-structure.md, start-up step d).
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class PlaybackRestoreTests : IAsyncLifetime
{
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = FakeTrackRepository.With(1, 2, 3);
    private readonly FakeSettingsStore _settings = new();
    private readonly FakePlayHistory _history = new();
    private readonly FakeQueueStore _queues = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
    private PlaybackSession _session = null!;

    public Task InitializeAsync()
    {
        _session = NewSession();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
    }

    private PlaybackSession NewSession() =>
        new(_engine, _tracks, _history, _queues, _settings, _time, new Random(1), autoPoll: false);

    [Fact]
    public void A_session_that_has_played_nothing_captures_an_empty_queue()
    {
        QueueState captured = _session.Capture();

        captured.Items.Should().BeEmpty();
        captured.CurrentIndex.Should().BeNull();
        captured.Position.Should().BeNull();
        captured.SavedAt.Should().Be(_time.GetUtcNow().ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Capture_carries_the_queue_the_position_and_the_modes()
    {
        await _session.PlayNowAsync([1, 2, 3], startIndex: 1);
        await _session.SetRepeatAsync(RepeatMode.All);
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(38) };

        QueueState captured = _session.Capture();

        captured.Items.Select(i => i.TrackId).Should().Equal(1, 2, 3);
        captured.CurrentIndex.Should().Be(1);
        captured.Position.Should().Be(TimeSpan.FromSeconds(38));
        captured.Repeat.Should().Be(RepeatMode.All);
    }

    [Fact]
    public async Task Stopping_saves_the_queue_and_the_position_it_stopped_at()
    {
        await _session.PlayNowAsync([1, 2, 3], startIndex: 2);
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(12) };

        await _session.StopAsync();

        _queues.Saved!.Position.Should().Be(TimeSpan.FromSeconds(12), "the position when the user stopped, not zero");
        _queues.Saved.CurrentIndex.Should().Be(2);
    }

    [Fact]
    public async Task Disposing_saves_the_queue_so_closing_the_app_is_enough()
    {
        await _session.PlayNowAsync([1, 2]);
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(5) };

        await _session.DisposeAsync();

        _queues.Saved!.Items.Should().HaveCount(2);
        _queues.Saved.Position.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_restarted_session_opens_the_track_it_was_left_on_paused_where_it_was_left()
    {
        await _session.PlayNowAsync([1, 2, 3], startIndex: 1);
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(64) };
        await _session.StopAsync();
        await _session.DisposeAsync();
        _engine.Drain();

        _session = NewSession();
        bool restored = await _session.RestoreAsync();

        restored.Should().BeTrue();
        _session.Queue.Items.Select(i => i.TrackId).Should().Equal(1, 2, 3);
        _session.Queue.CurrentIndex.Should().Be(1);
        _session.Current.State.Should().Be(PlaybackState.Paused, "a launch that starts making noise is a launch nobody asked for");
        _session.Current.Track!.Id.Should().Be(2);
        string[] calls = _engine.Drain();
        calls.Should().Contain(@"open:3:D:\Music\2.flac");
        calls.Should().ContainInOrder("play:3@64000", "pause");
    }

    [Fact]
    public async Task A_restored_shuffle_is_still_shuffled_and_still_undoable()
    {
        await _session.PlayNowAsync([1, 2, 3]);
        await _session.SetShuffleAsync(true);
        long[] order = [.. _session.Queue.Items.Select(i => i.TrackId)];
        await _session.DisposeAsync();

        _session = NewSession();
        await _session.RestoreAsync();

        _session.Queue.Shuffle.Should().BeTrue();
        _session.Queue.Items.Select(i => i.TrackId).Should().Equal(order);
        _session.Queue.AddedOrder.Select(i => i.TrackId).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Resume_on_launch_turned_off_brings_the_queue_back_without_touching_the_engine()
    {
        await _session.PlayNowAsync([1, 2]);
        await _session.DisposeAsync();
        _engine.Drain();

        _settings.SetValue(SettingsKeys.PlaybackResumeOnLaunch, false);
        _session = NewSession();
        bool restored = await _session.RestoreAsync();

        restored.Should().BeTrue();
        _session.Queue.Items.Should().HaveCount(2);
        _session.Current.State.Should().Be(PlaybackState.Stopped);
        _engine.Drain().Should().BeEmpty("nothing was opened");
    }

    [Fact]
    public async Task Nothing_saved_restores_nothing()
    {
        (await _session.RestoreAsync()).Should().BeFalse();
        _session.Current.Should().Be(PlaybackSnapshot.Idle);
    }

    [Fact]
    public async Task A_saved_queue_whose_tracks_have_all_gone_leaves_the_session_stopped()
    {
        await _session.PlayNowAsync([1, 2]);
        await _session.DisposeAsync();

        _tracks.Remove(1);
        _tracks.Remove(2);
        _session = NewSession();
        await _session.RestoreAsync();

        _session.Current.State.Should().Be(PlaybackState.Stopped);
        _session.Current.Track.Should().BeNull();
    }

    [Fact]
    public async Task A_saved_queue_with_no_current_item_comes_back_without_one()
    {
        await _session.PlayNowAsync([1]);
        _engine.Raise(new Core.Audio.EngineEvent(Core.Audio.EngineEventType.TrackEnded, 1, 0, null));
        await _session.PollAsync();
        await _session.DisposeAsync();

        _session = NewSession();
        await _session.RestoreAsync();

        _session.Queue.Count.Should().Be(1);
        _session.Queue.CurrentIndex.Should().BeNull();
        _session.Current.State.Should().Be(PlaybackState.Stopped);
    }

    [Fact]
    public async Task A_restored_track_does_not_arrive_with_its_position_already_heard()
    {
        await _session.PlayNowAsync([1]);
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(150) };
        await _session.DisposeAsync();
        _history.Events.Clear();

        _session = NewSession();
        await _session.RestoreAsync();
        await _session.StopAsync();

        _history.Events.Should().ContainSingle().Which.PlayedMs
            .Should().Be(0, "the session was handed a position, not two and a half minutes of listening");
    }

    [Fact]
    public async Task A_store_that_will_not_answer_costs_the_queue_and_nothing_else()
    {
        _queues.Refuse = new InvalidOperationException("the database is gone");

        (await _session.RestoreAsync()).Should().BeFalse();

        await _session.PlayNowAsync([1, 2]);
        await _session.StopAsync();
        _session.Current.State.Should().Be(PlaybackState.Stopped);
    }
}
#pragma warning restore CA1001
