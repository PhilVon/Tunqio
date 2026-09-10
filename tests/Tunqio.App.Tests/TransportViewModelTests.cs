using Tunqio.App;
using Tunqio.App.Playback;
using Tunqio.App.Shell;
using Tunqio.Core.Audio;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;

namespace Tunqio.App.Tests;

/// <summary>
/// E2-S2: the transport panel over a real <see cref="PlaybackSession"/> and a fake engine. AC-71's scrub and
/// AC-72's shuffle and repeat are behaviour rather than pixels, so they are asserted here; that every control is
/// reachable by keyboard and readable by Narrator is a property of the XAML and is checked by the shell spike.
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class TransportViewModelTests : IAsyncLifetime
{
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeSettings _settings = new();
    private readonly FakePlayHistory _history = new();
    private readonly FakeQueueStore _queues = new();
    private readonly StubSessionSource _source = new();
    private PlaybackSession _session = null!;
    private TransportViewModel _vm = null!;

    public Task InitializeAsync()
    {
        _tracks.Rows.AddRange([Rows.Track(11, "One"), Rows.Track(12, "Two"), Rows.Track(13, "Three")]);
        _session = new PlaybackSession(_engine, _tracks, _history, _queues, _settings, autoPoll: false);
        _source.Session = _session;
        _vm = new TransportViewModel(_source);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _vm.Dispose();
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
    }

    /// <summary>What the 10 Hz timer would do; the session is built with autoPoll off so a test decides when.</summary>
    private Task TickAsync() => _session.PollAsync();

    private async Task PlayAnAlbumAsync()
    {
        await _session.PlayNowAsync([11, 12, 13]);
        await TickAsync();
    }

    // ---- AC-72: shuffle and repeat reflect the snapshot ------------------------------------------------------------

    [Fact]
    public async Task Shuffle_and_repeat_follow_the_session_rather_than_the_button_that_asked_Async()
    {
        await PlayAnAlbumAsync();
        _vm.IsShuffled.Should().BeFalse();
        _vm.Repeat.Should().Be(RepeatMode.Off);

        await _vm.ToggleShuffleAsync();
        await TickAsync();
        _vm.IsShuffled.Should().BeTrue();
        _vm.ShuffleLabel.Should().Be("Shuffle on");

        // The session is the owner: a change made anywhere else reaches the icons the same way.
        await _session.SetShuffleAsync(false);
        await TickAsync();
        _vm.IsShuffled.Should().BeFalse("the panel reflects the snapshot, it does not remember what its own button did");
    }

    [Fact]
    public async Task Repeat_cycles_off_all_one_and_says_which_it_is_Async()
    {
        await PlayAnAlbumAsync();

        await _vm.CycleRepeatAsync();
        await TickAsync();
        _vm.Repeat.Should().Be(RepeatMode.All);
        _vm.RepeatLabel.Should().Be("Repeat all");

        await _vm.CycleRepeatAsync();
        await TickAsync();
        _vm.Repeat.Should().Be(RepeatMode.One);
        _vm.RepeatLabel.Should().Be("Repeat one");

        await _vm.CycleRepeatAsync();
        await TickAsync();
        _vm.Repeat.Should().Be(RepeatMode.Off);
        _vm.RepeatLabel.Should().Be("Repeat off");
    }

    [Fact]
    public async Task The_repeat_one_state_has_its_own_glyph_Async()
    {
        await PlayAnAlbumAsync();
        await _session.SetRepeatAsync(RepeatMode.All);
        await TickAsync();
        string all = _vm.RepeatGlyph;

        await _session.SetRepeatAsync(RepeatMode.One);
        await TickAsync();

        _vm.RepeatGlyph.Should().NotBe(all, "repeat-one has to be distinguishable from repeat-all at a glance");
    }

    // ---- AC-71: the scrub ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_snapshot_arriving_mid_drag_does_not_move_the_thumb_Async()
    {
        await PlayAnAlbumAsync();
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(10) };
        await TickAsync();
        _vm.PositionSeconds.Should().BeApproximately(10, 0.001);

        _vm.BeginScrub();
        _vm.ScrubTo(90);

        // Three snapshots at 10 Hz while the user holds the thumb.
        for (int i = 0; i < 3; i++)
        {
            _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(10 + i) };
            await TickAsync();
        }

        _vm.PositionSeconds.Should().BeApproximately(90, 0.001, "the thumb belongs to the user while they are holding it");
    }

    [Fact]
    public async Task The_drag_shows_the_target_time_before_it_commits_Async()
    {
        await PlayAnAlbumAsync();

        _vm.BeginScrub();
        _vm.ScrubTo(125);

        _vm.PositionText.Should().Be("2:05", "the label is the target, which is the whole point of showing it during the drag");
        _engine.Drain().Should().NotContain(c => c.StartsWith("seek", StringComparison.Ordinal), "nothing is committed until release");
    }

    [Fact]
    public async Task Releasing_the_thumb_seeks_once_to_where_it_was_left_Async()
    {
        await PlayAnAlbumAsync();
        _engine.Drain();

        _vm.BeginScrub();
        _vm.ScrubTo(30);
        _vm.ScrubTo(60);
        _vm.ScrubTo(125);
        await _vm.CommitScrubAsync();

        _engine.Drain().Should().ContainSingle("one seek, on release, not one per pixel of the drag")
            .Which.Should().Be("seek:125000");
        _vm.IsScrubbing.Should().BeFalse();
    }

    [Fact]
    public async Task After_the_release_the_position_follows_the_session_again_Async()
    {
        await PlayAnAlbumAsync();
        _vm.BeginScrub();
        _vm.ScrubTo(125);
        await _vm.CommitScrubAsync();

        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(126) };
        await TickAsync();

        _vm.PositionSeconds.Should().BeApproximately(126, 0.001);
    }

    [Fact]
    public async Task A_drag_cannot_be_taken_past_either_end_of_the_track_Async()
    {
        await PlayAnAlbumAsync();
        _vm.DurationSeconds.Should().BeGreaterThan(0);

        _vm.BeginScrub();
        _vm.ScrubTo(-40);
        _vm.PositionSeconds.Should().Be(0);

        _vm.ScrubTo(_vm.DurationSeconds + 500);
        _vm.PositionSeconds.Should().Be(_vm.DurationSeconds);
    }

    [Fact]
    public async Task Scrubbing_without_holding_the_thumb_does_nothing_Async()
    {
        await PlayAnAlbumAsync();
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(10) };
        await TickAsync();

        _vm.ScrubTo(90);

        _vm.PositionSeconds.Should().BeApproximately(10, 0.001, "a stray value change with no drag in progress is not a seek");
    }

    [Fact]
    public async Task Ten_snapshots_a_second_move_the_position_and_nothing_else_Async()
    {
        await PlayAnAlbumAsync();
        var seen = new List<double>();
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TransportViewModel.PositionSeconds))
            {
                seen.Add(_vm.PositionSeconds);
            }
        };

        for (int i = 1; i <= 10; i++)
        {
            _engine.Clock = _engine.Clock with { Position = TimeSpan.FromMilliseconds(i * 100) };
            await TickAsync();
        }

        seen.Should().HaveCount(10).And.OnlyHaveUniqueItems("a repeated position would be a redraw for nothing");
        seen.Should().BeInAscendingOrder("a position that went backwards at 10 Hz is the jitter the criterion is about");
    }

    // ---- the readout -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(65, "1:05")]
    [InlineData(600, "10:00")]
    [InlineData(3661, "1:01:01")]
    public async Task The_readout_is_written_the_way_the_track_lists_write_it_Async(double seconds, string expected)
    {
        _engine.Duration = TimeSpan.FromHours(2); // long enough that none of these clamp
        await PlayAnAlbumAsync();
        _vm.BeginScrub();

        _vm.ScrubTo(seconds);

        _vm.PositionText.Should().Be(expected, "the transport and the track lists share Controls.Format.Duration");
    }

    [Fact]
    public async Task The_readout_toggles_between_elapsed_and_remaining_Async()
    {
        await PlayAnAlbumAsync();
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(65) };
        await TickAsync();
        _vm.PositionText.Should().Be("1:05");

        _vm.ToggleTimeDisplay();

        _vm.PositionText.Should().StartWith("-", "remaining is signed so the two readings cannot be confused");
        _vm.PositionText.Should().Be("-" + Controls.Format.Duration((int)((_vm.DurationSeconds - 65) * 1000)));
    }

    // ---- volume ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Muting_remembers_the_level_to_come_back_to_Async()
    {
        await PlayAnAlbumAsync();
        _vm.SetVolume(0.4f);
        _engine.Drain();

        _vm.ToggleMute();
        _vm.IsMuted.Should().BeTrue();
        _engine.Drain().Should().Contain("volume:0");

        _vm.ToggleMute();

        _vm.IsMuted.Should().BeFalse();
        _vm.Volume.Should().BeApproximately(0.4f, 0.001f, "unmuting goes back to where the slider was, not to full");
    }

    [Fact]
    public async Task Moving_the_slider_off_zero_leaves_mute_Async()
    {
        await PlayAnAlbumAsync();
        _vm.ToggleMute();

        _vm.SetVolume(0.6f);

        _vm.IsMuted.Should().BeFalse("reaching for the slider is how someone unmutes without finding the mute button");
        _vm.Volume.Should().BeApproximately(0.6f, 0.001f);
    }

    [Fact]
    public async Task Volume_is_clamped_to_the_slider_Async()
    {
        await PlayAnAlbumAsync();

        _vm.SetVolume(4f);
        _vm.Volume.Should().Be(1f);

        _vm.SetVolume(-1f);
        _vm.Volume.Should().Be(0f);
    }

    // ---- transport and the keyboard nudges -------------------------------------------------------------------------

    [Fact]
    public async Task Play_pause_next_and_previous_reach_the_session_Async()
    {
        await PlayAnAlbumAsync();
        _engine.Drain();

        await _vm.PlayPauseAsync();
        _engine.Drain().Should().Equal("pause");
        _vm.IsPlaying.Should().BeFalse();
        _vm.PlayPauseLabel.Should().Be("Play");

        await _vm.PlayPauseAsync();
        _engine.Drain().Should().Equal("resume");
        _vm.PlayPauseLabel.Should().Be("Pause");

        await _vm.NextAsync();
        _session.Queue.CurrentIndex.Should().Be(1);
    }

    [Fact]
    public async Task The_seek_accelerators_move_by_their_step_and_stop_at_the_ends_Async()
    {
        await PlayAnAlbumAsync();
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(100) };
        await TickAsync();
        _engine.Drain();

        await _vm.NudgeAsync(TimeSpan.FromSeconds(5));
        _engine.Drain().Should().Equal("seek:105000");

        await _vm.NudgeAsync(TimeSpan.FromSeconds(-30));
        _engine.Drain().Should().Equal("seek:75000");

        await _vm.NudgeAsync(TimeSpan.FromHours(-1));
        _engine.Drain().Should().ContainSingle("a nudge past the start is a seek to the start, not a negative one")
            .Which.Should().Be("seek:0");
    }

    [Fact]
    public async Task Nothing_the_panel_can_do_throws_before_a_track_is_loaded_Async()
    {
        await _vm.PlayPauseAsync();
        await _vm.NextAsync();
        await _vm.PreviousAsync();
        await _vm.NudgeAsync(TimeSpan.FromSeconds(5));
        await _vm.ToggleShuffleAsync();
        await _vm.CycleRepeatAsync();
        _vm.ToggleMute();
        _vm.ToggleTimeDisplay();
        await _vm.CommitScrubAsync();

        _vm.HasTrack.Should().BeFalse();
        _engine.Calls.Should().NotContain(c => c.StartsWith("seek", StringComparison.Ordinal));
    }

    // ---- arriving before audio does -------------------------------------------------------------------------------

    [Fact]
    public async Task A_panel_built_before_audio_started_picks_the_session_up_when_it_arrives_Async()
    {
        var source = new StubSessionSource();
        using var early = new TransportViewModel(source);
        early.IsReady.Should().BeFalse();
        await early.PlayPauseAsync();
        _engine.Calls.Should().BeEmpty();

        source.Session = _session;

        early.IsReady.Should().BeTrue();
        await PlayAnAlbumAsync();
        _engine.Drain();
        await early.PlayPauseAsync();
        _engine.Drain().Should().Equal("pause");
    }

}
#pragma warning restore CA1001
