using FluentAssertions;
using Tunqio.Core.Library;

namespace Tunqio.Core.Tests;

/// <summary>
/// E3-S11: when a listen counts as a play — past half the track or past four minutes, whichever comes first
/// (docs/library-and-data.md, "Play history rules").
/// </summary>
public class PlayCompletionTests
{
    [Theory]
    [InlineData(180, 90)]      // a three-minute track: half of it
    [InlineData(600, 240)]     // a ten-minute track: the four-minute cap comes first
    [InlineData(480, 240)]     // exactly at the cap
    [InlineData(0, 240)]       // unknown length: the cap alone
    [InlineData(-5, 240)]      // nonsense length: the same
    public void The_threshold_is_the_shorter_of_half_the_track_and_four_minutes(int durationSeconds, int expectedSeconds)
    {
        PlayCompletion.Threshold(TimeSpan.FromSeconds(durationSeconds))
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Skipping_a_track_after_ten_seconds_is_not_a_play()
    {
        PlayCompletion.IsComplete(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(3)).Should().BeFalse();
    }

    [Fact]
    public void Listening_past_half_a_track_is_a_play()
    {
        PlayCompletion.IsComplete(TimeSpan.FromSeconds(91), TimeSpan.FromMinutes(3)).Should().BeTrue();
    }

    [Fact]
    public void Exactly_half_is_not_yet_a_play()
    {
        PlayCompletion.IsComplete(TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(3)).Should().BeFalse();
    }

    [Fact]
    public void A_long_track_completes_at_four_minutes_without_reaching_its_half()
    {
        PlayCompletion.IsComplete(TimeSpan.FromMinutes(4.1), TimeSpan.FromMinutes(20)).Should().BeTrue();
        PlayCompletion.IsComplete(TimeSpan.FromMinutes(3.9), TimeSpan.FromMinutes(20)).Should().BeFalse();
    }

    [Fact]
    public void A_track_of_unknown_length_still_has_to_be_heard_for_four_minutes()
    {
        PlayCompletion.IsComplete(TimeSpan.FromSeconds(1), TimeSpan.Zero).Should().BeFalse();
        PlayCompletion.IsComplete(TimeSpan.FromMinutes(5), TimeSpan.Zero).Should().BeTrue();
    }

    [Fact]
    public void An_event_carries_the_heard_time_and_the_verdict()
    {
        PlayEvent complete = PlayEvent.For(7, 1_700_000_000_000, TimeSpan.FromSeconds(100), TimeSpan.FromMinutes(3));

        complete.Should().Be(new PlayEvent(7, 1_700_000_000_000, 100_000, Completed: true));

        PlayEvent skipped = PlayEvent.For(7, 1_700_000_000_000, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(3));

        skipped.PlayedMs.Should().Be(10_000);
        skipped.Completed.Should().BeFalse();
    }

    [Fact]
    public void Negative_heard_time_is_clamped_away()
    {
        PlayEvent.For(7, 1, TimeSpan.FromSeconds(-4), TimeSpan.FromMinutes(3)).PlayedMs.Should().Be(0);
    }
}
