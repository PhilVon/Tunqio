using FluentAssertions;
using Tunqio.Core.Audio;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.Core.Tests.Playback;

/// <summary>
/// E1-S10c: the session emits a play event whenever a track stops being current, with the heard time it actually
/// had and the completed flag <see cref="PlayCompletion"/> gives that (AC-66).
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class PlaybackHistoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = FakeTrackRepository.With(1, 2, 3);
    private readonly FakeSettingsStore _settings = new();
    private readonly FakePlayHistory _history = new();
    private readonly ManualTimeProvider _time = new(Start);
    private PlaybackSession _session = null!;

    public Task InitializeAsync()
    {
        _engine.Duration = TimeSpan.FromMinutes(3);
        _session = new PlaybackSession(_engine, _tracks, _history, _settings, _time, new Random(1), autoPoll: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
    }

    /// <summary>Plays for <paramref name="span"/> of audio, in the polls the running session would have taken.</summary>
    private async Task ListenAsync(TimeSpan span)
    {
        TimeSpan step = PlaybackSession.SnapshotInterval;
        for (TimeSpan heard = TimeSpan.Zero; heard < span; heard += step)
        {
            _time.Advance(step);
            _engine.Clock = _engine.Clock with { Position = _engine.Clock.Position + step };
            await _session.PollAsync();
        }
    }

    [Fact]
    public async Task Skipping_a_track_after_ten_seconds_records_a_listen_that_did_not_count()
    {
        await _session.PlayNowAsync([1, 2]);
        await ListenAsync(TimeSpan.FromSeconds(10));

        await _session.NextAsync();

        PlayEvent recorded = _history.Events.Should().ContainSingle().Subject;
        recorded.TrackId.Should().Be(1);
        recorded.StartedAt.Should().Be(Start.ToUnixTimeMilliseconds());
        TimeSpan.FromMilliseconds(recorded.PlayedMs).Should().BeCloseTo(TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(200));
        recorded.Completed.Should().BeFalse();
    }

    [Fact]
    public async Task Listening_past_half_records_a_play()
    {
        await _session.PlayNowAsync([1, 2]);
        await ListenAsync(TimeSpan.FromSeconds(95));

        await _session.NextAsync();

        _history.Events.Should().ContainSingle().Which.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task Paused_time_is_not_heard_time()
    {
        await _session.PlayNowAsync([1]);
        await ListenAsync(TimeSpan.FromSeconds(20));

        await _session.TogglePlayPauseAsync();
        _time.Advance(TimeSpan.FromMinutes(10)); // the position does not move while paused
        await _session.PollAsync();
        await _session.TogglePlayPauseAsync();

        await ListenAsync(TimeSpan.FromSeconds(20));
        await _session.StopAsync();

        TimeSpan.FromMilliseconds(_history.Events.Single().PlayedMs)
            .Should().BeCloseTo(TimeSpan.FromSeconds(40), TimeSpan.FromMilliseconds(400));
    }

    [Fact]
    public async Task Seeking_forward_does_not_credit_the_audio_it_jumped_over()
    {
        await _session.PlayNowAsync([1]);
        await ListenAsync(TimeSpan.FromSeconds(10));

        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(150) };
        await _session.SeekAsync(TimeSpan.FromSeconds(150));
        await _session.PollAsync();

        await _session.StopAsync();

        PlayEvent recorded = _history.Events.Single();
        TimeSpan.FromMilliseconds(recorded.PlayedMs)
            .Should().BeLessThan(TimeSpan.FromSeconds(15), "the two minutes that were skipped were never audible");
        recorded.Completed.Should().BeFalse("skipping to the end is not listening to the track");
    }

    [Fact]
    public async Task A_gapless_join_closes_one_listen_and_opens_the_next()
    {
        await _session.PlayNowAsync([1, 2]);
        await ListenAsync(TimeSpan.FromSeconds(120));

        _engine.Clock = _engine.Clock with { MixerBytePosition = 5000, OutputBufferedBytes = 0 };
        _engine.Raise(new EngineEvent(EngineEventType.TrackEnded, 1, 5000, null));
        _engine.Raise(new EngineEvent(EngineEventType.TrackStarted, 2, 5000, null));
        await _session.PollAsync();

        _history.Events.Should().ContainSingle().Which.Should().Match<PlayEvent>(e => e.TrackId == 1 && e.Completed);

        _engine.Clock = _engine.Clock with { Position = TimeSpan.Zero };
        await ListenAsync(TimeSpan.FromSeconds(100));
        await _session.StopAsync();

        _history.Events.Should().HaveCount(2);
        _history.Events[1].TrackId.Should().Be(2);
        _history.Events[1].Completed.Should().BeTrue();
        _history.Events[1].StartedAt.Should().BeGreaterThan(_history.Events[0].StartedAt);
    }

    [Fact]
    public async Task A_track_that_ends_on_its_own_is_recorded()
    {
        await _session.PlayNowAsync([1]);
        await ListenAsync(TimeSpan.FromSeconds(100));

        _engine.Raise(new EngineEvent(EngineEventType.TrackEnded, 1, 0, null));
        await _session.PollAsync();

        _history.Events.Should().ContainSingle().Which.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task Replacing_the_queue_records_what_was_playing()
    {
        await _session.PlayNowAsync([1]);
        await ListenAsync(TimeSpan.FromSeconds(30));

        await _session.PlayNowAsync([2]);

        _history.Events.Should().ContainSingle().Which.TrackId.Should().Be(1);
    }

    [Fact]
    public async Task A_track_still_playing_when_the_session_is_disposed_is_recorded()
    {
        await _session.PlayNowAsync([1]);
        await ListenAsync(TimeSpan.FromSeconds(100));

        await _session.DisposeAsync();

        _history.Events.Should().ContainSingle().Which.Completed.Should().BeTrue();
    }

    [Fact]
    public async Task Going_back_records_the_track_that_was_abandoned()
    {
        await _session.PlayNowAsync([1, 2], startIndex: 1);
        await ListenAsync(TimeSpan.FromSeconds(1));

        await _session.PreviousAsync();

        _history.Events.Should().ContainSingle().Which.TrackId.Should().Be(2);
    }

    [Fact]
    public async Task Restarting_the_current_track_is_not_a_new_listen()
    {
        await _session.PlayNowAsync([1, 2]);
        await ListenAsync(TimeSpan.FromSeconds(30));

        await _session.PreviousAsync(); // past the restart window, so it seeks to zero

        _history.Events.Should().BeEmpty("the track never stopped being current");
    }

    [Fact]
    public async Task A_history_that_will_not_take_the_event_does_not_interrupt_the_music()
    {
        _history.Refuse = new InvalidOperationException("the database is gone");

        await _session.PlayNowAsync([1, 2]);
        await ListenAsync(TimeSpan.FromSeconds(100));
        await _session.NextAsync();

        _session.Current.State.Should().Be(PlaybackState.Playing);
        _session.Current.Track!.Id.Should().Be(2);
    }
}
#pragma warning restore CA1001
