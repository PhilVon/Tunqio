using FluentAssertions;
using Tunqio.Core.Playback;

namespace Tunqio.Core.Tests;

/// <summary>
/// E1-S9: the queue's ordering, shuffle, repeat and Previous rules (docs/library-and-data.md, "Play queue model").
/// Every rule here is a pure function of the queue value, so none of it needs an engine, a file or a database.
/// </summary>
public class PlayQueueTests
{
    private static readonly long[] InOrder = [1, 2, 3, 4, 5, 6, 7, 8];

    private static PlayQueue Queue(params long[] trackIds) => PlayQueue.Empty.PlayNow(trackIds);

    private static long[] Order(PlayQueue queue) => [.. queue.Items.Select(item => item.TrackId)];

    private static Guid InstanceAt(PlayQueue queue, int index) => queue.Items[index].InstanceId;

    // ---- the empty queue ----------------------------------------------------------------------------------------

    [Fact]
    public void Empty_queue_has_nothing_to_play()
    {
        PlayQueue queue = PlayQueue.Empty;

        queue.Count.Should().Be(0);
        queue.CurrentIndex.Should().BeNull();
        queue.Current.Should().BeNull();
        queue.Shuffle.Should().BeFalse();
        queue.Repeat.Should().Be(RepeatMode.Off);
        queue.PeekNext().Should().BeNull();
        queue.Advance(manual: true).Should().BeSameAs(queue);
        queue.Back(TimeSpan.Zero).Should().BeSameAs(queue);
    }

    [Fact]
    public void Null_track_ids_are_rejected()
    {
        PlayQueue queue = PlayQueue.Empty;

        queue.Invoking(q => q.PlayNow(null!)).Should().Throw<ArgumentNullException>();
        queue.Invoking(q => q.PlayNext(null!)).Should().Throw<ArgumentNullException>();
        queue.Invoking(q => q.Enqueue(null!)).Should().Throw<ArgumentNullException>();
        queue.Invoking(q => q.ToggleShuffle(null!)).Should().Throw<ArgumentNullException>();
    }

    // ---- PlayNow ------------------------------------------------------------------------------------------------

    [Fact]
    public void PlayNow_replaces_the_queue_and_starts_at_the_front()
    {
        PlayQueue queue = Queue(10, 11, 12).PlayNow([20, 21]);

        Order(queue).Should().Equal(20, 21);
        queue.CurrentIndex.Should().Be(0);
        queue.Current!.TrackId.Should().Be(20);
    }

    [Fact]
    public void PlayNow_from_a_later_track_keeps_the_earlier_ones_behind_it()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow([1, 2, 3, 4, 5], startIndex: 3);

