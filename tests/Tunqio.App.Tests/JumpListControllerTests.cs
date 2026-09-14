using Tunqio.App.Activation;
using Tunqio.App.JumpLists;
using Tunqio.App.Library;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;

namespace Tunqio.App.Tests;

/// <summary>
/// E7-S5 (AC-507, AC-509): the jump list's items over a fake jump list. Ten recent tracks at most, newest first, and the pinned
/// playlists; each item's launch arguments are the router's own <c>tunqio://</c> commands; a refresh at start-up, when a track
/// finishes playing and when a pin changes, and a burst of those writes the list once. The clock is moved by hand.
/// </summary>
public sealed class JumpListControllerTests : IDisposable
{
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakePlaylists _playlists = new();
    private readonly RecordingJumpList _jumpList = new();
    private readonly StubSessionSource _source = new();
    private readonly ManualClock _clock = new();
    private readonly ListLogger<JumpListController> _log = new();
    private JumpListController? _controller;

    public void Dispose() => _controller?.Dispose();

    private JumpListController Controller(IJumpList? jumpList = null) =>
        _controller = new JumpListController(_source, _tracks, _playlists, jumpList ?? _jumpList, _clock, _log);

    /// <summary>Moves the clock past the coalescing wait and waits for the write it started.</summary>
    private async Task SettleAsync(JumpListController controller)
    {
        _clock.Advance(JumpListController.CoalesceWindow);
        await controller.LastRefresh.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static TrackDto Played(long id, long playedAt, bool missing = false) =>
        Rows.Track(id, "Track " + id, albumArtist: "Artist " + id, lastPlayedAt: playedAt, playCount: 1, missing: missing);

    [Fact]
    public void The_recent_tracks_are_Library_Recently_played_s_query_capped_at_ten()
    {
        TrackQuery recentlyPlayed = TracksSpec.RecentlyPlayed.Query(TrackSort.Title, descending: false);

        JumpListController.RecentTracksQuery.Should().Be(recentlyPlayed with { PageSize = 10, Take = 10 });
        JumpListController.MaxRecentTracks.Should().Be(10);
    }

    [Fact]
    public async Task The_list_holds_ten_recent_tracks_newest_first_then_the_pinned_playlists_Async()
    {
        _tracks.Rows.AddRange(Enumerable.Range(1, 12).Select(i => Played(i, playedAt: 1000 + i)));
        _tracks.Rows.Add(Rows.Track(50, "Never played"));
        _tracks.Rows.Add(Played(60, playedAt: 99_999, missing: true));
        long road = _playlists.Add("Road trip", true, Rows.Track(1, "Track 1"));
        _playlists.Add("Unpinned", false);
        long ambient = _playlists.Add("Ambient", true);

        IReadOnlyList<JumpListEntry> entries = await Controller().BuildAsync();

        entries.Where(e => e.Group == JumpListController.RecentTracksGroup).Select(e => e.Arguments).Should().Equal(
            Enumerable.Range(3, 10).Reverse().Select(i => CommandRouter.TrackUri(i)),
            "the ten most recently played, newest first; the never-played and the missing are left out");
        entries.Where(e => e.Group == JumpListController.PinnedPlaylistsGroup).Select(e => e.Arguments).Should().Equal(
            CommandRouter.PlaylistUri(ambient), CommandRouter.PlaylistUri(road));
        entries.Take(10).Should().OnlyContain(e => e.Group == JumpListController.RecentTracksGroup, "the tracks come first");
    }

    [Fact]
    public async Task Each_track_appears_once_Async()
    {
        _tracks.Rows.AddRange([Played(1, 10), Played(1, 10), Played(2, 20)]);

        IReadOnlyList<JumpListEntry> entries = await Controller().BuildAsync();

        entries.Select(e => e.Arguments).Should().Equal(CommandRouter.TrackUri(2), CommandRouter.TrackUri(1));
    }

    [Fact]
    public void An_item_says_what_it_plays_and_launches_the_router_s_command()
    {
        JumpListEntry track = JumpListController.ForTrack(Rows.Track(42, "Blue in Green", albumArtist: "Miles Davis"));
        JumpListEntry playlist = JumpListController.ForPlaylist(new PlaylistDto(7, "Sunday", 0, 0, true, 3, 0));

        track.Should().Be(new JumpListEntry("Recent tracks", "Blue in Green - Miles Davis", "Play Blue in Green by Miles Davis", "tunqio://track?id=42"));
        playlist.Should().Be(new JumpListEntry("Pinned playlists", "Sunday", "Play the playlist Sunday", "tunqio://playlist?id=7"));
    }

    [Fact]
    public async Task Start_writes_the_list_once_after_the_coalescing_wait_Async()
    {
        _tracks.Rows.Add(Played(1, 10));
        JumpListController controller = Controller();

        controller.Start();
        _clock.Advance(JumpListController.CoalesceWindow - TimeSpan.FromMilliseconds(1));
        _jumpList.Writes.Should().BeEmpty("nothing is written before the wait is over");
        await SettleAsync(controller);

        _jumpList.Writes.Should().ContainSingle().Which.Should().ContainSingle();
    }

    [Fact]
    public void Nothing_is_written_until_something_asks()
    {
        Controller();

        _clock.Advance(TimeSpan.FromMinutes(1));

        _jumpList.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task Pinning_a_playlist_refreshes_the_list_with_it_and_unpinning_takes_it_out_Async()
    {
        long mix = _playlists.Add("Mix", false);
        JumpListController controller = Controller();

        await _playlists.SetPinnedAsync(mix, pinned: true);
        await SettleAsync(controller);
        _jumpList.Last.Select(e => e.DisplayName).Should().Equal("Mix");

        await _playlists.SetPinnedAsync(mix, pinned: false);
        await SettleAsync(controller);
        _jumpList.Last.Should().BeEmpty();
        _jumpList.Writes.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_burst_of_changes_writes_the_list_once_Async()
    {
        long one = _playlists.Add("One", false);
        long two = _playlists.Add("Two", false);
        JumpListController controller = Controller();

        controller.Start();
        await _playlists.SetPinnedAsync(one, pinned: true);
        await _playlists.SetPinnedAsync(two, pinned: true);
        await _playlists.RenameAsync(two, "Two renamed");
        controller.RequestRefresh("a track finished playing");
        await SettleAsync(controller);
        _clock.Advance(TimeSpan.FromMinutes(1));

        _jumpList.Writes.Should().ContainSingle().Which.Select(e => e.DisplayName).Should().Equal("One", "Two renamed");
        _log.Infos.Should().ContainSingle(l => l.StartsWith("Jump list: wrote", StringComparison.Ordinal))
            .Which.Should().Contain("a playlist changed").And.Contain("a track finished playing").And.Contain("startup");
    }

    [Fact]
    public async Task A_track_that_finishes_playing_refreshes_the_list_Async()
    {
        await using var engine = new FakeAudioEngine();
        _tracks.Rows.AddRange([Played(1, 10), Played(2, 20)]);
        await using var session = new PlaybackSession(engine, _tracks, new FakePlayHistory(), new FakeQueueStore(), new FakeSettings(), autoPoll: false);
        JumpListController controller = Controller();
        _source.Session = session; // arrives after the controller, as audio does after the window

        await session.PlayNowAsync([1, 2]);
        await session.NextAsync(); // track 1 stops being current: its listen is recorded
        await SettleAsync(controller);

        _jumpList.Writes.Should().ContainSingle();
        _log.Infos.Should().ContainSingle(l => l.Contains("a track finished playing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_during_a_write_gets_one_more_write_after_it_Async()
    {
        var gated = new GatedJumpList();
        JumpListController controller = Controller(gated);

        controller.Start();
        _clock.Advance(JumpListController.CoalesceWindow);
        gated.Started.Should().Be(1);
        controller.RequestRefresh("a playlist changed");
        controller.RequestRefresh("a track finished playing");
        _clock.Advance(JumpListController.CoalesceWindow);
        gated.Started.Should().Be(1, "a write under way is not overlapped");

        gated.Release();
        await controller.LastRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        _clock.Advance(JumpListController.CoalesceWindow);
        gated.Release();
        await controller.LastRefresh.WaitAsync(TimeSpan.FromSeconds(5));
        _clock.Advance(TimeSpan.FromMinutes(1));

        gated.Started.Should().Be(2, "the two requests made during the first write are one more write, and no more");
    }

    [Fact]
    public async Task A_jump_list_that_fails_is_one_warning_and_the_next_refresh_still_writes_Async()
    {
        _jumpList.FailWith = new UnauthorizedAccessException("no package identity");
        JumpListController controller = Controller();

        controller.Start();
        Func<Task> settle = () => SettleAsync(controller);
        await settle.Should().NotThrowAsync();
        _log.Warnings.Should().ContainSingle().Which.Should().Contain("failed");

        _jumpList.FailWith = null;
        controller.RequestRefresh("a playlist changed");
        await SettleAsync(controller);
        _jumpList.Writes.Should().ContainSingle();
    }

    [Fact]
    public async Task After_dispose_a_waiting_refresh_is_dropped_and_changes_are_ignored_Async()
    {
        long mix = _playlists.Add("Mix", false);
        JumpListController controller = Controller();
        controller.Start();

        controller.Dispose();
        await _playlists.SetPinnedAsync(mix, pinned: true);
        _clock.Advance(TimeSpan.FromMinutes(1));

        _jumpList.Writes.Should().BeEmpty();
    }

    /// <summary>A jump list whose writes finish only when the test says.</summary>
    private sealed class GatedJumpList : IJumpList
    {
        private TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Started { get; private set; }

        public Task WriteAsync(IReadOnlyList<JumpListEntry> entries, CancellationToken ct)
        {
            Started++;
            _pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _pending.Task;
        }

        public void Release() => _pending.TrySetResult();
    }
}
