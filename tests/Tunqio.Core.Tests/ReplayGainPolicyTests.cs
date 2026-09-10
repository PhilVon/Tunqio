using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.Core.Tests;

/// <summary>E1-S5: the mode, the tags and the preamp become one (gain, peak) pair for the engine.</summary>
public class ReplayGainPolicyTests
{
    private static readonly ReplayGainTags Both = new(TrackGainDb: -6.5, TrackPeak: 0.9, AlbumGainDb: -8.0, AlbumPeak: 0.95);

    [Theory]
    [InlineData("off", ReplayGainMode.Off)]
    [InlineData("track", ReplayGainMode.Track)]
    [InlineData("album", ReplayGainMode.Album)]
    [InlineData(" Album ", ReplayGainMode.Album)]
    [InlineData(null, ReplayGainMode.Album)]
    [InlineData("loud", ReplayGainMode.Album)]
    public void Mode_parses_the_setting_and_defaults_to_album(string? value, ReplayGainMode expected)
    {
        ReplayGainPolicy.ParseMode(value).Should().Be(expected);
        SettingsKeys.Defaults.PlaybackReplayGain.Should().Be("album");
    }

    [Fact]
    public void Album_mode_takes_the_album_pair_and_adds_the_preamp()
    {
        ReplayGainPolicy.Resolve(Both, ReplayGainMode.Album, preampDb: 3f).Should().Be(new ReplayGainSetting(-5f, 0.95f));
    }

    [Fact]
    public void Track_mode_takes_the_track_pair()
    {
        ReplayGainPolicy.Resolve(Both, ReplayGainMode.Track, preampDb: 0f).Should().Be(new ReplayGainSetting(-6.5f, 0.9f));
    }

    [Fact]
    public void Each_mode_falls_back_to_the_other_pair_when_its_own_gain_is_missing()
    {
        var trackOnly = new ReplayGainTags(-4.0, 0.8, null, null);
        var albumOnly = new ReplayGainTags(null, null, -9.0, 1.0);
        ReplayGainPolicy.Resolve(trackOnly, ReplayGainMode.Album, 0f).Should().Be(new ReplayGainSetting(-4f, 0.8f));
        ReplayGainPolicy.Resolve(albumOnly, ReplayGainMode.Track, 0f).Should().Be(new ReplayGainSetting(-9f, 1f));
    }

    [Fact]
    public void A_missing_or_impossible_peak_is_reported_as_unknown()
    {
        ReplayGainPolicy.Resolve(new ReplayGainTags(-4.0, null, null, null), ReplayGainMode.Track, 0f).Peak.Should().Be(0f);
        ReplayGainPolicy.Resolve(new ReplayGainTags(-4.0, -0.5, null, null), ReplayGainMode.Track, 0f).Peak.Should().Be(0f);
        ReplayGainPolicy.Resolve(new ReplayGainTags(-4.0, double.NaN, null, null), ReplayGainMode.Track, 0f).Peak.Should().Be(0f);
    }

    [Fact]
    public void Off_untagged_and_gainless_tracks_play_at_unity_without_the_preamp()
    {
        ReplayGainPolicy.Resolve(Both, ReplayGainMode.Off, preampDb: 6f).Should().Be(ReplayGainSetting.Unity);
        ReplayGainPolicy.Resolve(null, ReplayGainMode.Album, preampDb: 6f).Should().Be(ReplayGainSetting.Unity);
        ReplayGainPolicy.Resolve(new ReplayGainTags(null, 0.9, null, 0.9), ReplayGainMode.Album, 6f).Should().Be(ReplayGainSetting.Unity);
        ReplayGainPolicy.Resolve(new ReplayGainTags(double.PositiveInfinity, 0.9, null, null), ReplayGainMode.Track, 0f).Should().Be(ReplayGainSetting.Unity);
    }

    [Fact]
    public void A_non_finite_preamp_is_ignored()
    {
        ReplayGainPolicy.Resolve(Both, ReplayGainMode.Track, float.NaN).GainDb.Should().Be(-6.5f);
    }
}
