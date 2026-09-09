using Tunqio.App.Controls;
using Tunqio.App.Library;
using Tunqio.Core;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

/// <summary>E3-S8: the Albums grid's sort, chips and tile actions.</summary>
public class AlbumsViewModelTests
{
    private readonly FakeAlbumRepository _albums = new();
    private readonly FakeGenreRepository _genres = new();
    private readonly FakePlayback _playback = new();
    private readonly FakeNavigator _navigator = new();
    private readonly FakeRevealer _revealer = new();
    private readonly FakeSettings _settings = new();

    public AlbumsViewModelTests()
    {
        _albums.Rows.AddRange(
        [
            Rows.Album(1, "Zebra", year: 1994, addedAt: 10, lastPlayedAt: 5),
            Rows.Album(2, "apple", year: 2003, addedAt: 30),
            Rows.Album(3, "Mango", year: 1999, addedAt: 20, lastPlayedAt: 9),
        ]);
        _albums.Tracks[3] = [Rows.Track(31, "x", albumId: 3), Rows.Track(32, "y", albumId: 3)];
        _albums.Facets = new AlbumFacets([1990, 2000], ["flac", "mp3"]);
        _genres.Rows.AddRange([new GenreDto(1, "Rock", 5), new GenreDto(2, "Empty", 0), new GenreDto(3, "Jazz", 1)]);
    }

    private AlbumsViewModel Create() => new(_albums, _genres, new AlbumActions(_albums, _playback, _navigator, _revealer), _settings);

    [Fact]
    public async Task Initialising_builds_the_chip_options_and_loads_the_first_page_by_title_Async()
    {
        AlbumsViewModel vm = Create();
        await vm.InitializeAsync();

        vm.GenreOptions.Select(o => o.Label).Should().Equal("All genres", "Rock", "Jazz");
        vm.DecadeOptions.Select(o => o.Label).Should().Equal("All decades", "1990s", "2000s");
        vm.CodecOptions.Select(o => o.Label).Should().Equal("All formats", "FLAC", "MP3");
        vm.Query.Should().Be(new AlbumQuery(AlbumSort.Title));
        vm.Items!.Select(a => a.Title).Should().Equal("apple", "Mango", "Zebra");
        vm.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Chips_and_sort_shape_the_query_Async()
    {
        AlbumsViewModel vm = Create();
        await vm.InitializeAsync();

        vm.SelectedGenre = vm.GenreOptions[2];
        vm.SelectedDecade = vm.DecadeOptions[1];
        vm.SelectedCodec = vm.CodecOptions[2];
        vm.Query.Should().Be(new AlbumQuery(AlbumSort.Title, GenreId: 3, Decade: 1990, Codec: "mp3"));
        vm.HasFilter.Should().BeTrue();

        vm.SelectedSort = AlbumsViewModel.SortOptions.Single(o => (AlbumSort)o.Value! == AlbumSort.Added);
        vm.Descending.Should().BeTrue("date added opens newest first");
        vm.Query.Sort.Should().Be(AlbumSort.Added);
        _settings.GetValue(SettingsKeys.UiAlbumsSort, string.Empty).Should().Be("added");

        vm.Descending = false;
        vm.SelectedSort = AlbumsViewModel.SortOptions[0];
        vm.Descending.Should().BeFalse();
        vm.SelectedGenre = vm.GenreOptions[0];
        vm.SelectedDecade = vm.DecadeOptions[0];
        vm.SelectedCodec = vm.CodecOptions[0];
        vm.HasFilter.Should().BeFalse();
    }

    [Fact]
    public void The_remembered_sort_is_restored()
    {
        _settings.SetValue(SettingsKeys.UiAlbumsSort, "played");
        AlbumsViewModel vm = Create();
        (vm.Sort, vm.Descending).Should().Be((AlbumSort.Played, true));

        _settings.SetValue(SettingsKeys.UiAlbumsSort, "nonsense");
        Create().Sort.Should().Be(AlbumSort.Title);
    }

    [Fact]
    public async Task Tile_actions_resolve_the_album_to_its_tracks_Async()
    {
        AlbumsViewModel vm = Create();
        AlbumDto mango = _albums.Rows[2];

        await vm.HandleAsync(AlbumAction.Play, mango);
        await vm.HandleAsync(AlbumAction.PlayNext, mango);
        await vm.HandleAsync(AlbumAction.Enqueue, mango);
        await vm.HandleAsync(AlbumAction.Open, mango);
        await vm.HandleAsync(AlbumAction.ShowInFolder, mango);
        await vm.HandleAsync(AlbumAction.Play, _albums.Rows[0]);

        _playback.Requests.Should().Equal(
            new FakePlayback.Request("play", [31, 32], 0, false),
            new FakePlayback.Request("next", [31, 32], 0, false),
            new FakePlayback.Request("enqueue", [31, 32], 0, false));
        _navigator.Opened.Should().Equal(("album", 3L));
        _revealer.Revealed.Should().Equal(_albums.Tracks[3][0].Path);
    }

    [Fact]
    public async Task An_empty_library_shows_the_prompt_but_an_empty_filter_does_not_Async()
    {
        _albums.Rows.Clear();
        AlbumsViewModel vm = Create();
        await vm.InitializeAsync();
        vm.IsEmpty.Should().BeTrue();

        vm.SelectedCodec = vm.CodecOptions[1];
        for (int i = 0; i < 100 && vm.IsEmpty; i++)
        {
            await Task.Delay(10);
        }

        vm.IsEmpty.Should().BeFalse("a filtered view that finds nothing is not an empty library");
    }
}
