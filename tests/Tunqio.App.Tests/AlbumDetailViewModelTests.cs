using Tunqio.App.Controls;
using Tunqio.App.Library;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

/// <summary>E3-S8: album detail groups by disc and plays the album from any track.</summary>
public class AlbumDetailViewModelTests
{
    private readonly FakeAlbumRepository _albums = new();
    private readonly FakePlayback _playback = new();
    private readonly FakeNavigator _navigator = new();
    private readonly FakeRevealer _revealer = new();

    public AlbumDetailViewModelTests()
    {
        _albums.Rows.Add(Rows.Album(1, "Double", artist: "The Band", artistId: 10));
        _albums.Tracks[1] =
        [
            Rows.Track(11, "One", disc: 1, trackNo: 1, albumTitle: "Double", albumArtist: "The Band"),
            Rows.Track(12, "Two", disc: 1, trackNo: 2, albumTitle: "Double", albumArtist: "The Band", credits: [new ArtistRef(10, "The Band"), new ArtistRef(20, "Guest")]),
            Rows.Track(13, "Three", disc: 2, trackNo: 1, albumTitle: "Double", albumArtist: "The Band", durationMs: 3_600_000),
        ];
        _albums.Genres[1] = ["Rock", "Pop"];

        _albums.Rows.Add(Rows.Album(2, "Single", artist: null, artistId: null));
        _albums.Tracks[2] = [Rows.Track(21, "Only", disc: null, codec: "mp3", bitDepth: null, sampleRate: 44100)];
    }

    private readonly FakeRater _rater = new();

    private AlbumDetailViewModel Create() => new(_albums, _playback, _navigator, _revealer, _rater);

    // ---- E6-S7: the rating column ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_rows_stars_go_through_the_rater_and_follow_its_event_in_place_Async()
    {
        _albums.Tracks[1][0] = _albums.Tracks[1][0] with { Rating = 60 };
        AlbumDetailViewModel vm = Create();
        await vm.LoadAsync(1);
        vm.ListenForRatings(true);
        AlbumTrackRow one = vm.Rows.Single(r => r.Title == "One");
        one.Stars.Should().Be(3, "the row is built with the track's rating as stars");
        var notified = new List<string>();
        one.PropertyChanged += (_, e) => notified.Add(e.PropertyName ?? string.Empty);

        await vm.RateAsync(one, 5);

        _rater.Requests.Should().Equal((11L, 5));
        one.Stars.Should().Be(5, "the same row object is patched, so the bound control follows without a reload");
        notified.Should().Contain(nameof(AlbumTrackRow.Stars));
        ReferenceEquals(vm.Rows.Single(r => r.Title == "One"), one).Should().BeTrue();

        _rater.RaiseChanged(12, 20);
        vm.Rows.Single(r => r.Title == "Two").Stars.Should().Be(1, "a rating set elsewhere reaches the row");

        vm.ListenForRatings(false);
        _rater.RaiseChanged(11, null);
        one.Stars.Should().Be(5, "a page that has left the tree no longer follows");
    }

    [Fact]
    public async Task Tracks_are_grouped_by_disc_with_headers_and_credits_shown_only_when_they_differ_Async()
    {
        AlbumDetailViewModel vm = Create();
        await vm.LoadAsync(1);

        vm.Title.Should().Be("Double");
        vm.ArtistName.Should().Be("The Band");
        vm.HasArtistLink.Should().BeTrue();
        vm.Subline.Should().Be("2001 · Rock, Pop");
        vm.Summary.Should().Be("3 tracks · 1 h 8 min · 2 discs · FLAC 16/44.1");
        vm.HasMultipleDiscs.Should().BeTrue();
        vm.Discs.Select(d => d.Title).Should().Equal("Disc 1", "Disc 2");
        vm.Discs[0].Rows.Select(r => r.Title).Should().Equal("One", "Two");
        vm.Discs[1].Rows.Select(r => r.Title).Should().Equal("Three");
        vm.Rows.Select(r => r.Artists).Should().Equal(string.Empty, "The Band, Guest", string.Empty);
        vm.Rows.Select(r => r.Number).Should().Equal("1", "2", "1");
    }

