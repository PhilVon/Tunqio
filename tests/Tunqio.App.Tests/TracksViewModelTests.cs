using Tunqio.App.Controls;
using Tunqio.App.Library;
using Tunqio.Core;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

/// <summary>E3-S8: the Tracks views' queries, sorting, column chooser and selection actions.</summary>
public class TracksViewModelTests
{
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakePlayback _playback = new();
    private readonly FakeNavigator _navigator = new();
    private readonly FakeRevealer _revealer = new();
    private readonly FakeSettings _settings = new();
    private readonly FakeRater _rater = new();

    public TracksViewModelTests()
    {
        _tracks.Rows.AddRange(
        [
            Rows.Track(1, "Delta", addedAt: 100, lastPlayedAt: 500, playCount: 3, codec: "mp3"),
            Rows.Track(2, "alpha", addedAt: 300, lastPlayedAt: null, playCount: 0),
            Rows.Track(3, "Charlie", addedAt: 200, lastPlayedAt: 900, playCount: 1, folderId: 2, credits: [new ArtistRef(77, "Someone")], albumId: 5),
            Rows.Track(4, "bravo", addedAt: 400, lastPlayedAt: 700, playCount: 3),
        ]);
    }

    private TracksViewModel Create() => new(_tracks, _playback, _navigator, _revealer, _settings, _rater);

    // ---- E6-S7: the rating column ----------------------------------------------------------------------------------

    /// <summary>
    /// A row's stars go through the rater, and the rater's event patches the row in place — in the loaded list,
    /// without a requery, and only while the page has asked to listen. A rating that lands for a track that is not
    /// in the list is ignored.
    /// </summary>
    [Fact]
    public async Task Rating_a_row_goes_through_the_rater_and_the_row_follows_its_event_Async()
    {
        TracksViewModel vm = Create();
        await vm.LoadAsync(TracksSpec.All);
        int queries = _tracks.Queries.Count;
        vm.ListenForRatings(true);
        TrackDto delta = vm.Items!.Single(t => t.Title == "Delta");

        await vm.RateAsync(delta, 4);

        _rater.Requests.Should().Equal((1L, 4));
        vm.Items!.Single(t => t.Title == "Delta").Rating.Should().Be(80, "the row shows the rating the rater announced");
        _tracks.Queries.Count.Should().Be(queries, "the row is patched, not requeried: a requery would move it under the pointer");

        _rater.RaiseChanged(2, 20);
        vm.Items!.Single(t => t.Title == "alpha").Rating.Should().Be(20, "a rating set elsewhere reaches the row");
        _rater.RaiseChanged(99, 60);
        vm.Items!.Should().HaveCount(4, "a track that is not in the list changes nothing");

        vm.ListenForRatings(false);
        _rater.RaiseChanged(1, null);
        vm.Items!.Single(t => t.Title == "Delta").Rating.Should().Be(80, "a page that has left the tree no longer follows");
    }

    [Fact]
    public void Fixed_views_have_fixed_queries_and_a_cap()
    {
        TracksSpec.RecentlyAdded.Query(TrackSort.Codec, false).Should().Be(new TrackQuery(TrackSort.Added, Descending: true, Take: 500));
        TracksSpec.RecentlyPlayed.Query(TrackSort.Codec, false).Should().Be(new TrackQuery(TrackSort.LastPlayed, Descending: true, PlayedOnly: true, Take: 500));
        TracksSpec.MostPlayed.Query(TrackSort.Codec, false).Should().Be(new TrackQuery(TrackSort.PlayCount, Descending: true, PlayedOnly: true, Take: 500));
        TracksSpec.RecentlyPlayed.SortIsFixed.Should().BeTrue();
        TracksSpec.All.SortIsFixed.Should().BeFalse();
    }

