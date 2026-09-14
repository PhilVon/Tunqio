using FluentAssertions;
using Tunqio.Core.Library;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>E6-S2: every playlist write raises <see cref="IPlaylistRepository.Changed"/> with the playlist's id, which is what the auto-export follows.</summary>
public sealed class PlaylistChangedTests : IAsyncLifetime
{
    private LibrarySeed _seed = null!;
    private long _track;
    private readonly List<long> _raised = [];

    private IPlaylistRepository Playlists => _seed.Service.Playlists;

    public async Task InitializeAsync()
    {
        _seed = await LibrarySeed.CreateAsync(withExtras: false);
        _track = (await _seed.Tracks.ListAsync(new TrackQuery(PageSize: 1)))[0].Id;
        Playlists.Changed += (_, id) => _raised.Add(id);
    }

    public Task DisposeAsync()
    {
        _seed.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Create_add_move_remove_replace_rename_and_delete_each_raise_it_once_Async()
    {
        long id = (await Playlists.CreateAsync("Mix")).Id;
        await Playlists.AddTracksAsync(id, [_track, _track]);
        await Playlists.MoveAsync(id, 0, 1);
        await Playlists.RemoveAtAsync(id, [0]);
        await Playlists.ReplaceTracksAsync(id, [_track]);
        await Playlists.RenameAsync(id, "Renamed");
        await Playlists.DeleteAsync(id);

        _raised.Should().Equal(Enumerable.Repeat(id, 7));
    }

    [Fact]
    public async Task A_pin_change_raises_it_once_and_setting_the_flag_it_already_has_raises_nothing_Async()
    {
        long id = (await Playlists.CreateAsync("Pin")).Id;
        _raised.Clear();

        await Playlists.SetPinnedAsync(id, pinned: true);
        await Playlists.SetPinnedAsync(id, pinned: true);
        await Playlists.SetPinnedAsync(id, pinned: false);

        _raised.Should().Equal(id, id);
    }

    [Fact]
    public async Task A_write_to_a_playlist_that_does_not_exist_raises_nothing_Async()
    {
        await Playlists.SetPinnedAsync(404, pinned: true);
        await Playlists.RenameAsync(404, "x");
        await Playlists.DeleteAsync(404);
        await Playlists.AddTracksAsync(404, [_track]);
        await Playlists.ReplaceTracksAsync(404, [_track]);

        _raised.Should().BeEmpty();
    }
}
