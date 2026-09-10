using Tunqio.Core.Audio;

namespace Tunqio.Core.Tests;

/// <summary>E1-S3: the clock says when a mixer position (a join reported in <see cref="EngineEvent.B"/>) has been heard.</summary>
public class PlaybackClockTests
{
    [Fact]
    public void A_join_is_heard_once_it_has_left_the_output_buffer()
    {
        const long Join = 96_000 * 8;
        var mixedButBuffered = new PlaybackClock(TimeSpan.FromSeconds(2), MixerBytePosition: Join + 1000, TimeSpan.FromMilliseconds(200), 0, OutputBufferedBytes: 4000);
        mixedButBuffered.AudibleMixerBytePosition.Should().Be(Join - 3000);
        mixedButBuffered.HasPlayed(Join).Should().BeFalse("the join is still in the buffer");

        var drained = mixedButBuffered with { MixerBytePosition = Join + 4000 };
        drained.HasPlayed(Join).Should().BeTrue("exactly at the boundary counts as heard");

        var headless = new PlaybackClock(TimeSpan.Zero, Join, TimeSpan.Zero, 0, 0);
        headless.HasPlayed(Join).Should().BeTrue("nothing is buffered without a device");
        headless.HasPlayed(Join + 1).Should().BeFalse();
    }
}