        Order(queue).Should().Equal(1, 2, 3, 4, 5);
        queue.CurrentIndex.Should().Be(3);
        queue.Back(TimeSpan.Zero).Current!.TrackId.Should().Be(3);
    }

    [Theory]
    [InlineData(-4, 0)]
    [InlineData(99, 2)]
    public void PlayNow_clamps_the_start_index_into_the_queue(int startIndex, int expected)
    {
        PlayQueue.Empty.PlayNow([1, 2, 3], startIndex).CurrentIndex.Should().Be(expected);
    }

    [Fact]
    public void PlayNow_with_no_tracks_clears_the_queue_but_keeps_the_modes()
    {
        PlayQueue queue = Queue(1, 2, 3).WithRepeat(RepeatMode.All).ToggleShuffle(new Random(1)).PlayNow([]);

        queue.Count.Should().Be(0);
        queue.CurrentIndex.Should().BeNull();
        queue.Shuffle.Should().BeTrue();
        queue.Repeat.Should().Be(RepeatMode.All);
    }

    [Fact]
    public void PlayNow_while_shuffle_is_on_shuffles_the_new_queue_around_the_started_track()
    {
        PlayQueue shuffled = Queue(1, 2).ToggleShuffle(new Random(7));

        PlayQueue queue = shuffled.PlayNow([1, 2, 3, 4, 5, 6, 7, 8], startIndex: 1, rng: new Random(7));

        queue.Shuffle.Should().BeTrue();
        Order(queue).Should().BeEquivalentTo([1, 2, 3, 4, 5, 6, 7, 8]);
        Order(queue).Take(2).Should().Equal([1L, 2L], "what precedes the started track keeps its order");
        Order(queue).Should().NotEqual(InOrder);
        queue.Current!.TrackId.Should().Be(2);
        Order(queue.ToggleShuffle(new Random(7))).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
    }

    [Fact]
    public void PlayNow_while_shuffle_is_on_needs_no_rng_of_its_own()
    {
        PlayQueue queue = Queue(1, 2).ToggleShuffle(new Random(7)).PlayNow([1, 2, 3, 4, 5, 6, 7, 8]);

        queue.Shuffle.Should().BeTrue();
        Order(queue).Should().BeEquivalentTo([1, 2, 3, 4, 5, 6, 7, 8]);
        queue.Current!.TrackId.Should().Be(1);
    }

    // ---- AC-58: shuffle on then off restores the original order with the current track in place -------------------

    [Fact]
    public void Shuffle_on_then_off_restores_the_original_order_with_the_current_track_in_place()
    {
        PlayQueue original = PlayQueue.Empty.PlayNow([1, 2, 3, 4, 5, 6, 7, 8], startIndex: 2);
        QueueItem current = original.Current!;

        PlayQueue shuffled = original.ToggleShuffle(new Random(4));
        shuffled.Shuffle.Should().BeTrue();
        Order(shuffled).Should().NotEqual(InOrder);
        shuffled.Current.Should().Be(current);

        PlayQueue restored = shuffled.ToggleShuffle(new Random(4));
        restored.Shuffle.Should().BeFalse();
        Order(restored).Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
        restored.Current.Should().Be(current);
        restored.CurrentIndex.Should().Be(2);
    }

    [Fact]
    public void Shuffle_leaves_what_has_already_been_played_in_its_heard_order()
    {
        PlayQueue shuffled = PlayQueue.Empty.PlayNow([1, 2, 3, 4, 5, 6, 7, 8], startIndex: 3).ToggleShuffle(new Random(9));

        Order(shuffled).Take(4).Should().Equal(1, 2, 3, 4);
        shuffled.CurrentIndex.Should().Be(3);
        Order(shuffled).Skip(4).Should().BeEquivalentTo([5, 6, 7, 8]);
    }

    [Fact]
    public void Shuffle_with_nothing_left_to_shuffle_is_harmless()
    {
        PlayQueue shuffled = Queue(1).ToggleShuffle(new Random(3));

        Order(shuffled).Should().Equal(1);
        shuffled.CurrentIndex.Should().Be(0);
    }

    [Fact]
    public void Shuffle_with_nothing_current_shuffles_the_whole_queue()
    {
        PlayQueue exhausted = PlayQueue.Empty.PlayNow([1, 2, 3, 4, 5, 6, 7, 8], startIndex: 7).Advance(manual: true);
        exhausted.CurrentIndex.Should().BeNull();

        PlayQueue shuffled = exhausted.ToggleShuffle(new Random(5));

        Order(shuffled).Should().BeEquivalentTo([1, 2, 3, 4, 5, 6, 7, 8]);
        Order(shuffled).Should().NotEqual(InOrder);
        shuffled.CurrentIndex.Should().BeNull();
    }

    // ---- AC-59: Repeat One ---------------------------------------------------------------------------------------

    [Fact]
    public void Repeat_one_replays_a_track_that_ends_on_its_own()
    {
        PlayQueue queue = Queue(1, 2, 3).WithRepeat(RepeatMode.One);

        queue.Advance(manual: false).CurrentIndex.Should().Be(0);
        queue.PeekNext().Should().Be(queue.Current, "the engine pre-opens the same track again");
    }

    [Fact]
    public void Repeat_one_still_advances_on_a_manual_next()
    {
        PlayQueue queue = Queue(1, 2, 3).WithRepeat(RepeatMode.One);

        queue.Advance(manual: true).Current!.TrackId.Should().Be(2);
    }

    [Fact]
    public void Repeat_one_wraps_when_a_manual_next_runs_off_the_end()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow([1, 2, 3], startIndex: 2).WithRepeat(RepeatMode.One);

        queue.Advance(manual: true).CurrentIndex.Should().Be(0);
    }

    // ---- AC-60: Repeat All wraps, Off stops ----------------------------------------------------------------------

    [Fact]
    public void Repeat_all_wraps_at_the_end_of_the_queue()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow([1, 2, 3], startIndex: 2).WithRepeat(RepeatMode.All);

        queue.PeekNext()!.TrackId.Should().Be(1);
        queue.Advance(manual: false).CurrentIndex.Should().Be(0);
    }

    [Fact]
    public void Repeat_off_stops_at_the_end_of_the_queue()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow([1, 2, 3], startIndex: 2);

        queue.PeekNext().Should().BeNull();

        PlayQueue stopped = queue.Advance(manual: false);
        stopped.CurrentIndex.Should().BeNull();
        stopped.Current.Should().BeNull();
        stopped.PeekNext().Should().BeNull();
        stopped.Count.Should().Be(3, "the queue is still there, nothing is playing from it");
    }

    [Fact]
    public void Advancing_with_nothing_current_does_nothing()
    {
        PlayQueue stopped = Queue(1, 2).PlayNow([]);

        stopped.Advance(manual: true).Should().BeSameAs(stopped);
    }

    [Fact]
    public void Peek_next_is_the_following_item_in_play_order()
    {
        PlayQueue queue = Queue(1, 2, 3);

        queue.PeekNext()!.Should().Be(queue.Items[1]);
    }

    // ---- Previous ------------------------------------------------------------------------------------------------

    [Fact]
    public void Previous_early_in_a_track_goes_to_the_previous_track()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow([1, 2, 3], startIndex: 1);

        queue.Back(TimeSpan.FromSeconds(2.9)).Current!.TrackId.Should().Be(1);
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(41.0)]
    public void Previous_past_the_restart_window_keeps_the_current_track(double seconds)
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow([1, 2, 3], startIndex: 1);

        queue.Back(TimeSpan.FromSeconds(seconds)).Should().BeSameAs(queue);
    }

    [Fact]
    public void Previous_on_the_first_track_restarts_it_when_repeat_is_off()
    {
        PlayQueue queue = Queue(1, 2, 3);

        queue.Back(TimeSpan.Zero).Should().BeSameAs(queue);
    }

    [Fact]
    public void Previous_on_the_first_track_wraps_to_the_last_when_repeat_is_on()
    {
        PlayQueue queue = Queue(1, 2, 3).WithRepeat(RepeatMode.All);

        queue.Back(TimeSpan.Zero).Current!.TrackId.Should().Be(3);
    }

    [Fact]
    public void Previous_with_nothing_current_does_nothing()
    {
        PlayQueue stopped = PlayQueue.Empty.PlayNow([1, 2], startIndex: 1).Advance(manual: false);

        stopped.Back(TimeSpan.Zero).Should().BeSameAs(stopped);
    }

    // ---- AC-61: the same track twice is two items -----------------------------------------------------------------

    [Fact]
    public void The_same_track_twice_is_two_independent_items()
    {
        PlayQueue queue = Queue(7, 7, 8);

        queue.Items[0].InstanceId.Should().NotBe(queue.Items[1].InstanceId);

        PlayQueue afterRemove = queue.Remove(InstanceAt(queue, 0));
        Order(afterRemove).Should().Equal([7L, 8L], "removing one copy leaves the other");

        PlayQueue afterMove = queue.Move(InstanceAt(queue, 1), 2);
        Order(afterMove).Should().Equal(7, 8, 7);
        afterMove.Items[0].InstanceId.Should().Be(InstanceAt(queue, 0));

        queue.Advance(manual: true).Current.Should().Be(queue.Items[1], "the second copy is its own stop on the way");
    }

    // ---- PlayNext and Enqueue -------------------------------------------------------------------------------------

    [Fact]
    public void Play_next_inserts_after_the_current_item_and_keeps_it_current()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow([1, 2, 3], startIndex: 1).PlayNext([98, 99]);

        Order(queue).Should().Equal(1, 2, 98, 99, 3);
        queue.CurrentIndex.Should().Be(1);
        queue.PeekNext()!.TrackId.Should().Be(98);
    }

    [Fact]
    public void Play_next_with_nothing_current_goes_to_the_front()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNext([5, 6]);

        Order(queue).Should().Equal(5, 6);
        queue.CurrentIndex.Should().BeNull();
    }

    [Fact]
    public void Play_next_survives_turning_shuffle_off()
    {
        PlayQueue queue = PlayQueue.Empty
            .PlayNow([1, 2, 3, 4, 5, 6], startIndex: 1)
            .ToggleShuffle(new Random(2))
            .PlayNext([99]);

        Order(queue).Should().Contain(99);
        Order(queue.ToggleShuffle(new Random(2))).Should().Equal(1, 2, 99, 3, 4, 5, 6);
    }

    [Fact]
    public void Enqueue_appends_to_the_end()
    {
        PlayQueue queue = Queue(1, 2).Enqueue([3, 4]);

        Order(queue).Should().Equal(1, 2, 3, 4);
        queue.CurrentIndex.Should().Be(0);
    }

    [Fact]
    public void Adding_no_tracks_changes_nothing()
    {
        PlayQueue queue = Queue(1, 2);

        queue.PlayNext([]).Should().BeSameAs(queue);
        queue.Enqueue([]).Should().BeSameAs(queue);
    }

    // ---- Remove ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Removing_an_item_that_is_not_queued_changes_nothing()
    {
        PlayQueue queue = Queue(1, 2);

        queue.Remove(Guid.NewGuid()).Should().BeSameAs(queue);
    }

    [Fact]
    public void Removing_before_the_current_item_keeps_it_current()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow([1, 2, 3], startIndex: 2);

        PlayQueue after = queue.Remove(InstanceAt(queue, 0));

        Order(after).Should().Equal(2, 3);
        after.CurrentIndex.Should().Be(1);
        after.Current!.TrackId.Should().Be(3);
    }

    [Fact]
    public void Removing_after_the_current_item_keeps_it_current()
    {
        PlayQueue queue = Queue(1, 2, 3);

        PlayQueue after = queue.Remove(InstanceAt(queue, 2));

        Order(after).Should().Equal(1, 2);
        after.CurrentIndex.Should().Be(0);
    }

    [Fact]
    public void Removing_the_current_item_makes_the_next_one_current()
    {
        PlayQueue queue = Queue(1, 2, 3);

        PlayQueue after = queue.Remove(InstanceAt(queue, 0));

        Order(after).Should().Equal(2, 3);
        after.CurrentIndex.Should().Be(0);
        after.Current!.TrackId.Should().Be(2);
    }

    [Fact]
    public void Removing_the_current_item_when_it_is_last_leaves_nothing_current()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow([1, 2], startIndex: 1);

        PlayQueue after = queue.Remove(InstanceAt(queue, 1));

        Order(after).Should().Equal(1);
        after.CurrentIndex.Should().BeNull();
    }

    [Fact]
    public void Removing_while_shuffled_removes_from_the_restored_order_too()
    {
        PlayQueue shuffled = PlayQueue.Empty.PlayNow([1, 2, 3, 4, 5], startIndex: 0).ToggleShuffle(new Random(6));
        Guid gone = shuffled.Items.Single(item => item.TrackId == 4).InstanceId;

        PlayQueue after = shuffled.Remove(gone).ToggleShuffle(new Random(6));

        Order(after).Should().Equal(1, 2, 3, 5);
    }

    // ---- Move -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Moving_an_item_that_is_not_queued_changes_nothing()
    {
        PlayQueue queue = Queue(1, 2);

        queue.Move(Guid.NewGuid(), 0).Should().BeSameAs(queue);
    }

    [Fact]
    public void Moving_an_item_where_it_already_is_changes_nothing()
    {
        PlayQueue queue = Queue(1, 2, 3);

        queue.Move(InstanceAt(queue, 1), 1).Should().BeSameAs(queue);
    }

    [Fact]
    public void Moving_reorders_the_queue_and_the_current_item_follows_it()
    {
        PlayQueue queue = Queue(1, 2, 3, 4);

        PlayQueue after = queue.Move(InstanceAt(queue, 0), 2);

        Order(after).Should().Equal(2, 3, 1, 4);
        after.CurrentIndex.Should().Be(2, "track 1 was current and still is");
    }

    [Fact]
    public void Moving_clamps_the_target_index_into_the_queue()
    {
        PlayQueue queue = Queue(1, 2, 3);

        Order(queue.Move(InstanceAt(queue, 0), 99)).Should().Equal(2, 3, 1);
        Order(queue.Move(InstanceAt(queue, 2), -7)).Should().Equal(3, 1, 2);
    }

    [Fact]
    public void Moving_while_shuffle_is_off_moves_the_order_shuffle_restores()
    {
        PlayQueue queue = Queue(1, 2, 3, 4);
        PlayQueue moved = queue.Move(InstanceAt(queue, 0), 2);

        Order(moved).Should().Equal(2, 3, 1, 4);
        Order(moved.ToggleShuffle(new Random(8)).ToggleShuffle(new Random(8))).Should().Equal(2, 3, 1, 4);
    }

    [Fact]
    public void Moving_while_shuffle_is_on_leaves_the_order_shuffle_restores_alone()
    {
        PlayQueue shuffled = PlayQueue.Empty.PlayNow([1, 2, 3, 4, 5], startIndex: 0).ToggleShuffle(new Random(6));
        Guid last = shuffled.Items[4].InstanceId;

        PlayQueue after = shuffled.Move(last, 1);

        after.Items[1].InstanceId.Should().Be(last);
        Order(after.ToggleShuffle(new Random(6))).Should().Equal(1, 2, 3, 4, 5);
    }

    // ---- ClearUpcoming (E2-S5) --------------------------------------------------------------------------------------

    [Fact]
    public void Clearing_upcoming_keeps_what_is_playing_and_what_was_played()
    {
        PlayQueue queue = PlayQueue.Empty.PlayNow([1, 2, 3, 4, 5], startIndex: 2);

        PlayQueue after = queue.ClearUpcoming();

        // The two behind the current track stay: they are what Previous walks back through.
        Order(after).Should().Equal(1, 2, 3);
        after.Current!.TrackId.Should().Be(3);
        after.PeekNext().Should().BeNull();
    }

    [Fact]
    public void Clearing_upcoming_with_nothing_current_empties_the_queue()
    {
        PlayQueue exhausted = Queue(1, 2).Advance(manual: true).Advance(manual: true);
        exhausted.Current.Should().BeNull();

        PlayQueue after = exhausted.ClearUpcoming();

        after.Count.Should().Be(0);
        after.CurrentIndex.Should().BeNull();
    }

    [Fact]
    public void Clearing_upcoming_keeps_repeat_and_shuffle()
    {
        PlayQueue queue = Queue(1, 2, 3).WithRepeat(RepeatMode.All).ToggleShuffle(new Random(5));

        PlayQueue after = queue.ClearUpcoming();

        after.Repeat.Should().Be(RepeatMode.All);
        after.Shuffle.Should().BeTrue();
    }

    /// <summary>
    /// Under shuffle the two orders disagree about which items are after the current one, so the added order has to
    /// lose the same items the play order lost and not simply the same number of them — otherwise turning shuffle
    /// off afterwards brings back tracks the user has just cleared.
    /// </summary>
    [Fact]
    public void Clearing_upcoming_under_shuffle_removes_the_same_items_from_the_order_shuffle_restores()
    {
        // Shuffled and then played into: the two orders now disagree about which items are behind the current one.
        PlayQueue shuffled = Queue(1, 2, 3, 4, 5).ToggleShuffle(new Random(6)).Advance(manual: true).Advance(manual: true);
        long[] heard = [.. shuffled.Items.Take(3).Select(item => item.TrackId).Order()];
        heard.Should().NotEqual(
            [1L, 2L, 3L], "otherwise both orders agree on what was heard and truncating would have worked too");

        PlayQueue after = shuffled.ClearUpcoming().ToggleShuffle(new Random(6));

        Order(after).Should().Equal(heard, "unshuffling restores the added order of exactly what survived");
    }

    // ---- restoring a saved queue (E1-S10) --------------------------------------------------------------------------

    [Fact]
    public void A_queue_that_was_never_shuffled_restores_from_one_order()
    {
        PlayQueue original = Queue(1, 2, 3);

        PlayQueue restored = PlayQueue.Restore(original.AddedOrder, null, 1, shuffle: false, RepeatMode.All);

        restored.Items.Should().Equal(original.Items);
        restored.AddedOrder.Should().Equal(original.Items);
        restored.CurrentIndex.Should().Be(1);
        restored.Shuffle.Should().BeFalse();
        restored.Repeat.Should().Be(RepeatMode.All);
    }

    [Fact]
    public void A_shuffled_queue_restores_both_orders_so_it_can_still_be_unshuffled()
    {
        PlayQueue shuffled = PlayQueue.Empty.PlayNow([1, 2, 3, 4, 5, 6, 7, 8]).ToggleShuffle(new Random(4));

        PlayQueue restored = PlayQueue.Restore(
            shuffled.AddedOrder, shuffled.Items, shuffled.CurrentIndex, shuffle: true, shuffled.Repeat);

        restored.Items.Should().Equal(shuffled.Items);
        restored.Current.Should().Be(shuffled.Current);
        Order(restored.ToggleShuffle(new Random(4))).Should().Equal(InOrder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(3)]
    public void A_current_index_outside_the_restored_queue_restores_as_nothing_current(int? currentIndex)
    {
        PlayQueue restored = PlayQueue.Restore(Queue(1, 2, 3).Items, null, currentIndex, shuffle: false, RepeatMode.Off);

        restored.Count.Should().Be(3);
        restored.CurrentIndex.Should().BeNull();
    }

    [Fact]
    public void Restoring_needs_an_order()
    {
        FluentActions.Invoking(() => PlayQueue.Restore(null!, null, 0, false, RepeatMode.Off))
            .Should().Throw<ArgumentNullException>();
    }

    // ---- capture ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_capture_carries_both_orders_and_the_position()
    {
        PlayQueue shuffled = PlayQueue.Empty.PlayNow([1, 2, 3, 4, 5, 6, 7, 8], startIndex: 2)
            .WithRepeat(RepeatMode.One)
            .ToggleShuffle(new Random(4));

        QueueState state = QueueState.Capture(shuffled, TimeSpan.FromSeconds(9), 1_700_000_000_000);

        state.Items.Should().Equal(shuffled.Items);
        state.AddedOrder.Should().Equal(shuffled.AddedOrder);
        state.CurrentIndex.Should().Be(shuffled.CurrentIndex);
        state.Position.Should().Be(TimeSpan.FromSeconds(9));
        state.Shuffle.Should().BeTrue();
        state.Repeat.Should().Be(RepeatMode.One);
        state.SavedAt.Should().Be(1_700_000_000_000);
        state.ToQueue().Items.Should().Equal(shuffled.Items);
    }

    [Fact]
    public void Capturing_needs_a_queue()
    {
        FluentActions.Invoking(() => QueueState.Capture(null!, null, 0)).Should().Throw<ArgumentNullException>();
    }
}
