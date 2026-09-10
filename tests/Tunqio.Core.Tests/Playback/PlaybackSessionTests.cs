using FluentAssertions;
using Tunqio.Core;
using Tunqio.Core.Audio;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.Core.Tests.Playback;

/// <summary>
/// E1-S10b: the session drives the engine. A scripted sequence of transport commands is compared with the exact
/// calls the engine should have received (AC-63) and with the snapshot the shell would have drawn.
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class PlaybackSessionTests : IAsyncLifetime
{
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = FakeTrackRepository.With(1, 2, 3, 4);
    private readonly FakeSettingsStore _settings = new();
    private readonly FakePlayHistory _history = new();
    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-09-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    private PlaybackSession _session = null!;

    public Task InitializeAsync()
    {
        _session = new PlaybackSession(_engine, _tracks, _history, _settings, _time, new Random(1), autoPoll: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
    }

    /// <summary>Every call the engine gets from one command, so a regression shows as a diff and not as a silence.</summary>
    private string[] Drain() => _engine.Drain();

    [Fact]
    public async Task A_scripted_transport_sequence_produces_the_expected_engine_calls()
    {
        // Play an album from its second track.
        await _session.PlayNowAsync([1, 2, 3], startIndex: 1);
        Drain().Should().Equal(
            @"open:1:D:\Music\2.flac", "gain:1:0:0", "play:1@0",
            "crossfade:0",
            @"open:2:D:\Music\3.flac", "gain:2:0:0", "preload:2:gapless");

        // A manual Next: the queued track is not the one that plays, it is opened again from the top.
        await _session.NextAsync();
        Drain().Should().Equal(
            "preload:none", "close:2",
            "close:1",
            @"open:3:D:\Music\3.flac", "gain:3:0:0", "play:3@0",
            "crossfade:0");

        // Previous, well into the track: the same track restarts.
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(30) };
        await _session.PreviousAsync();
        Drain().Should().Equal("seek:0");

        // Previous, just after the start: the track before it plays.
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(1) };
        await _session.PreviousAsync();
        Drain().Should().Equal(
            "close:3",
            @"open:4:D:\Music\2.flac", "gain:4:0:0", "play:4@0",
            "crossfade:0", @"open:5:D:\Music\3.flac", "gain:5:0:0", "preload:5:gapless");

        await _session.SeekAsync(TimeSpan.FromSeconds(42));
        Drain().Should().Equal("seek:42000");

        await _session.TogglePlayPauseAsync();
        Drain().Should().Equal("pause");
        _session.Current.State.Should().Be(PlaybackState.Paused);

        await _session.TogglePlayPauseAsync();
        Drain().Should().Equal("resume");
        _session.Current.State.Should().Be(PlaybackState.Playing);

        await _session.StopAsync();
        Drain().Should().Equal("stop", "preload:none", "close:5", "close:4");
        _session.Current.State.Should().Be(PlaybackState.Stopped);
        _engine.OpenHandles.Should().BeEmpty("every handle the session opened, it closed");
    }

    [Fact]
    public async Task The_snapshot_follows_the_queue()
    {
        await _session.PlayNowAsync([1, 2, 3], startIndex: 1);
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(12) };
        await _session.PollAsync();

        PlaybackSnapshot snapshot = _session.Current;
        snapshot.State.Should().Be(PlaybackState.Playing);
        snapshot.Track!.Id.Should().Be(2);
        snapshot.Current.Should().Be(_session.Queue.Current);
        snapshot.Position.Should().Be(TimeSpan.FromSeconds(12));
        snapshot.Duration.Should().Be(TimeSpan.FromMinutes(3));
        snapshot.QueueIndex.Should().Be(1);
        snapshot.QueueCount.Should().Be(3);
        snapshot.Repeat.Should().Be(RepeatMode.Off);
        snapshot.Shuffle.Should().BeFalse();
    }

    [Fact]
    public async Task Snapshots_are_published_to_subscribers()
    {
        var seen = new List<PlaybackSnapshot>();
        using IDisposable subscription = _session.Snapshots.Subscribe(seen.Add);

        await _session.PlayNowAsync([1, 2]);

        seen.Should().HaveCountGreaterThan(1);
        seen[0].Should().Be(PlaybackSnapshot.Idle);
        seen[^1].Track!.Id.Should().Be(1);
    }

    // ---- the gapless join ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_now_playing_item_changes_when_the_join_is_heard_and_not_when_it_is_mixed()
    {
        await _session.PlayNowAsync([1, 2, 3]);
        Drain();

        // The engine announces the join as soon as it is mixed: 5000 bytes in, with 2000 still buffered.
        _engine.Clock = new PlaybackClock(TimeSpan.Zero, 5000, TimeSpan.Zero, 0, 2000);
        _engine.Raise(new EngineEvent(EngineEventType.TrackEnded, 1, 5000, null));
        _engine.Raise(new EngineEvent(EngineEventType.TrackStarted, 2, 5000, null));

        await _session.PollAsync();
        _session.Current.Track!.Id.Should().Be(1, "the join has been mixed but not yet heard");
        Drain().Should().BeEmpty();

        // The buffer drains past the join.
        _engine.Clock = new PlaybackClock(TimeSpan.Zero, 7500, TimeSpan.Zero, 0, 2000);
        await _session.PollAsync();

        _session.Current.Track!.Id.Should().Be(2);
        _session.Queue.CurrentIndex.Should().Be(1);
        Drain().Should().Equal("close:1", "crossfade:0", @"open:3:D:\Music\3.flac", "gain:3:0:0", "preload:3:gapless");
    }

    [Fact]
    public async Task A_track_that_ends_with_nothing_behind_it_stops_the_session()
    {
        await _session.PlayNowAsync([1]);
        Drain();

        _engine.Raise(new EngineEvent(EngineEventType.TrackEnded, 1, 0, null));
        await _session.PollAsync();

        _session.Current.State.Should().Be(PlaybackState.Stopped);
        _session.Current.Track.Should().BeNull();
        _session.Queue.CurrentIndex.Should().BeNull();
        _engine.OpenHandles.Should().BeEmpty();
    }

    [Fact]
    public async Task Repeat_all_queues_the_wrap_so_the_last_track_joins_the_first()
    {
        await _session.PlayNowAsync([1, 2], startIndex: 1);
        await _session.SetRepeatAsync(RepeatMode.All);

        _engine.Calls.Should().Contain(call => call.StartsWith("preload:", StringComparison.Ordinal) && call.EndsWith(":gapless", StringComparison.Ordinal));
        _engine.Calls.Should().Contain(@"open:2:D:\Music\1.flac");
    }

    [Fact]
    public async Task Repeat_one_queues_the_same_track_again_as_its_own_stream()
    {
        await _session.PlayNowAsync([1, 2]);
        Drain();

        await _session.SetRepeatAsync(RepeatMode.One);

        Drain().Should().Equal(
            "crossfade:0", @"open:3:D:\Music\1.flac", "gain:3:0:0",
            "preload:none", "close:2",
            "preload:3:gapless");
    }

    // ---- the joins the policies choose -----------------------------------------------------------------------------

    [Fact]
    public async Task A_boundary_between_albums_takes_the_user_crossfade()
    {
        _tracks.Add(2, albumId: 77);
        _settings.SetValue(SettingsKeys.PlaybackCrossfadeMs, 4000);

        await _session.PlayNowAsync([1, 2]);

        _engine.Calls.Should().Contain("crossfade:4000");
        _engine.Calls.Should().Contain("preload:2:crossfade");
    }

    [Fact]
    public async Task Gapless_turned_off_makes_every_boundary_a_crossfade()
    {
        _settings.SetValue(SettingsKeys.PlaybackGapless, false);

        await _session.PlayNowAsync([1, 2]);

        _engine.Calls.Should().Contain("preload:2:crossfade");
    }

    [Fact]
    public async Task Each_track_carries_its_own_gain_before_it_is_queued()
    {
        _tracks.Add(2, gain: new ReplayGainTags(TrackGainDb: -6.5, TrackPeak: 0.9, AlbumGainDb: null, AlbumPeak: null));
        _settings.SetValue(SettingsKeys.PlaybackReplayGain, "track");
        _settings.SetValue(SettingsKeys.PlaybackReplayGainPreampDb, 2f);

        await _session.PlayNowAsync([1, 2]);

        string[] calls = Drain();
        calls.Should().Contain("gain:2:-4.5:0.9");
        Array.IndexOf(calls, "gain:2:-4.5:0.9").Should().BeLessThan(
            Array.IndexOf(calls, "preload:2:gapless"),
            "the gain belongs to the track and has to be set before the join is queued");
    }

    // ---- queue commands ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Play_next_re_queues_the_boundary()
    {
        await _session.PlayNowAsync([1, 2]);
        Drain();

        await _session.PlayNextAsync([3]);

        _session.Queue.Items.Select(i => i.TrackId).Should().Equal(1, 3, 2);
        Drain().Should().Equal(
            "crossfade:0", @"open:3:D:\Music\3.flac", "gain:3:0:0",
            "preload:none", "close:2",
            "preload:3:gapless");
    }

    [Fact]
    public async Task Enqueue_appends_without_disturbing_a_queued_boundary()
    {
        await _session.PlayNowAsync([1, 2]);
        Drain();

        await _session.EnqueueAsync([3]);

        _session.Queue.Items.Select(i => i.TrackId).Should().Equal(1, 2, 3);
        Drain().Should().Equal(["crossfade:0"], "the boundary is unchanged, so the queued track stays queued");
    }

    [Fact]
    public async Task Removing_the_playing_item_starts_the_one_that_took_its_place()
    {
        await _session.PlayNowAsync([1, 2, 3]);
        Guid playing = _session.Queue.Items[0].InstanceId;
        Drain();

        await _session.RemoveFromQueueAsync(playing);

        _session.Queue.Items.Select(i => i.TrackId).Should().Equal(2, 3);
        _session.Current.Track!.Id.Should().Be(2);
    }

    [Fact]
    public async Task Removing_a_queued_item_re_queues_the_boundary()
    {
        await _session.PlayNowAsync([1, 2, 3]);
        Guid queued = _session.Queue.Items[1].InstanceId;
        Drain();

        await _session.RemoveFromQueueAsync(queued);

        _session.Queue.Items.Select(i => i.TrackId).Should().Equal(1, 3);
        Drain().Should().Contain(@"open:3:D:\Music\3.flac").And.Contain("preload:3:gapless");
    }

    [Fact]
    public async Task Moving_a_queue_item_re_queues_the_boundary()
    {
        await _session.PlayNowAsync([1, 2, 3]);
        Guid last = _session.Queue.Items[2].InstanceId;
        Drain();

        await _session.MoveInQueueAsync(last, 1);

        _session.Queue.Items.Select(i => i.TrackId).Should().Equal(1, 3, 2);
        Drain().Should().Contain("preload:3:gapless");
    }

    [Fact]
    public async Task Shuffle_keeps_the_current_track_playing_and_re_queues_what_follows()
    {
        await _session.PlayNowAsync([1, 2, 3, 4]);
        long playing = _session.Current.Track!.Id;
        Drain();

        await _session.SetShuffleAsync(true);

        _session.Current.Track!.Id.Should().Be(playing, "shuffle does not interrupt the track");
        _session.Queue.Shuffle.Should().BeTrue();
        Drain().Should().NotBeEmpty("the boundary is reconsidered under the new order");

        await _session.SetShuffleAsync(true);
        Drain().Should().BeEmpty("shuffle was already on, so nothing is re-queued");
    }

    [Fact]
    public void Volume_is_clamped_and_reported()
    {
        _session.SetVolume(0.4f);
        Drain().Should().Equal("volume:0.4");
        _session.Current.Volume.Should().Be(0.4f);

        _session.SetVolume(3f);
        Drain().Should().Equal("volume:1");
        _session.Current.Volume.Should().Be(1f);
    }

    // ---- tracks the library cannot give the engine --------------------------------------------------------------------

    [Fact]
    public async Task A_track_that_left_the_library_is_skipped_rather_than_stopping_the_queue()
    {
        _tracks.Remove(2);

        await _session.PlayNowAsync([2, 3]);

        _session.Current.Track!.Id.Should().Be(3);
        _session.Queue.CurrentIndex.Should().Be(1);
    }

    [Fact]
    public async Task A_track_whose_file_would_not_open_is_skipped()
    {
        _engine.Unopenable.Add(@"D:\Music\1.flac");

        await _session.PlayNowAsync([1, 2]);

        _session.Current.Track!.Id.Should().Be(2);
    }

    [Fact]
    public async Task A_track_missing_at_the_last_scan_is_skipped()
    {
        _tracks.Add(1, missing: true);

        await _session.PlayNowAsync([1, 2]);

        _session.Current.Track!.Id.Should().Be(2);
    }

    [Fact]
    public async Task A_queue_of_nothing_playable_stops()
    {
        _tracks.Remove(1);
        _tracks.Remove(2);

        await _session.PlayNowAsync([1, 2]);

        _session.Current.State.Should().Be(PlaybackState.Stopped);
        _session.Current.Track.Should().BeNull();
    }

    [Fact]
    public async Task Playing_nothing_stops()
    {
        await _session.PlayNowAsync([1]);
        await _session.PlayNowAsync([]);

        _session.Current.State.Should().Be(PlaybackState.Stopped);
        _session.Queue.Count.Should().Be(0);
        _engine.OpenHandles.Should().BeEmpty();
    }

    // ---- the device going away ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Losing_the_device_parks_the_session_rather_than_stopping_it()
    {
        await _session.PlayNowAsync([1, 2]);

        _engine.Raise(new EngineEvent(EngineEventType.DeviceLost, 0, 0, "endpoint"));
        await _session.PollAsync();

        _session.Current.State.Should().Be(PlaybackState.Paused);
        _session.Current.Track!.Id.Should().Be(1, "nothing was unloaded, so resuming carries on from here");
    }

    // ---- lifetime ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_session_needs_an_engine_a_library_and_settings()
    {
        FluentActions.Invoking(() => new PlaybackSession(null!, _tracks, _history, _settings)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new PlaybackSession(_engine, null!, _history, _settings)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new PlaybackSession(_engine, _tracks, null!, _settings)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new PlaybackSession(_engine, _tracks, _history, null!)).Should().Throw<ArgumentNullException>();
        await FluentActions.Awaiting(() => _session.PlayNowAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _session.PlayNextAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
        await FluentActions.Awaiting(() => _session.EnqueueAsync(null!)).Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task Disposing_closes_what_is_open_and_refuses_further_commands()
    {
        await _session.PlayNowAsync([1, 2]);

        await _session.DisposeAsync();
        await _session.DisposeAsync();

        _engine.OpenHandles.Should().BeEmpty();
        await FluentActions.Awaiting(() => _session.NextAsync()).Should().ThrowAsync<ObjectDisposedException>();
    }
}
#pragma warning restore CA1001