    [Fact]
    public async Task A_single_disc_album_has_one_group_and_no_disc_count_Async()
    {
        AlbumDetailViewModel vm = Create();
        await vm.LoadAsync(2);

        vm.HasMultipleDiscs.Should().BeFalse();
        vm.Discs.Should().HaveCount(1);
        vm.HasArtistLink.Should().BeFalse();
        vm.Summary.Should().Be("1 track · 4 min · MP3", "a format line without a uniform bit depth is the codec alone");
        vm.Subline.Should().Be("2001");
    }

    [Fact]
    public async Task Playing_from_a_track_queues_the_whole_album_with_that_track_current_Async()
    {
        AlbumDetailViewModel vm = Create();
        await vm.LoadAsync(1);

        await vm.PlayFromAsync(vm.Rows[2]);
        _playback.Last.Should().Be(new FakePlayback.Request("play", [11, 12, 13], 2, false));

        await vm.PlayAsync();
        _playback.Last.Should().Be(new FakePlayback.Request("play", [11, 12, 13], 0, false));

        await vm.ShuffleAsync();
        _playback.Last.Shuffle.Should().BeTrue();

        await vm.PlayNextAsync();
        _playback.Last.Kind.Should().Be("next");
        await vm.EnqueueAsync();
        _playback.Last.Should().Be(new FakePlayback.Request("enqueue", [11, 12, 13], 0, false));
    }

    [Fact]
    public async Task Row_actions_play_from_a_single_row_but_only_the_selection_when_several_are_selected_Async()
    {
        AlbumDetailViewModel vm = Create();
        await vm.LoadAsync(1);

        await vm.HandleAsync(TrackAction.Play, [vm.Rows[1]], vm.Rows[1]);
        _playback.Last.Should().Be(new FakePlayback.Request("play", [11, 12, 13], 1, false));

        await vm.HandleAsync(TrackAction.Play, [vm.Rows[0], vm.Rows[2]], vm.Rows[2]);
        _playback.Last.Should().Be(new FakePlayback.Request("play", [11, 13], 0, false));

        await vm.HandleAsync(TrackAction.PlayNext, [vm.Rows[2]], vm.Rows[2]);
        _playback.Last.Should().Be(new FakePlayback.Request("next", [13], 0, false));

        await vm.HandleAsync(TrackAction.OpenArtist, [vm.Rows[1]], vm.Rows[1]);
        await vm.HandleAsync(TrackAction.ShowInFolder, [vm.Rows[1]], null);
        vm.OpenArtist();
        vm.ShowInFolder();

        _navigator.Opened.Should().Equal(("artist", 10L), ("artist", 10L));
        _revealer.Revealed.Should().Equal(vm.Rows[1].Track.Path, vm.Rows[0].Track.Path);
    }

    [Fact]
    public async Task An_unknown_album_is_reported_and_plays_nothing_Async()
    {
        AlbumDetailViewModel vm = Create();
        await vm.LoadAsync(99);

        vm.NotFound.Should().BeTrue();
        vm.CanPlay.Should().BeFalse();
        await vm.PlayAsync();
        _playback.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("flac", 24, 96000, "FLAC 24/96")]
    [InlineData("flac", 16, 44100, "FLAC 16/44.1")]
    [InlineData("opus", null, 48000, "OPUS")]
    public void Format_summary_names_the_resolution_when_the_tracks_agree(string codec, int? depth, int rate, string expected)
    {
        TrackDto[] tracks = [Rows.Track(1, "a", codec: codec, bitDepth: depth, sampleRate: rate), Rows.Track(2, "b", codec: codec, bitDepth: depth, sampleRate: rate)];
        AlbumDetailViewModel.FormatSummary(tracks).Should().Be(expected);
    }

    [Fact]
    public void Format_summary_lists_mixed_codecs()
    {
        TrackDto[] tracks = [Rows.Track(1, "a", codec: "mp3"), Rows.Track(2, "b", codec: "flac"), Rows.Track(3, "c", codec: "flac", sampleRate: 48000)];
        AlbumDetailViewModel.FormatSummary(tracks).Should().Be("FLAC, MP3");
        AlbumDetailViewModel.FormatSummary([tracks[1], tracks[2]]).Should().Be("FLAC", "the same codec at two rates is just the codec");
    }
}
