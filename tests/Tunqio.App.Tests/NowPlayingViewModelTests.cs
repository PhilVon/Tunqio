using Tunqio.App.Controls;
using Tunqio.App.Shell;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;

namespace Tunqio.App.Tests;

/// <summary>
/// E2-S3: the Now Playing panel's metadata over a real <see cref="PlaybackSession"/> and a fake engine. What the
/// art <em>looks</em> like is XAML and is checked on the live tree by the Now Playing spike; what it is derived
/// from — the hash colour and initial for a track with no art (AC-74), and the one notification per real change
/// that keeps a 1000 px decode from being asked for ten times a second (AC-73) — is behaviour, and is here.
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class NowPlayingViewModelTests : IAsyncLifetime
{
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeSettings _settings = new();
    private readonly FakePlayHistory _history = new();
    private readonly FakeQueueStore _queues = new();
    private readonly StubSessionSource _source = new();
    private readonly FakeNavigator _navigator = new();
    private readonly FakeRater _rater = new();
    private PlaybackSession _session = null!;
    private NowPlayingViewModel _vm = null!;

    public Task InitializeAsync()
    {
        _tracks.Rows.AddRange([
            Rows.Track(11, "Wide Awake", albumTitle: "City Lights", credits: [new ArtistRef(7, "Nova")], artHash: "aaaa1111"),
            Rows.Track(12, "Second Light", albumTitle: "City Lights", credits: [new ArtistRef(7, "Nova")], artHash: "aaaa1111"),
            Rows.Track(13, "Loose Tape", albumId: null, albumTitle: null, credits: [new ArtistRef(9, "Kestrel")], codec: "mp3", bitDepth: null, bitrateKbps: 320, year: null),
        ]);
        _session = new PlaybackSession(_engine, _tracks, _history, _queues, _settings, autoPoll: false);
        _source.Session = _session;
        _vm = new NowPlayingViewModel(_source, _rater, _navigator);
        return Task.CompletedTask;
    }

    // ---- E6-S7: the stars ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_stars_come_from_the_track_and_rating_it_goes_through_the_rater_Async()
    {
        _tracks.Rows[0] = _tracks.Rows[0] with { Rating = 60 };
        await PlayAsync(11);

        _vm.Stars.Should().Be(3, "60 on the 0..100 scale is three stars");
        _vm.CanRate.Should().BeTrue("a library row can be rated");

        _vm.Rate(5).Should().BeTrue();

        _rater.Requests.Should().Equal((11L, 5));
        _vm.Stars.Should().Be(5, "the rater's Changed event patches the shown track without a new snapshot");
        _vm.Track!.Rating.Should().Be(100);

        // The next snapshot carries the session's older copy of the row; the patched rating must not be undone by it.
        await TickAsync();
        _vm.Stars.Should().Be(5, "a snapshot that says the same track is not a change, whatever its copy of the row says");
    }

    [Fact]
    public async Task A_rating_set_elsewhere_reaches_the_panel_and_one_for_another_track_does_not_Async()
    {
        await PlayAsync(11, 12);
        var changes = new List<string>();
        _vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName ?? string.Empty);

        _rater.RaiseChanged(12, 80);
        _vm.Stars.Should().Be(0, "track 12 is queued, not showing");
        changes.Should().NotContain(nameof(NowPlayingViewModel.Stars));

        _rater.RaiseChanged(11, 40);
        _vm.Stars.Should().Be(2);
        changes.Should().Contain(nameof(NowPlayingViewModel.Stars));

        _rater.RaiseChanged(11, null);
        _vm.Stars.Should().Be(0, "cleared");
    }

    [Fact]
    public async Task Nothing_playing_or_no_rater_cannot_be_rated_and_says_so_Async()
    {
        _vm.CanRate.Should().BeFalse("nothing is playing");
        _vm.Rate(3).Should().BeFalse("the shortcut leaves the key unhandled");
        _rater.Requests.Should().BeEmpty();

        using var plain = new NowPlayingViewModel(_source, rater: null, _navigator);
        await PlayAsync(11);
        plain.CanRate.Should().BeFalse("the spike modes have no rater and the stars are read-only");
        plain.Rate(3).Should().BeFalse();
        _vm.CanRate.Should().BeTrue();
    }

    public async Task DisposeAsync()
    {
        _vm.Dispose();
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
    }

    /// <summary>What the 10 Hz timer would do; the session is built with autoPoll off so a test decides when.</summary>
    private Task TickAsync() => _session.PollAsync();

    private async Task PlayAsync(params long[] ids)
    {
        await _session.PlayNowAsync(ids);
        await TickAsync();
    }

    // ---- the metadata the panel draws ------------------------------------------------------------------------------

    [Fact]
    public async Task An_empty_session_is_the_nothing_playing_state_Async()
    {
        _vm.HasTrack.Should().BeFalse();
        _vm.IsEmpty.Should().BeTrue();
        _vm.Title.Should().BeEmpty();
        _vm.AutomationName.Should().Be("Nothing playing");

        await PlayAsync(11);
        _vm.IsEmpty.Should().BeFalse("something is loaded now");
    }

    [Fact]
    public async Task Title_artists_album_and_year_come_from_the_current_track_Async()
    {
        await PlayAsync(11, 12);

        _vm.Title.Should().Be("Wide Awake");
        _vm.ArtistNames.Should().Be("Nova");
        _vm.AlbumTitle.Should().Be("City Lights");
        _vm.YearText.Should().Be("2001");
        _vm.AlbumLine.Should().Be("City Lights (2001)");
        _vm.AutomationName.Should().Be("Now playing: Wide Awake by Nova, from City Lights (2001)");
    }

    [Fact]
    public async Task The_metadata_follows_the_boundary_rather_than_the_call_that_asked_Async()
    {
        await PlayAsync(11, 12);
        _vm.Title.Should().Be("Wide Awake");

        await _session.NextAsync();
        await TickAsync();
        _vm.Title.Should().Be("Second Light");
    }

    [Fact]
    public async Task A_track_with_no_album_shows_what_it_has_and_leaves_the_rest_out_Async()
    {
        await PlayAsync(13);

        _vm.Title.Should().Be("Loose Tape");
        _vm.AlbumTitle.Should().BeEmpty();
        _vm.YearText.Should().BeEmpty();
        _vm.AlbumLine.Should().BeEmpty("neither half of the line is known, so there is no line");
        _vm.HasAlbumLine.Should().BeFalse();
        _vm.AutomationName.Should().Be("Now playing: Loose Tape by Kestrel");
    }

    // ---- AC-74: the placeholder is derived from the title hash ----------------------------------------------------

    [Fact]
    public async Task Every_track_of_an_album_gets_the_albums_placeholder_not_its_own_Async()
    {
        await PlayAsync(11, 12);
        _vm.ArtTitle.Should().Be("City Lights");

        await _session.NextAsync();
        await TickAsync();
        _vm.ArtTitle.Should().Be("City Lights", "the placeholder must not change colour mid-album");
        _vm.Title.Should().Be("Second Light", "though the track did change");
    }

    [Fact]
    public async Task A_track_with_no_album_falls_back_to_its_own_title_Async()
    {
        await PlayAsync(13);
        _vm.ArtTitle.Should().Be("Loose Tape");
    }

    [Fact]
    public void The_placeholder_colour_and_initial_are_a_deterministic_function_of_that_title()
    {
        PlaceholderArt.Initials("City Lights").Should().Be("CL");
        PlaceholderArt.Initials(null).Should().Be("♪", "an untitled track still needs something in the tile");

        // The same title gives the same bucket every session, which is what "deterministic" is for: an album
        // does not change colour between runs. Asserted as a literal rather than against a second call, which
        // any pure function would pass — the claim is that this colour survives a restart.
        var cityLights = new Windows.UI.Color { A = 255, R = 123, G = 50, B = 62 };
        PlaceholderArt.ColorFor("City Lights").Should().Be(cityLights);
        PlaceholderArt.ColorFor("city lights").Should().Be(cityLights, "the hash folds case, so tag casing does not restyle an album");
        PlaceholderArt.ColorFor("Loose Tape").Should().NotBe(cityLights);
    }

    [Fact]
    public async Task The_art_hash_is_handed_on_so_the_image_layer_can_find_the_file_Async()
    {
        await PlayAsync(11);
        _vm.ArtHash.Should().Be("aaaa1111");

        await PlayAsync(13);
        _vm.ArtHash.Should().BeNull("nothing was extracted for it, so the placeholder is what shows");
    }

    // ---- AC-73: one notification per real change -------------------------------------------------------------------

    [Fact]
    public async Task Ten_snapshots_of_the_same_track_are_one_change_not_ten_Async()
    {
        await PlayAsync(11, 12);
        int artChanges = 0;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NowPlayingViewModel.ArtHash))
            {
                artChanges++;
            }
        };

        // A second of playback at the published rate. The art binding builds a BitmapImage over a 1000 px file
        // every time this fires, so anything but zero here is that decode being asked for again for nothing.
        for (int i = 0; i < 10; i++)
        {
            await TickAsync();
        }

        artChanges.Should().Be(0);

        await _session.NextAsync();
        await TickAsync();
        artChanges.Should().Be(1, "the track changed once");
    }

    [Fact]
    public async Task Replaying_the_same_track_is_a_change_Async()
    {
        await PlayAsync(11);
        int changes = 0;
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NowPlayingViewModel.Title))
            {
                changes++;
            }
        };

        await _session.PlayNowAsync([11]);
        await TickAsync();

        changes.Should().Be(1, "a fresh queue item is a fresh instance even when it is the same track");
    }

    // ---- the format badge -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_badge_reads_a_lossless_track_as_depth_and_rate_and_a_lossy_one_as_a_bit_rate_Async()
    {
        await PlayAsync(11);
        _vm.FormatBadge.Should().Be("FLAC 16/44.1");
        _vm.HasFormatBadge.Should().BeTrue();

        await PlayAsync(13);
        _vm.FormatBadge.Should().Be("MP3 320 kbps");
    }

    [Theory]
    [InlineData("flac", 24, 96_000, null, "FLAC 24/96")]
    [InlineData("flac", 16, 44_100, null, "FLAC 16/44.1")]
    [InlineData("wav", 24, 88_200, null, "WAV 24/88.2")]
    [InlineData("alac", null, 48_000, null, "ALAC 48 kHz")]
    [InlineData("flac", null, null, null, "FLAC")]
    [InlineData("mp3", null, 44_100, 320, "MP3 320 kbps")]
    [InlineData("opus", null, 48_000, null, "OPUS")]
    [InlineData("aac", 16, 44_100, 256, "AAC 256 kbps")]
    public void The_badge_never_states_a_number_the_format_does_not_have(
        string codec, int? bitDepth, int? sampleRate, int? bitrateKbps, string expected) =>
        Format.Badge(codec, bitDepth, sampleRate, bitrateKbps).Should().Be(expected);

    [Fact]
    public void An_unknown_codec_has_no_badge() => Format.Badge(null, 16, 44_100, null).Should().BeEmpty();

    // ---- the links ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Artist_and_album_open_the_sidebar_at_the_row_behind_the_name_Async()
    {
        await PlayAsync(11);
        _vm.HasArtistLink.Should().BeTrue();
        _vm.HasAlbumLink.Should().BeTrue();

        _vm.OpenArtist();
        _vm.OpenAlbum();
        _navigator.Opened.Should().Equal([("artist", 7L), ("album", 1L)]);
    }

    [Fact]
    public async Task A_track_that_is_not_on_a_library_album_offers_no_album_link_Async()
    {
        await PlayAsync(13);
        _vm.HasAlbumLink.Should().BeFalse();

        _vm.OpenAlbum();
        _navigator.Opened.Should().BeEmpty("there is no row to navigate to, and a link that does nothing is worse than no link");
    }

    [Fact]
    public async Task With_no_sidebar_the_names_are_text_Async()
    {
        using var plain = new NowPlayingViewModel(_source, rater: null);
        await PlayAsync(11);

        plain.Title.Should().Be("Wide Awake");
        plain.HasArtistLink.Should().BeFalse();
        plain.HasAlbumLink.Should().BeFalse();
        plain.HasArtists.Should().BeTrue("the name is still shown, it just is not a link");
    }
}
#pragma warning restore CA1001
