using FluentAssertions;
using Tunqio.Core.Library;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>
/// E6-S1: playlists over the seeded library. Create, rename, delete; add in order with duplicates allowed; remove and
/// reorder with positions staying contiguous; totals that follow the items; and a track that leaves the library
/// leaving every playlist with it.
/// </summary>
public sealed class PlaylistRepositoryTests : IAsyncLifetime
{
    private LibrarySeed _seed = null!;
    private TrackDto[] _tracks = null!;

    public async Task InitializeAsync()
    {
        _seed = await LibrarySeed.CreateAsync(withExtras: false);
        _tracks = [.. await _seed.Tracks.ListAsync(new TrackQuery(PageSize: 6))];
    }

    public Task DisposeAsync()
    {
        _seed.Dispose();
        return Task.CompletedTask;
    }

    private IPlaylistRepository Playlists => _seed.Service.Playlists;

    private long Id(int index) => _tracks[index].Id;

    private async Task<long[]> ItemsOfAsync(long playlistId) =>
        [.. (await Playlists.GetDetailAsync(playlistId))!.Tracks.Select(t => t.Id)];

    [Fact]
    public async Task A_new_playlist_is_listed_empty_with_its_name_trimmed_Async()
    {
        PlaylistDto created = await Playlists.CreateAsync("  Sunday  ");

        created.Name.Should().Be("Sunday");
        created.TrackCount.Should().Be(0);
        created.CreatedAt.Should().Be(LibrarySeed.Now);
        (await Playlists.ListAsync()).Should().ContainSingle().Which.Should().Be(created);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_name_is_refused_Async(string name)
    {
        Func<Task> create = () => Playlists.CreateAsync(name);

        await create.Should().ThrowAsync<ArgumentException>();
        (await Playlists.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Tracks_are_added_in_order_a_track_can_appear_twice_and_unknown_ids_are_skipped_Async()
    {
        PlaylistDto list = await Playlists.CreateAsync("Mix");

        await Playlists.AddTracksAsync(list.Id, [Id(2), Id(0), 999_999, Id(2)]);
        await Playlists.AddTracksAsync(list.Id, [Id(1)]);

        (await ItemsOfAsync(list.Id)).Should().Equal(Id(2), Id(0), Id(2), Id(1));
    }

    [Fact]
    public async Task The_list_totals_follow_the_items_Async()
    {
        PlaylistDto list = await Playlists.CreateAsync("Totals");
        await Playlists.AddTracksAsync(list.Id, [Id(0), Id(1), Id(1)]);

        PlaylistDto row = (await Playlists.ListAsync()).Single();

        row.TrackCount.Should().Be(3);
        row.TotalDurationMs.Should().Be((long)_tracks[0].DurationMs + _tracks[1].DurationMs * 2);
    }

    [Fact]
    public async Task Playlists_are_listed_by_name_whatever_the_case_Async()
    {
        await Playlists.CreateAsync("beta");
        await Playlists.CreateAsync("Alpha");
        await Playlists.CreateAsync("gamma");

        (await Playlists.ListAsync()).Select(p => p.Name).Should().Equal("Alpha", "beta", "gamma");
    }

    [Fact]
    public async Task Rename_changes_the_name_and_blank_is_refused_Async()
    {
        PlaylistDto list = await Playlists.CreateAsync("Old");

        await Playlists.RenameAsync(list.Id, " New ");
        Func<Task> blank = () => Playlists.RenameAsync(list.Id, " ");

        (await Playlists.ListAsync()).Single().Name.Should().Be("New");
        await blank.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData(0, 3, new[] { 1, 2, 3, 0 })]
    [InlineData(3, 0, new[] { 3, 0, 1, 2 })]
    [InlineData(1, 2, new[] { 0, 2, 1, 3 })]
    [InlineData(2, 2, new[] { 0, 1, 2, 3 })]
    public async Task Move_takes_one_item_and_shifts_the_ones_between_Async(int from, int to, int[] expected)
    {
        PlaylistDto list = await Playlists.CreateAsync("Order");
        await Playlists.AddTracksAsync(list.Id, [Id(0), Id(1), Id(2), Id(3)]);

        await Playlists.MoveAsync(list.Id, from, to);

        (await ItemsOfAsync(list.Id)).Should().Equal(expected.Select(Id));
    }

    [Fact]
    public async Task Remove_takes_the_items_at_the_positions_and_the_rest_close_up_Async()
    {
        PlaylistDto list = await Playlists.CreateAsync("Trim");
        await Playlists.AddTracksAsync(list.Id, [Id(0), Id(1), Id(2), Id(3), Id(4)]);

        await Playlists.RemoveAtAsync(list.Id, [3, 0, 3]);
        await Playlists.AddTracksAsync(list.Id, [Id(5)]);

        (await ItemsOfAsync(list.Id)).Should().Equal(Id(1), Id(2), Id(4), Id(5));
    }

    [Fact]
    public async Task A_position_out_of_range_is_refused_and_changes_nothing_Async()
    {
        PlaylistDto list = await Playlists.CreateAsync("Bounds");
        await Playlists.AddTracksAsync(list.Id, [Id(0), Id(1)]);

        Func<Task> move = () => Playlists.MoveAsync(list.Id, 0, 2);
        Func<Task> remove = () => Playlists.RemoveAtAsync(list.Id, [5]);

        await move.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await remove.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await ItemsOfAsync(list.Id)).Should().Equal(Id(0), Id(1));
    }

    [Fact]
    public async Task Replace_writes_exactly_the_list_given_duplicates_kept_and_unknown_ids_skipped_Async()
    {
        PlaylistDto list = await Playlists.CreateAsync("Replace");
        await Playlists.AddTracksAsync(list.Id, [Id(0), Id(1), Id(2)]);

        await Playlists.ReplaceTracksAsync(list.Id, [Id(2), 999_999, Id(2), Id(4)]);
        await Playlists.MoveAsync(list.Id, 2, 0); // positions stayed contiguous past the skipped id

        (await ItemsOfAsync(list.Id)).Should().Equal(Id(4), Id(2), Id(2));
        await Playlists.ReplaceTracksAsync(list.Id, []);
        (await ItemsOfAsync(list.Id)).Should().BeEmpty();
        await Playlists.ReplaceTracksAsync(12345, [Id(0)]); // an unknown playlist: nothing, and no throw
    }

    [Fact]
    public async Task Replacing_with_500_tracks_stores_all_of_them_in_order_Async()
    {
        TrackDto[] library = [.. await _seed.Tracks.ListAsync(new TrackQuery(PageSize: 50))];
        long[] ids = [.. Enumerable.Range(0, 500).Select(i => library[i % library.Length].Id)];
        PlaylistDto list = await Playlists.CreateAsync("Big");

        await Playlists.ReplaceTracksAsync(list.Id, ids);

        (await ItemsOfAsync(list.Id)).Should().Equal(ids);
    }

    [Fact]
    public async Task Delete_removes_the_playlist_and_its_items_Async()
    {
        PlaylistDto keep = await Playlists.CreateAsync("Keep");
        PlaylistDto gone = await Playlists.CreateAsync("Gone");
        await Playlists.AddTracksAsync(gone.Id, [Id(0)]);

        await Playlists.DeleteAsync(gone.Id);

        (await Playlists.GetDetailAsync(gone.Id)).Should().BeNull();
        (await Playlists.ListAsync()).Should().ContainSingle().Which.Id.Should().Be(keep.Id);
    }

    [Fact]
    public async Task A_track_purged_from_the_library_leaves_every_playlist_Async()
    {
        PlaylistDto list = await Playlists.CreateAsync("Purge");
        await Playlists.AddTracksAsync(list.Id, [Id(0), Id(1), Id(0), Id(2)]);

        await _seed.Tracks.MarkMissingAsync([Id(0)], missing: true);
        await _seed.Tracks.PurgeMissingAsync(long.MaxValue);

        (await ItemsOfAsync(list.Id)).Should().Equal(Id(1), Id(2));
        await Playlists.MoveAsync(list.Id, 1, 0); // positions are still usable after the cascade
        (await ItemsOfAsync(list.Id)).Should().Equal(Id(2), Id(1));
    }

    [Fact]
    public async Task An_unknown_playlist_has_no_detail_and_changes_to_it_do_nothing_Async()
    {
        (await Playlists.GetDetailAsync(12345)).Should().BeNull();

        await Playlists.AddTracksAsync(12345, [Id(0)]);
        await Playlists.RenameAsync(12345, "x");
        await Playlists.DeleteAsync(12345);

        (await Playlists.ListAsync()).Should().BeEmpty();
    }
}
