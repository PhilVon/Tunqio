using Tunqio.Core.Audio;
using Tunqio.Core.Library;

namespace Tunqio.Core.Playback;

/// <summary>
/// Decides how one track follows the next (E1-S4): the engine offers a gapless join and a crossfade per boundary, and
/// only the session knows the albums. Two consecutive tracks of one album are joined gapless when gapless is on, so
/// the crossfade never cuts into a continuous album; every other boundary takes the user crossfade (which the engine
/// makes a gapless join when the crossfade is 0). Pure; <c>PlaybackSession</c> (E1-S10) calls it per preload.
/// </summary>
public static class CrossfadePolicy
{
    /// <summary>The longest crossfade the engine applies (<c>mp_engine_set_crossfade</c> clamps to it).</summary>
    public static readonly TimeSpan MaxCrossfade = TimeSpan.FromSeconds(12);

    /// <summary>The <c>playback.crossfadeMs</c> setting as the engine takes it: 0 to 12 000 ms.</summary>
    public static TimeSpan Duration(int crossfadeMs) => TimeSpan.FromMilliseconds(Math.Clamp(crossfadeMs, 0, (int)MaxCrossfade.TotalMilliseconds));

    /// <summary>
    /// The join for <paramref name="next"/> after <paramref name="current"/>: gapless when <paramref name="gapless"/> is on
    /// and both tracks belong to the same album, otherwise the crossfade.
    /// </summary>
    public static JoinMode Resolve(TrackDto current, TrackDto next, bool gapless) =>
        gapless && SameAlbum(current, next) ? JoinMode.Gapless : JoinMode.Crossfade;

    /// <summary>
    /// Same album: the same library album, or, for tracks the scanner could not attach to one, the same album title and
    /// album artist (a compilation's tracks share those without sharing a track artist). Untitled tracks are never one album.
    /// </summary>
    public static bool SameAlbum(TrackDto a, TrackDto b)
    {
        if (a.AlbumId is long id && b.AlbumId is long other)
        {
            return id == other;
        }

        if (a.AlbumId is not null || b.AlbumId is not null || string.IsNullOrWhiteSpace(a.AlbumTitle) || string.IsNullOrWhiteSpace(b.AlbumTitle))
        {
            return false;
        }

        return string.Equals(a.AlbumTitle.Trim(), b.AlbumTitle.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.AlbumArtist?.Trim() ?? string.Empty, b.AlbumArtist?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
