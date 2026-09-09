using Tunqio.App.Controls;
using Tunqio.App.Library;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

/// <summary>E3-S9: the search box's debounce, cancellation, grouping, "show all" and result actions.</summary>
public class SearchViewModelTests
{
    private readonly FakeSearchService _search = new();
    private readonly FakePlayback _playback = new();
    private readonly FakeNavigator _navigator = new();
    private readonly FakeRevealer _revealer = new();
    private readonly FakeAlbumRepository _albums = new();

    private SearchViewModel Create(TimeSpan? debounce = null) =>
        new(_search, _playback, _navigator, new AlbumActions(_albums, _playback, _navigator, _revealer), _revealer, TimeProvider.System, debounce ?? TimeSpan.Zero);

    [Fact]
    public async Task Typing_continuously_never_shows_stale_results_Async()
    {
        SearchViewModel vm = Create();

        vm.Text = "r";
        _search.Calls.Should().HaveCount(1);
        vm.IsSearching.Should().BeTrue();
        vm.Text = "ra";
        _search.Calls.Should().HaveCount(2);
        _search.Calls[0].Token.IsCancellationRequested.Should().BeTrue("a new keystroke cancels the query in flight");
        _search.Calls[1].Token.IsCancellationRequested.Should().BeFalse();

        // The backend ignores the cancellation and answers the old query late; the answer must not show.
        _search.Calls[0].Answer(Results("r", Rows.Track(1, "Rain")));
        vm.Results.Should().BeNull("the answer to 'r' arrived after 'ra' was typed");
        vm.Groups.Should().BeEmpty();
        vm.IsSearching.Should().BeTrue("the current query is still in flight");

        _search.Calls[1].Answer(Results("ra", Rows.Track(2, "Radio")));
        await vm.Completion;
        vm.Results!.Text.Should().Be("ra");
        vm.Groups.Should().ContainSingle().Which.Should().ContainSingle().Which.Should().BeOfType<TrackDto>().Which.Id.Should().Be(2);
        vm.IsSearching.Should().BeFalse();
        vm.IsActive.Should().BeTrue();
        vm.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Keystrokes_within_the_debounce_window_run_one_query_for_the_final_text_Async()
    {
        SearchViewModel vm = Create(TimeSpan.FromMilliseconds(40));
        _search.AnswerImmediately = (text, _) => Results(text, Rows.Track(1, text));

        vm.Text = "r";
        vm.Text = "ra";
        vm.Text = "rad";
        await vm.Completion;

        _search.Calls.Select(c => c.Text).Should().Equal("rad");
        vm.Results!.Text.Should().Be("rad");
    }

    [Fact]
    public async Task Blank_text_clears_the_results_without_a_query_Async()
    {
        SearchViewModel vm = Create();
        _search.AnswerImmediately = (text, _) => Results(text, Rows.Track(1, "Rain"));
        vm.Text = "rain";
        await vm.Completion;
        vm.Groups.Should().NotBeEmpty();

        vm.Text = "   ";

        vm.IsActive.Should().BeFalse();
        vm.Results.Should().BeNull();
        vm.Groups.Should().BeEmpty();
        vm.IsEmpty.Should().BeFalse();
        _search.Calls.Should().HaveCount(1, "blank text never reaches the service");
    }

    [Fact]
    public async Task Whitespace_around_the_same_terms_does_not_requery_Async()
    {
        SearchViewModel vm = Create();
        _search.AnswerImmediately = (text, _) => Results(text, Rows.Track(1, "Rain"));

        vm.Text = "rain";
        await vm.Completion;
        vm.Text = "rain ";
        await vm.Completion;

        _search.Calls.Should().HaveCount(1);
        vm.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Groups_omit_empty_groups_and_carry_the_more_flags_Async()
    {
        SearchViewModel vm = Create();
        _search.AnswerImmediately = (text, _) => new SearchResults(text, [Rows.Track(1, "Rain"), Rows.Track(2, "Rainbow")], true, [], false, [new ArtistDto(9, "Rain Dogs", "Rain Dogs", null, 1, 4, null)], false);

        vm.Text = "rain";
        await vm.Completion;

        vm.Groups.Select(g => g.Kind).Should().Equal(SearchGroupKind.Tracks, SearchGroupKind.Artists);
        SearchGroup tracks = vm.Groups[0];
        tracks.Title.Should().Be("Tracks");
        tracks.HasMore.Should().BeTrue();
        tracks.CanToggle.Should().BeTrue();
        tracks.ToggleLabel.Should().Be("Show all");
        tracks.Should().HaveCount(2);
        vm.Groups[1].CanToggle.Should().BeFalse();
        vm.FirstTrack!.Id.Should().Be(1);
    }

    [Fact]
    public async Task No_results_is_empty_and_active_Async()
    {
        SearchViewModel vm = Create();
        _search.AnswerImmediately = (text, _) => SearchResults.Empty(text);

        vm.Text = "zzz";
        await vm.Completion;

        vm.IsActive.Should().BeTrue();
        vm.IsEmpty.Should().BeTrue();
        vm.Groups.Should().BeEmpty();
        vm.FirstTrack.Should().BeNull();
    }

    [Fact]
    public async Task Show_all_expands_one_group_show_fewer_restores_and_typing_resets_Async()
    {
        SearchViewModel vm = Create();
        _search.AnswerImmediately = (text, limits) => new SearchResults(text, [Rows.Track(1, "Rain")], limits.Tracks < 100, [], false, [], false);
        vm.Text = "rain";
        await vm.Completion;

        vm.ToggleGroup(SearchGroupKind.Tracks);
        await vm.Completion;
        _search.Calls[^1].Limits.Should().Be(new SearchLimits(Tracks: SearchViewModel.ExpandedLimit));
        vm.Groups[0].IsExpanded.Should().BeTrue();
        vm.Groups[0].ToggleLabel.Should().Be("Show fewer");
        vm.Groups[0].CanToggle.Should().BeTrue("a shown-all group can be folded back even though nothing more exists");

        vm.ToggleGroup(SearchGroupKind.Tracks);
        await vm.Completion;
        _search.Calls[^1].Limits.Should().Be(SearchLimits.Default);
        vm.Groups[0].IsExpanded.Should().BeFalse();

        vm.ToggleGroup(SearchGroupKind.Albums);
        await vm.Completion;
        _search.Calls[^1].Limits.Should().Be(new SearchLimits(Albums: SearchViewModel.ExpandedLimit));

        vm.Text = "rains";
        await vm.Completion;
        _search.Calls[^1].Limits.Should().Be(SearchLimits.Default, "a text change resets the expansion");
    }

    [Fact]
    public async Task Enter_plays_the_first_track_shift_plays_it_next_and_ctrl_queues_it_Async()
    {
        SearchViewModel vm = Create();
        await vm.PlayFirstAsync();
        _playback.Requests.Should().BeEmpty("nothing to play yet");

        _search.AnswerImmediately = (text, _) => Results(text, Rows.Track(7, "Rain"), Rows.Track(8, "Rainbow"));
        vm.Text = "rain";
        await vm.Completion;

        await vm.PlayFirstAsync();
        await vm.PlayNextFirstAsync();
        await vm.EnqueueFirstAsync();

        _playback.Requests.Should().Equal(
            new FakePlayback.Request("play", [7], 0, false),
            new FakePlayback.Request("next", [7], 0, false),
            new FakePlayback.Request("enqueue", [7], 0, false));
    }

    [Fact]
    public async Task Track_row_actions_play_navigate_and_reveal_Async()
    {
        SearchViewModel vm = Create();
        TrackDto track = Rows.Track(3, "Rain", albumId: 5, credits: [new ArtistRef(77, "Someone")]);

        await vm.HandleTrackAsync(TrackAction.Play, track);
        await vm.HandleTrackAsync(TrackAction.PlayNext, track);
        await vm.HandleTrackAsync(TrackAction.Enqueue, track);
        await vm.HandleTrackAsync(TrackAction.OpenAlbum, track);
        await vm.HandleTrackAsync(TrackAction.OpenArtist, track);
        await vm.HandleTrackAsync(TrackAction.ShowInFolder, track);

        _playback.Requests.Select(r => r.Kind).Should().Equal("play", "next", "enqueue");
        _navigator.Opened.Should().Equal(("album", 5L), ("artist", 77L));
        _revealer.Revealed.Should().Equal(track.Path);
    }

    [Fact]
    public async Task Album_and_artist_rows_go_through_the_shared_actions_Async()
    {
        SearchViewModel vm = Create();
        AlbumDto album = Rows.Album(5, "Rain Songs");
        _albums.Rows.Add(album);
        _albums.Tracks[5] = [Rows.Track(1, "a", albumId: 5), Rows.Track(2, "b", albumId: 5)];

        await vm.HandleAlbumAsync(AlbumAction.Play, album);
        await vm.HandleAlbumAsync(AlbumAction.Open, album);
        vm.OpenArtist(new ArtistDto(9, "Rain Dogs", "Rain Dogs", null, 1, 4, null));

        _playback.Last.Should().Be(new FakePlayback.Request("play", [1, 2], 0, false));
        _navigator.Opened.Should().Equal(("album", 5L), ("artist", 9L));
    }

    private static SearchResults Results(string text, params TrackDto[] tracks) => new(text, tracks, false, [], false, [], false);
}
