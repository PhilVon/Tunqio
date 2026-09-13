using Tunqio.App.Library;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

/// <summary>
/// E6-S1: Library › Playlists and a playlist's page, against a fake repository. The list shows totals and New playlist
/// opens what it made; the page shows totals, plays from a row, renames, deletes and goes back, removes, and turns a
/// reorder of any shape into the single moves the repository takes.
/// </summary>
public sealed class PlaylistViewModelTests
{
    private readonly FakePlaylistRepository _playlists = new();
    private readonly FakePlayback _playback = new();
    private readonly FakeNavigator _navigator = new();

    private static readonly TrackDto One = Rows.Track(1, "One", durationMs: 60_000);
    private static readonly TrackDto Two = Rows.Track(2, "Two", durationMs: 120_000);
    private static readonly TrackDto Three = Rows.Track(3, "Three", durationMs: 180_000);

    private PlaylistsViewModel List() => new(_playlists, _navigator);

    private PlaylistDetailViewModel Detail() => new(_playlists, _playback, _navigator);

    [Fact]
    public async Task The_list_shows_each_playlist_with_its_totals_Async()
    {
        _playlists.Add("Sunday", One, Two);
        _playlists.Add("Empty");
        PlaylistsViewModel vm = List();

        await vm.LoadAsync();

        // An empty playlist leaves the duration off: "0 tracks · 0 s" says less than "0 tracks".
        vm.Rows.Select(r => (r.Name, r.Detail)).Should().Equal(("Sunday", "2 tracks · 3 min"), ("Empty", "0 tracks"));
        vm.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task New_playlist_creates_it_and_opens_it_and_a_blank_name_does_nothing_Async()
    {
        PlaylistsViewModel vm = List();

        await vm.CreateAsync("  ");
        await vm.CreateAsync("Road trip");

        _playlists.Stored.Should().ContainSingle().Which.Name.Should().Be("Road trip");
        _navigator.Opened.Should().Equal(("playlist", _playlists.Stored[0].Id));
        vm.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task The_page_shows_the_tracks_in_order_with_their_total_Async()
    {
        long id = _playlists.Add("Mix", Three, One, Three);
        PlaylistDetailViewModel vm = Detail();

        await vm.LoadAsync(id);

        vm.Name.Should().Be("Mix");
        vm.Rows.Select(r => (r.Position, r.Title)).Should().Equal((0, "Three"), (1, "One"), (2, "Three"));
        vm.Summary.Should().Be("3 tracks · 7 min");
        vm.CanPlay.Should().BeTrue();
    }

    [Fact]
    public async Task Play_from_a_row_queues_the_whole_playlist_from_that_row_Async()
    {
        long id = _playlists.Add("Mix", One, Two, Three);
        PlaylistDetailViewModel vm = Detail();
        await vm.LoadAsync(id);

        await vm.PlayFromAsync(vm.Rows[1]);
        await vm.ShuffleAsync();

        _playback.Requests.Should().Equal(
            new FakePlayback.Request("play", [1, 2, 3], 1, false),
            new FakePlayback.Request("play", [1, 2, 3], 0, true));
    }

    [Fact]
    public async Task Rename_is_stored_and_shown_and_a_blank_name_does_nothing_Async()
    {
        long id = _playlists.Add("Old", One);
        PlaylistDetailViewModel vm = Detail();
        await vm.LoadAsync(id);

        await vm.RenameAsync(" ");
        await vm.RenameAsync("New");

        vm.Name.Should().Be("New");
        _playlists.Stored.Single().Name.Should().Be("New");
    }

    [Fact]
    public async Task Delete_removes_the_playlist_and_goes_back_to_the_list_Async()
    {
        long id = _playlists.Add("Gone", One);
        PlaylistDetailViewModel vm = Detail();
        await vm.LoadAsync(id);

        await vm.DeleteAsync();

        _playlists.Stored.Should().BeEmpty();
        _navigator.Opened.Should().Equal("playlists");
    }

    [Fact]
    public async Task Remove_takes_the_selected_rows_out_and_the_total_follows_Async()
    {
        long id = _playlists.Add("Trim", One, Two, Three);
        PlaylistDetailViewModel vm = Detail();
        await vm.LoadAsync(id);

        await vm.RemoveAsync([vm.Rows[0], vm.Rows[2]]);

        vm.Rows.Select(r => r.Title).Should().Equal("Two");
        vm.Summary.Should().Be("1 track · 2 min");
    }

    [Fact]
    public async Task Move_up_and_down_shift_one_row_and_stop_at_the_ends_Async()
    {
        long id = _playlists.Add("Order", One, Two, Three);
        PlaylistDetailViewModel vm = Detail();
        await vm.LoadAsync(id);

        await vm.MoveDownAsync(vm.Rows[0]);
        vm.Rows.Select(r => r.Title).Should().Equal("Two", "One", "Three");
        await vm.MoveUpAsync(vm.Rows[0]);
        await vm.MoveDownAsync(vm.Rows[2]);

        vm.Rows.Select(r => r.Title).Should().Equal("Two", "One", "Three");
    }

    [Theory]
    [InlineData(new[] { 0, 1, 2, 3 })]
    [InlineData(new[] { 3, 0, 1, 2 })]
    [InlineData(new[] { 1, 2, 3, 0 })]
    [InlineData(new[] { 2, 3, 0, 1 })] // two rows dragged together
    [InlineData(new[] { 3, 2, 1, 0 })]
    public void The_moves_for_any_reorder_produce_exactly_that_order(int[] positions)
    {
        var list = new List<int> { 0, 1, 2, 3 };

        foreach ((int from, int to) in PlaylistDetailViewModel.MovesFor(positions))
        {
            int item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
        }

        list.Should().Equal(positions);
    }

    [Fact]
    public async Task A_drag_that_moved_two_rows_is_stored_as_the_order_it_left_Async()
    {
        long id = _playlists.Add("Drag", One, Two, Three);
        PlaylistDetailViewModel vm = Detail();
        await vm.LoadAsync(id);

        await vm.ApplyOrderAsync([1, 2, 0]);

        vm.Rows.Select(r => r.Title).Should().Equal("Two", "Three", "One");
    }

    [Fact]
    public async Task A_playlist_that_has_gone_says_so_and_cannot_be_played_Async()
    {
        PlaylistDetailViewModel vm = Detail();

        await vm.LoadAsync(404);

        vm.NotFound.Should().BeTrue();
        vm.CanPlay.Should().BeFalse();
        vm.IsEmpty.Should().BeTrue();
    }

    /// <summary>A playlist store in memory, with the repository's semantics for positions.</summary>
    private sealed class FakePlaylistRepository : IPlaylistRepository
    {
        private long _nextId = 1;

        public List<(long Id, string Name, List<TrackDto> Tracks)> Stored { get; } = [];

        public long Add(string name, params TrackDto[] tracks)
        {
            long id = _nextId++;
            Stored.Add((id, name, [.. tracks]));
            return id;
        }

        public Task<IReadOnlyList<PlaylistDto>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlaylistDto>>([.. Stored.Select(Dto)]);

        public Task<PlaylistDetailDto?> GetDetailAsync(long id, CancellationToken ct = default)
        {
            var found = Stored.FirstOrDefault(p => p.Id == id);
            return Task.FromResult(found.Name is null ? null : new PlaylistDetailDto(Dto(found), [.. found.Tracks]));
        }

        public Task<PlaylistDto> CreateAsync(string name, CancellationToken ct = default)
        {
            long id = Add(name.Trim());
            return Task.FromResult(Dto(Stored.Single(p => p.Id == id)));
        }

        public Task RenameAsync(long id, string name, CancellationToken ct = default)
        {
            int at = Stored.FindIndex(p => p.Id == id);
            Stored[at] = (id, name.Trim(), Stored[at].Tracks);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(long id, CancellationToken ct = default)
        {
            Stored.RemoveAll(p => p.Id == id);
            return Task.CompletedTask;
        }

        public Task AddTracksAsync(long id, IReadOnlyList<long> trackIds, CancellationToken ct = default) => throw new NotSupportedException();

        public Task ReplaceTracksAsync(long id, IReadOnlyList<long> trackIds, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RemoveAtAsync(long id, IReadOnlyList<int> positions, CancellationToken ct = default)
        {
            List<TrackDto> tracks = Stored.Single(p => p.Id == id).Tracks;
            foreach (int position in positions.Distinct().OrderDescending())
            {
                tracks.RemoveAt(position);
            }

            return Task.CompletedTask;
        }

        public Task MoveAsync(long id, int fromPosition, int toPosition, CancellationToken ct = default)
        {
            List<TrackDto> tracks = Stored.Single(p => p.Id == id).Tracks;
            TrackDto moved = tracks[fromPosition];
            tracks.RemoveAt(fromPosition);
            tracks.Insert(toPosition, moved);
            return Task.CompletedTask;
        }

        private static PlaylistDto Dto((long Id, string Name, List<TrackDto> Tracks) p) =>
            new(p.Id, p.Name, 0, 0, false, p.Tracks.Count, p.Tracks.Sum(t => (long)t.DurationMs));
    }
}
