using Tunqio.App.Library;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

/// <summary>
/// E5-S4: the Curation editor. Flow 6 up to Ctrl+Z against an in-memory store with the repository's semantics; undo and
/// redo as snapshots that restore what was stored; the list arithmetic behind drops and moves; and the dual pane's view
/// model choosing its target and source, filtering, and showing the batch bar only for a selection of more than one.
/// </summary>
public sealed class CurationTests
{
    private readonly MemoryPlaylists _store = new();
    private readonly FakeTrackRepository _tracks = new();

    public CurationTests()
    {
        for (int i = 1; i <= 8; i++)
        {
            TrackDto track = Rows.Track(i, "Track " + i, albumTitle: i <= 4 ? "Alpha" : "Beta", durationMs: 60_000);
            _store.Library[i] = track;
            _tracks.Rows.Add(track);
        }
    }

    private CurationViewModel Curation() => new(_store, _tracks);

    // ---- flow 6 and the history --------------------------------------------------------------------------------------

    [Fact]
    public async Task Flow_6_add_six_reorder_two_and_ctrl_z_undoes_only_the_reorder_Async()
    {
        long sunday = _store.Create("Sunday");
        using var editor = new PlaylistEditor(_store);
        await editor.OpenAsync(sunday);

        await editor.AddAsync([1, 2, 3, 4, 5, 6]);
        await editor.MoveAsync([0], +1);
        _store.Items(sunday).Should().Equal(2, 1, 3, 4, 5, 6);

        (await editor.UndoAsync()).Should().BeTrue();

        _store.Items(sunday).Should().Equal(1, 2, 3, 4, 5, 6);
        editor.Tracks.Select(t => t.Id).Should().Equal(1, 2, 3, 4, 5, 6);
        editor.UndoLabel.Should().Be("Add 6 tracks", "the add is still there to undo");
        editor.RedoLabel.Should().Be("Move down");
    }