    [Fact]
    public void Genre_and_folder_views_carry_their_filter_and_the_chosen_sort()
    {
        var genre = TracksSpec.Genre(new GenreDto(7, "Jazz", 12));
        var folder = TracksSpec.Folder(new LibraryFolderDto(2, @"D:\Music\", true, null, null));

        genre.Query(TrackSort.Year, true).Should().Be(new TrackQuery(TrackSort.Year, Descending: true, GenreId: 7));
        genre.Title.Should().Be("Jazz");
        folder.Query(TrackSort.Album, false).Should().Be(new TrackQuery(TrackSort.Album, FolderId: 2));
        folder.DefaultSort.Should().Be(TrackSort.Album, "a folder reads best in album order");
        TracksSpec.All.DefaultSort.Should().Be(TrackSort.Title);
    }

    [Fact]
    public async Task Recently_played_lists_played_tracks_newest_first_and_locks_the_sort_Async()
    {
        TracksViewModel vm = Create();
        await vm.LoadAsync(TracksSpec.RecentlyPlayed);

        vm.Title.Should().Be("Recently played");
        vm.CanSort.Should().BeFalse();
        vm.Sort.Should().Be(TrackSort.LastPlayed);
        vm.Descending.Should().BeTrue();
        vm.Items!.Select(t => t.Id).Should().Equal([3L, 4L, 1L], "never-played tracks are not 'recently played'");
        vm.Items!.Take.Should().Be(500);
        vm.SortBy(TrackSort.Title);
        vm.Sort.Should().Be(TrackSort.LastPlayed, "a fixed view ignores header clicks");
    }

    [Fact]
    public async Task Header_clicks_toggle_direction_on_the_same_column_and_open_others_in_their_natural_direction_Async()
    {
        TracksViewModel vm = Create();
        await vm.LoadAsync(TracksSpec.All);
        vm.Items!.Select(t => t.Title).Should().Equal("alpha", "bravo", "Charlie", "Delta");

        vm.SortBy(TrackSort.Title);
        await WaitForItemsAsync(vm);
        vm.Descending.Should().BeTrue();
        vm.Items!.Select(t => t.Title).Should().Equal("Delta", "Charlie", "bravo", "alpha");

        vm.SortBy(TrackSort.PlayCount);
        await WaitForItemsAsync(vm);
        (vm.Sort, vm.Descending).Should().Be((TrackSort.PlayCount, true), "counts read highest first");
        vm.Items!.Select(t => t.Id).Should().Equal([4L, 1L, 3L, 2L], "ties on count break on most recent play, then id, reversed");

        vm.SortBy(TrackSort.Codec);
        await WaitForItemsAsync(vm);
        (vm.Sort, vm.Descending).Should().Be((TrackSort.Codec, false));
        vm.Query.Should().Be(new TrackQuery(TrackSort.Codec));
    }

    [Fact]
    public async Task Play_hands_the_selection_over_in_list_order_starting_at_the_anchor_Async()
    {
        TracksViewModel vm = Create();
        await vm.LoadAsync(TracksSpec.All);
        TrackDto[] selection = [vm.Items![0], vm.Items[1], vm.Items[3]];

        await vm.HandleAsync(TrackAction.Play, selection, anchor: vm.Items[1]);
        _playback.Last.Should().Be(new FakePlayback.Request("play", [2, 4, 1], 1, false));

        await vm.HandleAsync(TrackAction.Play, selection, anchor: null);
        _playback.Last.StartIndex.Should().Be(0);

        await vm.HandleAsync(TrackAction.PlayNext, selection, anchor: vm.Items[3]);
        _playback.Last.Should().Be(new FakePlayback.Request("next", [2, 4, 1], 0, false));

        await vm.HandleAsync(TrackAction.Enqueue, [vm.Items[2]], anchor: vm.Items[2]);
        _playback.Last.Should().Be(new FakePlayback.Request("enqueue", [3], 0, false));
    }

    [Fact]
    public async Task Navigation_and_reveal_actions_use_the_anchor_row_Async()
    {
        TracksViewModel vm = Create();
        await vm.LoadAsync(TracksSpec.All);
        TrackDto charlie = vm.Items!.Single(t => t.Id == 3);

        await vm.HandleAsync(TrackAction.OpenAlbum, [vm.Items![0], charlie], anchor: charlie);
        await vm.HandleAsync(TrackAction.OpenArtist, [charlie], anchor: null);
        await vm.HandleAsync(TrackAction.ShowInFolder, [charlie], anchor: charlie);

        _navigator.Opened.Should().Equal(("album", 5L), ("artist", 77L));
        _revealer.Revealed.Should().Equal(charlie.Path);
        _playback.Requests.Should().BeEmpty();
    }

    [Fact]
    public void Hidden_columns_are_remembered_across_view_models_through_settings()
    {
        TracksViewModel first = Create();
        var changed = new List<TrackColumn>();
        first.ColumnVisibilityChanged += (_, c) => changed.Add(c);

        first.SetColumnVisible(TrackColumn.Plays, false);
        first.SetColumnVisible(TrackColumn.Rating, false);
        first.SetColumnVisible(TrackColumn.Rating, false);
        first.SetColumnVisible(TrackColumn.Title, false);

        changed.Should().Equal(TrackColumn.Plays, TrackColumn.Rating);
        first.IsColumnVisible(TrackColumn.Title).Should().BeTrue("the title column cannot be hidden");
        _settings.GetValue(SettingsKeys.UiTracksHiddenColumns, string.Empty).Should().Be("Plays,Rating");

        TracksViewModel second = Create();
        second.IsColumnVisible(TrackColumn.Plays).Should().BeFalse();
        second.IsColumnVisible(TrackColumn.Artist).Should().BeTrue();
        second.SetColumnVisible(TrackColumn.Plays, true);
        _settings.GetValue(SettingsKeys.UiTracksHiddenColumns, string.Empty).Should().Be("Rating");
    }

    [Fact]
    public async Task An_empty_result_is_reported_Async()
    {
        TracksViewModel vm = Create();
        await vm.LoadAsync(TracksSpec.Folder(new LibraryFolderDto(9, @"D:\Nothing\", true, null, null)));
        vm.IsEmpty.Should().BeTrue();
        vm.Title.Should().Be(@"D:\Nothing\");
    }

    /// <summary>A header click requeries in the background; the new source is in place once it holds a page.</summary>
    private static async Task WaitForItemsAsync(TracksViewModel vm)
    {
        IncrementalItemsSource<TrackDto>? before = vm.Items;
        for (int i = 0; i < 200 && (ReferenceEquals(vm.Items, before) || vm.Items!.IsLoading || vm.Items!.Count == 0); i++)
        {
            await Task.Delay(10);
        }
    }
}
