using Tunqio.App.Controls;
using Tunqio.App.Library;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

/// <summary>E3-S8: the Artists list and its jump bar, artist detail, the genre cloud and the folder list.</summary>
public class ArtistsGenresFoldersTests
{
    private readonly FakeArtistRepository _artists = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeAlbumRepository _albums = new();
    private readonly FakePlayback _playback = new();
    private readonly FakeNavigator _navigator = new();

    [Fact]
    public async Task A_jump_loads_pages_until_the_letter_is_in_the_list_and_answers_with_its_index_Async()
    {
        string[] names = ["#1 Hits", "Abba", "Beck", "Cake", "Doves", "Elbow", "Fugazi", "Gorillaz", "Hole", "Interpol", "James", "Kraftwerk", "Low", "Muse", "Nas", "Oasis", "Pulp", "Queen", "Ride", "Suede"];
        for (int i = 0; i < names.Length; i++)
        {
            _artists.Rows.Add(new ArtistDto(i + 1, names[i], names[i], null, 1, 10, null));
        }

        var vm = new ArtistsViewModel(_artists, _navigator);
        await vm.LoadAsync();
        vm.Items!.PageSize.Should().Be(200);

        // Page through a small window so the jump has to load.
        vm.Items = new IncrementalItemsSource<ArtistDto>(PageLoaders.Artists(_artists, new ArtistQuery(PageSize: 5)), 5);
        await vm.Items.LoadMoreAsync();
        int pagesBefore = _artists.Pages;

        (await vm.JumpToAsync("M")).Should().Be(13);
        vm.Items.Count.Should().Be(15, "three pages of five bring Muse into view");
        (_artists.Pages - pagesBefore).Should().Be(2);
        (await vm.JumpToAsync("A")).Should().Be(1);
        (await vm.JumpToAsync("#")).Should().Be(0);
        (await vm.JumpToAsync("Z")).Should().Be(-1, "nothing sorts at or after Z");
        vm.Items.HasMore.Should().BeFalse("the failed jump loaded everything");
        (await vm.JumpToAsync("T")).Should().Be(-1);
    }

    [Fact]
    public void Rank_puts_non_letters_first()
    {
        ArtistsViewModel.Rank('#').Should().Be(0);
        ArtistsViewModel.Rank('3').Should().Be(0);
        ArtistsViewModel.Rank('a').Should().Be(1);
        ArtistsViewModel.Rank('Z').Should().Be(26);
        ArtistsViewModel.Letters.Should().HaveCount(27).And.StartWith("#").And.EndWith("Z");
    }

    [Fact]
    public async Task Artist_detail_lists_albums_and_appearances_and_plays_every_credit_Async()
    {
        var artist = new ArtistDto(10, "The Band", "Band, The", null, 1, 3, "abc");
        AlbumDto own = Rows.Album(1, "Own", artistId: 10);
        AlbumDto guest = Rows.Album(2, "Guest spot", artist: "Other", artistId: 20);
        _artists.Details[10] = new ArtistDetailDto(artist, [own], [guest]);
        _albums.Rows.AddRange([own, guest]);
        _albums.Tracks[2] = [Rows.Track(21, "g", albumId: 2)];
        _tracks.Rows.AddRange(
        [
            Rows.Track(1, "b", albumId: 1, albumTitle: "Own", trackNo: 2),
            Rows.Track(2, "a", albumId: 1, albumTitle: "Own", trackNo: 1),
            Rows.Track(3, "guest", albumId: 2, albumTitle: "Guest spot", albumArtist: "Other", credits: [new ArtistRef(20, "Other"), new ArtistRef(10, "The Band")]),
            Rows.Track(4, "unrelated", albumId: 3, albumTitle: "No", albumArtist: "Other", credits: [new ArtistRef(20, "Other")]),
        ]);

        var vm = new ArtistDetailViewModel(_artists, _tracks, _playback, new AlbumActions(_albums, _playback, _navigator, new FakeRevealer()));
        await vm.LoadAsync(10);

        vm.Name.Should().Be("The Band");
        vm.Summary.Should().Be("1 album · 3 tracks");
        vm.ArtHash.Should().Be("abc");
        (vm.HasAlbums, vm.HasAppearsOn).Should().Be((true, true));

        await vm.PlayAllAsync();
        _playback.Last.Should().Be(new FakePlayback.Request("play", [3, 2, 1], 0, false), "album order: 'Guest spot' before 'Own', then track numbers");

        await vm.HandleAsync(AlbumAction.Enqueue, guest);
        _playback.Last.Should().Be(new FakePlayback.Request("enqueue", [21], 0, false));

        var missing = new ArtistDetailViewModel(_artists, _tracks, _playback, new AlbumActions(_albums, _playback, _navigator, new FakeRevealer()));
        await missing.LoadAsync(99);
        missing.NotFound.Should().BeTrue();
    }

    [Fact]
    public async Task The_genre_cloud_sizes_tags_by_count_and_opens_the_filtered_tracks_view_Async()
    {
        var genres = new FakeGenreRepository();
        genres.Rows.AddRange([new GenreDto(1, "Rock", 1000), new GenreDto(2, "Jazz", 10), new GenreDto(3, "Silent", 0), new GenreDto(4, "Folk", 1)]);
        var vm = new GenresViewModel(genres, _navigator);

        await vm.LoadAsync();

        vm.Tags.Select(t => t.Label).Should().Equal("Rock (1000)", "Jazz (10)", "Folk (1)");
        vm.Tags[0].FontSize.Should().Be(GenresViewModel.MaxFontSize);
        vm.Tags[1].FontSize.Should().BeInRange(GenresViewModel.MinFontSize + 1, GenresViewModel.MaxFontSize - 1);
        vm.Tags[2].FontSize.Should().BeGreaterThan(GenresViewModel.MinFontSize).And.BeLessThan(vm.Tags[1].FontSize);
        GenresViewModel.FontSizeFor(0, 100).Should().Be(GenresViewModel.MinFontSize);

        vm.Open(vm.Tags[1]);
        _navigator.Opened.Should().Equal(new TracksSpec(TracksKind.Genre, 2, "Jazz"));
    }

    [Fact]
    public async Task Folders_are_named_by_their_last_segment_and_open_the_filtered_tracks_view_Async()
    {
        var folders = new FakeFolderRepository();
        folders.Rows.AddRange(
        [
            new LibraryFolderDto(1, @"D:\Music\", true, 1, "completed"),
            new LibraryFolderDto(2, @"E:\", false, null, null),
        ]);
        var vm = new FoldersViewModel(folders, _navigator);

        await vm.LoadAsync();

        vm.Rows.Select(r => r.Name).Should().Equal("Music", "E:");
        vm.Rows.Select(r => r.Detail).Should().Equal(@"D:\Music\ · last scan completed", @"E:\ · disabled");
        vm.Open(vm.Rows[1]);
        _navigator.Opened.Should().Equal(new TracksSpec(TracksKind.Folder, 2, @"E:\"));
        FoldersViewModel.NameOf(@"C:\Users\phil\Music").Should().Be("Music");
    }
}
