using Tunqio.App.Playback;
using Tunqio.App.Shell;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;

namespace Tunqio.App.Tests;

/// <summary>
/// E2-S5: the queue panel over a real <see cref="PlaybackSession"/> and a fake engine. What the panel owes is
/// behaviour — which item is pinned, what order the rest is in, what a drag does to the boundary the engine has
/// already queued — so it is asserted here rather than by dragging a row in a running window.
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class QueueViewModelTests : IAsyncLifetime
{
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeSettings _settings = new();
    private readonly FakePlayHistory _history = new();
    private readonly FakeQueueStore _queues = new();
    private readonly StubSessionSource _source = new();
    private PlaybackSession _session = null!;
    private QueueViewModel _vm = null!;

    public Task InitializeAsync()
    {
        _tracks.Rows.AddRange(
        [
            Rows.Track(11, "One", credits: [new Tunqio.Core.Library.ArtistRef(10, "The Band")]),
            Rows.Track(12, "Two"),
            Rows.Track(13, "Three"),
            Rows.Track(14, "Four"),
        ]);
        _session = new PlaybackSession(_engine, _tracks, _history, _queues, _settings, autoPoll: false);
        _source.Session = _session;
        _vm = new QueueViewModel(_source, _tracks);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _vm.Dispose();
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
    }

    /// <summary>What the 10 Hz timer would do, plus the continuation the rows need to reach the collection.</summary>
    private async Task TickAsync()
    {
        await _session.PollAsync();
        await _vm.RowsSettledAsync();
    }

    /// <summary>
    /// What the <c>ListView</c> does when a row is dragged, or moved with the keyboard: it moves the item in the
    /// bound collection. Nothing tells the view model which of those it was, which is the point — this is the only
    /// path either of them takes.
    /// </summary>
    private async Task DragAsync(int from, int to)
    {
        _vm.Upcoming.Move(from, to);
        await _vm.ReorderSettledAsync();
        await TickAsync();
    }

    private static long[] Ids(IEnumerable<QueueRow> rows) => [.. rows.Select(row => row.TrackId)];

    // ---- what the panel shows ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_current_track_is_pinned_and_only_what_follows_it_is_listed_Async()
    {
        await _session.PlayNowAsync([11, 12, 13, 14], startIndex: 1);
        await TickAsync();

        _vm.NowPlaying!.TrackId.Should().Be(12);
        _vm.NowPlaying.Title.Should().Be("Two");
        // Track 11 is behind the current one, not ahead of it.
        Ids(_vm.Upcoming).Should().Equal(13, 14);
        _vm.UpcomingCountText.Should().Be("2 tracks");
        _vm.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task An_empty_queue_is_the_empty_state_Async()
    {
        await TickAsync();

        _vm.NowPlaying.Should().BeNull();
        _vm.Upcoming.Should().BeEmpty();
        _vm.IsEmpty.Should().BeTrue();
        _vm.HasUpcoming.Should().BeFalse();
    }

    [Fact]
    public async Task A_row_names_its_track_for_narrator_rather_than_its_columns_Async()
    {
        await _session.PlayNowAsync([11, 12]);
        await TickAsync();

        _vm.NowPlaying!.AutomationName.Should().Be("One by The Band");
        _vm.Upcoming[0].RemoveLabel.Should().Be("Remove Two from the queue");
    }

    [Fact]
    public async Task A_shuffled_queue_is_listed_in_the_order_it_will_play_Async()
    {
        await _session.PlayNowAsync([11, 12, 13, 14]);
        await _session.SetShuffleAsync(true);
        await TickAsync();

        Ids(_vm.Upcoming).Should().Equal(
            [.. _session.Queue.Items.Skip(1).Select(item => item.TrackId)],
            "the panel shows the play order, which is the shuffled one");
    }

    /// <summary>
    /// The rows cost a repository round trip and a fresh set of objects, so a snapshot that did not change the queue
    /// must not produce one — at 10 Hz that would be ten rebuilds a second for a queue nobody touched.
    /// </summary>
    [Fact]
    public async Task Snapshots_that_leave_the_queue_alone_do_not_rebuild_the_rows_Async()
    {
        await _session.PlayNowAsync([11, 12, 13]);
        await TickAsync();
        QueueRow first = _vm.Upcoming[0];

        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(5) };
        await TickAsync();
        await TickAsync();

        _vm.Upcoming[0].Should().BeSameAs(first);
    }

    // ---- AC-76: a drag re-queues the boundary -----------------------------------------------------------------------

    /// <summary>
    /// The one thing a queue panel can get wrong that nothing else notices: the engine has already opened and queued
    /// the track after the current one, so a drag that changes which track that is has to reach the mixer as well as
    /// the list. Asserted through the engine's own call log rather than through the queue value, because the queue
    /// being right is exactly what would hide this.
    /// </summary>
    [Fact]
    public async Task Dragging_a_track_to_the_top_re_queues_what_the_engine_had_preloaded_Async()
    {
        await _session.PlayNowAsync([11, 12, 13]);
        await TickAsync();
        _engine.Calls.Should().Contain(call => call.EndsWith(@":D:\Music\12.flac", StringComparison.Ordinal));
        _engine.Drain();

        // Drag track 13 to the top of the upcoming list, so it and not 12 is what follows the current track.
        await DragAsync(1, 0);

        Ids(_vm.Upcoming).Should().Equal(13, 12);
        string[] calls = _engine.Drain();
        string opened = calls.Should()
            .ContainSingle(call => call.EndsWith(@":D:\Music\13.flac", StringComparison.Ordinal)).Subject;
        calls.Should().Contain("preload:" + opened.Split(':')[1] + ":gapless");
    }

    [Fact]
    public async Task Dragging_below_the_boundary_leaves_the_preloaded_track_alone_Async()
    {
        await _session.PlayNowAsync([11, 12, 13, 14]);
        await TickAsync();
        _engine.Drain();

        // 13 and 14 swap; what follows the current track is still 12.
        await DragAsync(2, 1);

        Ids(_vm.Upcoming).Should().Equal(12, 14, 13);
        _engine.Drain().Should().NotContain(call => call.StartsWith("preload:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dragging_a_row_to_the_end_takes_as_many_moves_as_it_needs_Async()
    {
        await _session.PlayNowAsync([11, 12, 13, 14]);
        await TickAsync();

        await DragAsync(0, 2);

        Ids(_vm.Upcoming).Should().Equal(13, 14, 12);
        // One drag can need more than one move; the queue is what the list ended up as, not one step towards it.
        _session.Queue.Items.Select(item => item.TrackId).Should().Equal(11, 13, 14, 12);
    }

    /// <summary>
    /// A <c>ListView</c> reorder reaches the bound collection as a remove and then an insert. The half-way state is
    /// a queue with an item missing, and reporting *that* would take the track out of the queue for real.
    /// </summary>
    [Fact]
    public async Task A_reorder_half_done_is_not_reported_as_a_removal_Async()
    {
        await _session.PlayNowAsync([11, 12, 13]);
        await TickAsync();
        QueueRow moving = _vm.Upcoming[0];

        _vm.Upcoming.RemoveAt(0);
        await _vm.ReorderSettledAsync();
        _session.Queue.Count.Should().Be(3, "nothing has been decided yet; the row is mid-flight");

        _vm.Upcoming.Insert(1, moving);
        await _vm.ReorderSettledAsync();
        await TickAsync();

        Ids(_vm.Upcoming).Should().Equal(13, 12);
        _session.Queue.Items.Select(item => item.TrackId).Should().Equal(11, 13, 12);
    }

    // ---- remove and clear -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Removing_the_pinned_row_starts_the_one_that_took_its_place_Async()
    {
        await _session.PlayNowAsync([11, 12, 13]);
        await TickAsync();

        await _vm.RemoveAsync(_vm.NowPlaying!);
        await TickAsync();

        _vm.NowPlaying!.TrackId.Should().Be(12);
        Ids(_vm.Upcoming).Should().Equal(13);
        _session.Current.State.Should().Be(PlaybackState.Playing);
    }

    [Fact]
    public async Task Removing_an_upcoming_row_leaves_playback_where_it_was_Async()
    {
        await _session.PlayNowAsync([11, 12, 13]);
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(30) };
        await TickAsync();

        await _vm.RemoveAsync(_vm.Upcoming[0]);
        await TickAsync();

        _vm.NowPlaying!.TrackId.Should().Be(11);
        Ids(_vm.Upcoming).Should().Equal(13);
        _session.Current.Position.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Clearing_upcoming_empties_the_list_and_leaves_the_pinned_track_playing_Async()
    {
        await _session.PlayNowAsync([11, 12, 13]);
        await TickAsync();

        await _vm.ClearUpcomingAsync();
        await TickAsync();

        _vm.Upcoming.Should().BeEmpty();
        _vm.HasUpcoming.Should().BeFalse();
        _vm.NowPlaying!.TrackId.Should().Be(11);
        _vm.IsEmpty.Should().BeFalse("something is still playing");
        _session.Current.State.Should().Be(PlaybackState.Playing);
    }

    // ---- the time left ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Four-minute tags against a three-minute file: the current track's share has to come from the engine, which
    /// has the file open, and the upcoming ones from their rows, which is all anyone has for a file not yet opened.
    /// </summary>
    [Fact]
    public async Task The_time_left_is_the_rest_of_this_track_plus_every_upcoming_one_Async()
    {
        _engine.Duration = TimeSpan.FromMinutes(3);
        await _session.PlayNowAsync([11, 12, 13]);
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromMinutes(1) };
        await TickAsync();

        // 2 min left of this one, plus two four-minute tracks.
        _vm.RemainingText.Should().Be("10 min left");
    }

    [Fact]
    public async Task The_time_left_follows_the_position_and_not_only_the_queue_Async()
    {
        _engine.Duration = TimeSpan.FromMinutes(3);
        await _session.PlayNowAsync([11]);
        await TickAsync();
        _vm.RemainingText.Should().Be("3 min left");

        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromMinutes(2) };
        await TickAsync();

        _vm.RemainingText.Should().Be("1 min left");
    }

    [Fact]
    public async Task An_empty_queue_has_no_time_left_to_show_Async()
    {
        await TickAsync();

        _vm.RemainingText.Should().BeEmpty();
    }

    // ---- before there is a session ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_panel_is_inert_until_audio_comes_up_Async()
    {
        var pending = new StubSessionSource();
        using var vm = new QueueViewModel(pending, _tracks);

        vm.IsReady.Should().BeFalse();
        await vm.ClearUpcomingAsync();
        vm.IsEmpty.Should().BeTrue();

        pending.Session = _session;

        vm.IsReady.Should().BeTrue();
    }
}
#pragma warning restore CA1001
