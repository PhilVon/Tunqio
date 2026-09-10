using Tunqio.Core.Library;

namespace Tunqio.Core.Playback;

/// <summary>The <c>playback.replayGain</c> setting: which tagged gain to apply, if any.</summary>
public enum ReplayGainMode
{
    Off,
    Track,
    Album,
}

/// <summary>
/// What <see cref="Audio.IAudioEngine.SetReplayGain"/> takes for one track: the whole gain in dB (tag plus preamp) and the
/// tagged linear peak the core limits against (0 = unknown, no limiting).
/// </summary>
public readonly record struct ReplayGainSetting(float GainDb, float Peak)
{
    /// <summary>No gain and no peak information: the track plays as decoded.</summary>
    public static ReplayGainSetting Unity => new(0f, 0f);
}

/// <summary>
/// Turns a track's ReplayGain tags, the mode and the preamp into the pair the engine applies (E1-S5). Pure; the session
/// (E1-S10) calls it for every track it opens. Album mode falls back to the track values when the album ones are missing,
/// track mode the other way round, and the peak used is the one tagged beside the gain that was chosen. A track with no
/// usable gain plays at unity: the preamp is part of ReplayGain, so it is not applied to files that carry none.
/// </summary>
public static class ReplayGainPolicy
{
    /// <summary>Parses the setting's string value; anything unrecognised is the documented default (album).</summary>
    public static ReplayGainMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "off" => ReplayGainMode.Off,
        "track" => ReplayGainMode.Track,
        "album" => ReplayGainMode.Album,
        _ => ParseMode(SettingsKeys.Defaults.PlaybackReplayGain),
    };

    public static ReplayGainSetting Resolve(ReplayGainTags? tags, ReplayGainMode mode, float preampDb)
    {
        if (mode == ReplayGainMode.Off || tags is null)
        {
            return ReplayGainSetting.Unity;
        }

        (double? gain, double? peak) = mode == ReplayGainMode.Album
            ? tags.AlbumGainDb is not null ? (tags.AlbumGainDb, tags.AlbumPeak) : (tags.TrackGainDb, tags.TrackPeak)
            : tags.TrackGainDb is not null ? (tags.TrackGainDb, tags.TrackPeak) : (tags.AlbumGainDb, tags.AlbumPeak);
        if (gain is null || !double.IsFinite(gain.Value))
        {
            return ReplayGainSetting.Unity;
        }

        float peakValue = peak is { } p && double.IsFinite(p) && p > 0 ? (float)p : 0f;
        float preamp = float.IsFinite(preampDb) ? preampDb : 0f;
        return new ReplayGainSetting((float)gain.Value + preamp, peakValue);
    }
}
