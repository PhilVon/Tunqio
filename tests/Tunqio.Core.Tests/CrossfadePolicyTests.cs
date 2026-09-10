using Tunqio.Core.Audio;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.Core.Tests;

/// <summary>E1-S4: the crossfade is not applied at gapless album boundaries; every other boundary takes it.</summary>
public class CrossfadePolicyTests
{
    private static TrackDto Track(long id, long? albumId, string? albumTitle = "Album", string? albumArtist = "Artist") => new(
        Id: id,
        FolderId: 1,
        Path: $"C:\\music\\{id}.flac",
        Title: $"Track {id}",
        Artists: [new ArtistRef(1, "Artist")],
        AlbumId: albumId,
        AlbumTitle: albumTitle,
        AlbumArtist: albumArtist,
        TrackNo: (int)id,
        DiscNo: 1,
        Year: 2020,
        DurationMs: 200_000,
        Codec: "flac",
        BitrateKbps: null,
        SampleRate: 44100,
        Channels: 2,
        BitDepth: 16,
        FileSize: 1,
        FileMtime: 0,
        Composer: null,
        Comment: null,
        ReplayGain: null,
        ArtHash: null,
        Mbid: null,
        AddedAt: 0,
        Rating: null,
        PlayCount: 0,
        LastPlayedAt: null,
        Missing: false);

    [Fact]
    public void Consecutive_tracks_of_one_album_join_gapless_when_gapless_is_on()
    {
        CrossfadePolicy.Resolve(Track(1, albumId: 7), Track(2, albumId: 7), gapless: true).Should().Be(JoinMode.Gapless);
    }

    [Fact]
    public void Tracks_of_different_albums_crossfade()
    {
        CrossfadePolicy.Resolve(Track(1, albumId: 7), Track(2, albumId: 8), gapless: true).Should().Be(JoinMode.Crossfade);
    }

    [Fact]
    public void With_gapless_off_even_one_album_crossfades()
    {
        CrossfadePolicy.Resolve(Track(1, albumId: 7), Track(2, albumId: 7), gapless: false).Should().Be(JoinMode.Crossfade);
    }

    [Fact]
    public void Tracks_without_a_library_album_match_on_title_and_album_artist()
    {
        CrossfadePolicy.SameAlbum(Track(1, null, "Live at Leeds", "The Who"), Track(2, null, "live at leeds ", "the who")).Should().BeTrue();
        CrossfadePolicy.SameAlbum(Track(1, null, "Live at Leeds", "The Who"), Track(2, null, "Live at Leeds", "Someone Else")).Should().BeFalse();
        CrossfadePolicy.SameAlbum(Track(1, null, "Live at Leeds", null), Track(2, null, "Live at Leeds", null)).Should().BeTrue();
    }

    [Fact]
    public void An_untitled_or_half_attached_pair_is_never_one_album()
    {
        CrossfadePolicy.SameAlbum(Track(1, null, null), Track(2, null, null)).Should().BeFalse();
        CrossfadePolicy.SameAlbum(Track(1, null, " "), Track(2, null, " ")).Should().BeFalse();
        CrossfadePolicy.SameAlbum(Track(1, albumId: 7), Track(2, null)).Should().BeFalse();
    }

    [Fact]
    public void The_setting_becomes_a_duration_between_0_and_12_s()
    {
        CrossfadePolicy.Duration(5000).Should().Be(TimeSpan.FromSeconds(5));
        CrossfadePolicy.Duration(-3).Should().Be(TimeSpan.Zero);
        CrossfadePolicy.Duration(60_000).Should().Be(CrossfadePolicy.MaxCrossfade);
        SettingsKeys.Defaults.PlaybackCrossfadeMs.Should().Be(0, "the crossfade is off until the user asks for one");
    }
}