    [Fact]
    public async Task Redo_puts_the_undone_edit_back_and_a_new_edit_forgets_it_Async()
    {
        long id = _store.Create("Mix");
        using var editor = new PlaylistEditor(_store);
        await editor.OpenAsync(id);
        await editor.AddAsync([1, 2, 3]);
        await editor.RemoveAsync([1]);

        await editor.UndoAsync();
        (await editor.RedoAsync()).Should().BeTrue();
        _store.Items(id).Should().Equal(1, 3);

        await editor.UndoAsync();
        await editor.AddAsync([4]);
        editor.CanRedo.Should().BeFalse("a new edit is a different future");
        (await editor.RedoAsync()).Should().BeFalse();
        _store.Items(id).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task Undoing_a_remove_puts_the_tracks_back_in_the_middle_Async()
    {
        long id = _store.Create("Middle", 1, 2, 3, 4, 5);
        using var editor = new PlaylistEditor(_store);
        await editor.OpenAsync(id);

        await editor.RemoveAsync([1, 3]);
        _store.Items(id).Should().Equal(1, 3, 5);
        await editor.UndoAsync();

        _store.Items(id).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_is_not_a_step_Async()
    {
        long id = _store.Create("Still", 1, 2, 3);
        using var editor = new PlaylistEditor(_store);
        await editor.OpenAsync(id);

        (await editor.MoveAsync([0], -1)).Should().BeFalse();
        (await editor.ReorderAsync([0, 1, 2])).Should().BeFalse();
        (await editor.RemoveAsync([7])).Should().BeFalse();

        editor.CanUndo.Should().BeFalse();
        _store.Writes.Should().Be(0);
    }

    [Fact]
    public async Task A_drop_inserts_where_it_landed_and_past_the_end_appends_Async()
    {
        long id = _store.Create("Drop", 1, 2);
        using var editor = new PlaylistEditor(_store);
        await editor.OpenAsync(id);

        await editor.AddAsync([5, 6], position: 1);
        await editor.AddAsync([7], position: 99);

        _store.Items(id).Should().Equal(1, 5, 6, 2, 7);
    }

    /// <summary>The repository skips a track the library no longer has; redo must restore the list as it was stored.</summary>
    [Fact]
    public async Task Redo_restores_what_was_stored_not_what_was_asked_for_Async()
    {
        long id = _store.Create("Gone", 1);
        using var editor = new PlaylistEditor(_store);
        await editor.OpenAsync(id);

        await editor.AddAsync([999, 2]);
        await editor.UndoAsync();
        await editor.RedoAsync();

        _store.Items(id).Should().Equal(1, 2);
        editor.Tracks.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_history_keeps_its_depth_and_drops_the_oldest_Async()
    {
        long id = _store.Create("Deep");
        using var editor = new PlaylistEditor(_store);
        await editor.OpenAsync(id);
        for (int i = 0; i < PlaylistEditor.Depth + 5; i++)
        {
            await editor.AddAsync([1]);
        }

        int undone = 0;
        while (await editor.UndoAsync())
        {
            undone++;
        }

        undone.Should().Be(PlaylistEditor.Depth);
        _store.Items(id).Should().HaveCount(5, "the five oldest adds fell off the end of the history");
    }

    [Fact]
    public async Task Opening_another_playlist_starts_a_fresh_history_Async()
    {
        long one = _store.Create("One");
        long two = _store.Create("Two");
        using var editor = new PlaylistEditor(_store);
        await editor.OpenAsync(one);
        await editor.AddAsync([1]);

        await editor.OpenAsync(two);

        editor.CanUndo.Should().BeFalse("undo in Two must never rewrite One");
    }

    [Fact]
    public async Task A_reorder_that_is_not_a_permutation_is_refused_and_writes_nothing_Async()
    {
        long id = _store.Create("Bad", 1, 2, 3);
        using var editor = new PlaylistEditor(_store);
        await editor.OpenAsync(id);

        Func<Task> reorder = () => editor.ReorderAsync([0, 0, 1]);

        await reorder.Should().ThrowAsync<ArgumentException>();
        _store.Items(id).Should().Equal(1, 2, 3);
    }

    [Theory]
    [InlineData(new[] { 1, 2 }, -1, new long[] { 1, 2, 0, 3, 4 })]
    [InlineData(new[] { 0, 1 }, -1, new long[] { 0, 1, 2, 3, 4 })] // already at the top: stays
    [InlineData(new[] { 3, 4 }, +1, new long[] { 0, 1, 2, 3, 4 })] // already at the bottom
    [InlineData(new[] { 0, 2 }, +1, new long[] { 1, 0, 3, 2, 4 })] // two apart move independently
    [InlineData(new[] { 4 }, -1, new long[] { 0, 1, 2, 4, 3 })]
    public void Moving_a_selection_moves_it_as_a_block(int[] positions, int delta, long[] expected)
    {
        PlaylistEditor.Shift([0, 1, 2, 3, 4], positions, delta).Should().Equal(expected);
    }

    [Fact]
    public void The_selection_follows_the_rows_it_moved()
    {
        PlaylistEditor.ShiftedPositions(5, [1, 2], -1).Should().Equal(0, 1);
        PlaylistEditor.ShiftedPositions(5, [0, 1], -1).Should().Equal(0, 1);
        PlaylistEditor.ShiftedPositions(5, [0, 2], +1).Should().Equal(1, 3);
    }

    // ---- the dual pane ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Entering_curation_with_no_target_opens_the_first_playlist_over_the_library_Async()
    {
        _store.Create("Zulu", 1);
        long alpha = _store.Create("Alpha", 2, 3);
        using CurationViewModel vm = Curation();

        await vm.ActivateAsync();

        vm.Target!.Id.Should().Be(alpha);
        vm.TargetRows.Select(r => r.Track.Id).Should().Equal(2, 3);
        vm.Sources.Select(s => s.Name).Should().Equal("Library", "Alpha", "Zulu");
        vm.Source.Should().Be(CurationSource.Library);
        vm.SourceItems.Should().HaveCount(8);
        _tracks.Queries[^1].Sort.Should().Be(TrackSort.Album, "an album's tracks sit together in the source");
    }

    [Fact]
    public async Task Edit_in_curation_opens_the_playlist_it_named_and_with_none_there_is_no_target_Async()
    {
        using CurationViewModel empty = Curation();
        await empty.ActivateAsync();
        empty.NoTarget.Should().BeTrue();

        _store.Create("Alpha");
        long sunday = _store.Create("Sunday", 4);
        using CurationViewModel vm = Curation();
        vm.RequestEdit(sunday);
        await vm.ActivateAsync();

        vm.Target!.Name.Should().Be("Sunday");
    }

    [Fact]
    public async Task New_playlist_becomes_the_target_and_a_blank_name_does_nothing_Async()
    {
        using CurationViewModel vm = Curation();
        await vm.ActivateAsync();

        (await vm.CreatePlaylistAsync("  ")).Should().BeFalse();
        (await vm.CreatePlaylistAsync("Sunday")).Should().BeTrue();

        vm.Target!.Name.Should().Be("Sunday");
        vm.TargetIsEmpty.Should().BeTrue();
        vm.Sources.Select(s => s.Name).Should().Contain("Sunday");
    }

    [Fact]
    public async Task Adding_from_the_source_shows_the_rows_the_total_and_what_undo_would_undo_Async()
    {
        _store.Create("Sunday");
        using CurationViewModel vm = Curation();
        await vm.ActivateAsync();

        await vm.AddAsync([vm.SourceItems[0].Id, vm.SourceItems[1].Id]);

        vm.TargetRows.Should().HaveCount(2);
        vm.TargetSummary.Should().Be("2 tracks · 2 min");
        vm.UndoName.Should().Be("Undo add 2 tracks");
        vm.RedoName.Should().Be("Redo");
        await vm.UndoAsync();
        vm.TargetIsEmpty.Should().BeTrue();
        vm.RedoName.Should().Be("Redo add 2 tracks");
    }

    [Fact]
    public async Task The_batch_bar_shows_only_for_a_selection_of_more_than_one_Async()
    {
        _store.Create("Sunday", 1, 2);
        using CurationViewModel vm = Curation();
        await vm.ActivateAsync();

        vm.SourceSelectionCount = 1;
        vm.BatchBarVisible.Should().BeFalse();
        vm.CanAddSelection.Should().BeTrue();

        vm.SourceSelectionCount = 6;
        vm.TargetSelectionCount = 2;
        vm.BatchBarVisible.Should().BeTrue();
        vm.BatchText.Should().Be("6 selected in Library · 2 selected in Sunday");
    }

    /// <summary>Q-83: one row selected, a different row dragged. The dragged row is what goes.</summary>
    [Fact]
    public void A_drag_of_an_unselected_row_carries_that_row_not_the_selection()
    {
        TrackDto selected = _store.Library[1];
        TrackDto dragged = _store.Library[5];

        CurationViewModel.DraggedTracks([dragged], [selected]).Should().Equal(dragged);
    }

    [Fact]
    public void A_drag_of_the_selection_carries_it_in_list_order()
    {
        TrackDto[] inOrder = [_store.Library[1], _store.Library[2], _store.Library[3]];

        // The ListView hands the dragged items in the order they were selected, not the order of the list.
        CurationViewModel.DraggedTracks([inOrder[2], inOrder[0], inOrder[1]], inOrder).Should().Equal(inOrder);
        CurationViewModel.DraggedTracks([inOrder[0], _store.Library[7]], [inOrder[0], inOrder[1]]).Should().Equal(inOrder[0], _store.Library[7]);
    }

    [Fact]
    public async Task A_filter_narrows_the_library_source_and_a_playlist_source_alike_Async()
    {
        _store.Create("Mixed", 1, 5, 6);
        using CurationViewModel vm = Curation();
        await vm.ActivateAsync();

        await vm.SetFilterAsync("beta");
        vm.SourceItems.Select(t => t.Id).Should().Equal(5, 6, 7, 8);
        _tracks.Queries[^1].Text.Should().Be("beta");

        await vm.SelectSourceAsync(vm.Sources.Single(s => s.Name == "Mixed"));
        vm.SourceItems.Select(t => t.Id).Should().Equal(5, 6);
    }

    [Fact]
    public async Task A_source_that_is_the_target_follows_the_edits_Async()
    {
        _store.Create("Self", 1, 2);
        using CurationViewModel vm = Curation();
        await vm.ActivateAsync();
        await vm.SelectSourceAsync(vm.Sources.Single(s => s.Name == "Self"));

        await vm.RemoveAsync([vm.TargetRows[0]]);

        vm.SourceItems.Select(t => t.Id).Should().Equal(2);
    }

    [Fact]
    public async Task A_target_deleted_elsewhere_gives_way_to_another_on_the_next_entry_Async()
    {
        long gone = _store.Create("Gone", 1);
        long other = _store.Create("Other", 2);
        using CurationViewModel vm = Curation();
        vm.RequestEdit(gone);
        await vm.ActivateAsync();

        _store.Delete(gone);
        await vm.ActivateAsync();

        vm.Target!.Id.Should().Be(other);
        vm.CanUndo.Should().BeFalse();
    }

    /// <summary>Playlists in memory, with the repository's semantics: unknown track ids take no position, names order the list.</summary>
    private sealed class MemoryPlaylists : IPlaylistRepository
    {
        private readonly List<(long Id, string Name, List<long> Items)> _lists = [];
        private long _nextId = 1;

        public Dictionary<long, TrackDto> Library { get; } = [];

        /// <summary>How many writes reached the store, so a no-op can be seen not to have written.</summary>
        public int Writes { get; private set; }

        public long Create(string name, params long[] items)
        {
            long id = _nextId++;
            _lists.Add((id, name, [.. items]));
            return id;
        }

        public void Delete(long id) => _lists.RemoveAll(l => l.Id == id);

        public long[] Items(long id) => [.. _lists.Single(l => l.Id == id).Items];

        public Task<IReadOnlyList<PlaylistDto>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PlaylistDto>>([.. _lists.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).Select(Dto)]);

        public Task<PlaylistDetailDto?> GetDetailAsync(long id, CancellationToken ct = default)
        {
            var found = _lists.FirstOrDefault(l => l.Id == id);
            return Task.FromResult(found.Name is null ? null : new PlaylistDetailDto(Dto(found), [.. found.Items.Select(i => Library[i])]));
        }

        public Task<PlaylistDto> CreateAsync(string name, CancellationToken ct = default)
        {
            long id = Create(name.Trim());
            return Task.FromResult(Dto(_lists.Single(l => l.Id == id)));
        }

        public Task RenameAsync(long id, string name, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DeleteAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();

        public Task AddTracksAsync(long id, IReadOnlyList<long> trackIds, CancellationToken ct = default) => throw new NotSupportedException();

        public Task RemoveAtAsync(long id, IReadOnlyList<int> positions, CancellationToken ct = default) => throw new NotSupportedException();

        public Task MoveAsync(long id, int fromPosition, int toPosition, CancellationToken ct = default) => throw new NotSupportedException();

        public Task ReplaceTracksAsync(long id, IReadOnlyList<long> trackIds, CancellationToken ct = default)
        {
            int at = _lists.FindIndex(l => l.Id == id);
            if (at >= 0)
            {
                Writes++;
                _lists[at] = (id, _lists[at].Name, [.. trackIds.Where(Library.ContainsKey)]);
            }

            return Task.CompletedTask;
        }

        private PlaylistDto Dto((long Id, string Name, List<long> Items) l) =>
            new(l.Id, l.Name, 0, 0, false, l.Items.Count, l.Items.Sum(i => (long)Library[i].DurationMs));
    }
}
