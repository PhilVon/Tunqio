using Tunqio.App.Library;
using Tunqio.App.Shell;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

/// <summary>
/// E3-S10's dialog, headless (docs/ui-screens-and-flows.md, "Tag editor dialog" and flow 8). The rule the batch
/// shape turns on is that only the boxes the user changed are written: without it, opening twelve tracks to set
/// the album artist would stamp the first one's title onto the other eleven.
/// </summary>
public class TagEditorViewModelTests
{
    private static readonly TrackDto[] Twelve =
        [.. Enumerable.Range(1, 12).Select(i => Rows.Track(i, "Track " + i, albumArtist: "Old Artist", path: $@"D:\Music\{i}.flac"))];

    private static TagEditorViewModel ViewModel(FakeTagEditor editor, FakeTagWriter writer) => new(editor, writer);

    [Fact]
    public async Task A_single_track_loads_the_values_the_file_holds_Async()
    {
        var writer = new FakeTagWriter();
        writer.Files[@"D:\Music\1.flac"] = new TagSnapshot("First Light", ["Night Signal"], "Aurora Lines", "Night Signal", 2019, 1, 1, ["Ambient"]);
        TagEditorViewModel vm = ViewModel(new FakeTagEditor(), writer);

        await vm.LoadAsync([Twelve[0]]);

        vm.IsBatch.Should().BeFalse();
        vm.Header.Should().Be("Edit tags");
        vm.Title.Should().Be("First Light", "the dialog shows what the file says, not what the scanner guessed from it");
        vm.Artists.Should().Be("Night Signal");
        vm.Year.Should().Be("2019");
        vm.Genres.Should().Be("Ambient");
        vm.HasChanges.Should().BeFalse("nothing has been typed yet");
        vm.CanConfirm.Should().BeFalse();
        vm.Files.Should().ContainSingle().Which.Display.Should().Be("Track 1");
    }

    [Fact]
    public async Task A_batch_shows_what_the_selection_agrees_on_and_leaves_the_rest_blank_Async()
    {
        var writer = new FakeTagWriter();
        foreach (TrackDto track in Twelve)
        {
            // Same album artist and year, a different title each: the shared fields fill in, the title does not.
            writer.Files[track.Path] = new TagSnapshot(track.Title, ["Night Signal"], "Aurora Lines", "Old Artist", 2019, 1, 1);
        }

        TagEditorViewModel vm = ViewModel(new FakeTagEditor(), writer);
        await vm.LoadAsync(Twelve);

        vm.IsBatch.Should().BeTrue();
        vm.Header.Should().Be("Edit tags — 12 tracks");
        vm.AlbumArtist.Should().Be("Old Artist", "every selected track says the same thing, so the box can show it");
        vm.Year.Should().Be("2019");
        vm.Title.Should().BeEmpty("the titles differ, so the box shows the (multiple values) placeholder instead");
        vm.Files.Should().HaveCount(12);
    }

    // ---- what a screen reader is told about a box the selection disagrees on (T-123) --------------------------

    [Fact]
    public async Task A_batch_marks_as_mixed_only_the_fields_the_selection_disagrees_on_Async()
    {
        var writer = new FakeTagWriter();
        foreach (TrackDto track in Twelve)
        {
            // Titles and track numbers differ; the rest agree, and Disc agrees on being blank, which must not read
            // as "they differ" just because the box is empty.
            writer.Files[track.Path] = new TagSnapshot(track.Title, ["Night Signal"], "Aurora Lines", "Old Artist", 2019, (int)track.Id, null, ["Ambient"]);
        }

        TagEditorViewModel vm = ViewModel(new FakeTagEditor(), writer);
        await vm.LoadAsync(Twelve);

        vm.IsTitleMixed.Should().BeTrue("the twelve titles differ");
        vm.IsTrackNoMixed.Should().BeTrue();
        vm.IsArtistsMixed.Should().BeFalse("every track has the same artist");
        vm.IsAlbumTitleMixed.Should().BeFalse();
        vm.IsAlbumArtistMixed.Should().BeFalse();
        vm.IsYearMixed.Should().BeFalse();
        vm.IsGenresMixed.Should().BeFalse();
        vm.IsDiscNoMixed.Should().BeFalse("a blank every track shares is agreement, not disagreement");
    }

    [Fact]
    public async Task Typing_in_a_mixed_box_stops_it_being_mixed_and_blanking_it_again_restores_it_Async()
    {
        var writer = new FakeTagWriter();
        foreach (TrackDto track in Twelve)
        {
            writer.Files[track.Path] = new TagSnapshot(track.Title, AlbumArtist: "Old Artist");
        }

        TagEditorViewModel vm = ViewModel(new FakeTagEditor(), writer);
        await vm.LoadAsync(Twelve);
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Title = "One Title For All";

        vm.IsTitleMixed.Should().BeFalse("the box will now be written, so it no longer stands for 'left as it is'");
        raised.Should().Contain(nameof(TagEditorViewModel.IsTitleMixed), "the dialog's HelpText binding only moves when told to");
        vm.BuildEdit().Title.Should().Be("One Title For All");

        vm.Title = string.Empty;

        vm.IsTitleMixed.Should().BeTrue("blank again matches the load, so the field is untouched and still differs across the tracks");
        vm.BuildEdit().Title.Should().BeNull("the help text and the write agree: a mixed box is not written");
    }

    [Fact]
    public async Task A_single_track_has_no_mixed_fields_even_where_it_is_blank_Async()
    {
        var writer = new FakeTagWriter();
        writer.Files[Twelve[0].Path] = new TagSnapshot("First Light");
        TagEditorViewModel vm = ViewModel(new FakeTagEditor(), writer);
        await vm.LoadAsync([Twelve[0]]);

        new[] { vm.IsTitleMixed, vm.IsArtistsMixed, vm.IsAlbumTitleMixed, vm.IsAlbumArtistMixed, vm.IsYearMixed, vm.IsTrackNoMixed, vm.IsDiscNoMixed, vm.IsGenresMixed }
            .Should().AllSatisfy(mixed => mixed.Should().BeFalse("one track cannot disagree with itself"));
    }

    [Fact]
    public async Task A_batch_writes_only_the_field_that_was_changed_Async()
    {
        var writer = new FakeTagWriter();
        foreach (TrackDto track in Twelve)
        {
            writer.Files[track.Path] = new TagSnapshot(track.Title, ["Night Signal"], "Aurora Lines", "Old Artist", 2019, 1, 1);
        }

        var editor = new FakeTagEditor();
        TagEditorViewModel vm = ViewModel(editor, writer);
        await vm.LoadAsync(Twelve);

        vm.AlbumArtist = "Batch Artist";

        vm.HasChanges.Should().BeTrue();
        vm.CanConfirm.Should().BeTrue();
        TagEditReport? report = await vm.ConfirmAsync();

        report.Should().NotBeNull();
        editor.Applied.Should().ContainSingle();
        TagEdit edit = editor.Applied[0].Edit;
        edit.AlbumArtist.Should().Be("Batch Artist");
        edit.Title.Should().BeNull("the untouched boxes must not be written, or the whole selection would take the first track's title");
        edit.AlbumTitle.Should().BeNull();
        edit.Year.Should().BeNull();
        edit.Artists.Should().BeNull();
        editor.Applied[0].Targets.Should().HaveCount(12);
    }

    [Fact]
    public async Task Confirm_moves_the_progress_bar_and_marks_every_file_in_the_list_Async()
    {
        var writer = new FakeTagWriter();
        foreach (TrackDto track in Twelve)
        {
            writer.Files[track.Path] = new TagSnapshot(track.Title, AlbumArtist: "Old Artist");
        }

        // The eleventh file fails; the batch must still finish and the list must say which one it was.
        var editor = new FakeTagEditor { FailPath = Twelve[10].Path };
        TagEditorViewModel vm = ViewModel(editor, writer);
        await vm.LoadAsync(Twelve);
        vm.AlbumArtist = "Batch Artist";

        TagEditReport? report = await vm.ConfirmAsync();

        report!.Written.Should().Be(11);
        report.Failed.Should().Be(1);
        editor.Progress.Should().HaveCount(13, "one sample before each of the twelve files and one at the end");
        vm.Progress.Should().Be(1);
        vm.ProgressText.Should().Be("11 files updated, 1 failed.");
        vm.Files.Count(f => f.Status == "Updated").Should().Be(11);
        vm.Files.Single(f => f.Failed).Display.Should().Be("Track 11");
        vm.Error.Should().Contain("left untouched", "a failed write leaves the original alone and the user should be told so");
        vm.IsWriting.Should().BeFalse();
    }

    [Fact]
    public async Task An_emptied_box_clears_the_field_Async()
    {
        var writer = new FakeTagWriter();
        writer.Files[Twelve[0].Path] = new TagSnapshot("First Light", AlbumArtist: "Old Artist", Year: 2019);
        var editor = new FakeTagEditor();
        TagEditorViewModel vm = ViewModel(editor, writer);
        await vm.LoadAsync([Twelve[0]]);

        vm.AlbumArtist = string.Empty;
        vm.Year = string.Empty;
        await vm.ConfirmAsync();

        TagEdit edit = editor.Applied[0].Edit;
        edit.AlbumArtist.Should().Be(string.Empty, "the empty value is how the writer is told to clear a field");
        edit.Year.Should().Be(0);
    }

    [Fact]
    public async Task A_year_that_is_not_a_number_is_refused_before_anything_is_written_Async()
    {
        var writer = new FakeTagWriter();
        writer.Files[Twelve[0].Path] = new TagSnapshot("First Light");
        var editor = new FakeTagEditor();
        TagEditorViewModel vm = ViewModel(editor, writer);
        await vm.LoadAsync([Twelve[0]]);

        vm.Year = "nineteen ninety three";
        TagEditReport? report = await vm.ConfirmAsync();

        report.Should().BeNull();
        editor.Applied.Should().BeEmpty("nothing may be written while a box holds something that is not a number");
        vm.Error.Should().Be("Year must be a number (or empty to clear it).");
    }

    [Fact]
    public async Task A_file_whose_tags_cannot_be_read_still_appears_and_falls_back_to_the_row_Async()
    {
        // FakeTagWriter returns null for a path it does not know, which is what an unreadable file looks like.
        TagEditorViewModel vm = ViewModel(new FakeTagEditor(), new FakeTagWriter());

        await vm.LoadAsync([Twelve[0]]);

        vm.Files.Should().ContainSingle();
        vm.Title.Should().Be("Track 1", "the library's idea of the track beats an empty form");
    }

    // ---- the undo bar (flow 8: "Undo available from the sidebar InfoBar for the session") ---------------------

    [Fact]
    public async Task A_finished_edit_leaves_a_sticky_bar_that_offers_undo_Async()
    {
        using var notices = new ShellNotices(source: null, scans: null);
        var editor = new FakeTagEditor();
        var report = new TagEditReport("Album artist on 12 tracks", [.. Twelve.Select(t => new TagEditFileResult(TagEditTarget.For(t), TagWriteOutcome.Written))]);

        notices.ShowTagEdit(report, async () => { await editor.UndoAsync(); });

        ShellNotice bar = notices.Items.Single(n => n.Kind == NoticeKind.TagEdit);
        bar.Title.Should().Be("Album artist on 12 tracks");
        bar.Message.Should().Be("12 files updated.");
        bar.ActionText.Should().Be("Undo");
        bar.IsSticky.Should().BeTrue("the bar is the only way to reach the undo, so a bar that timed out would take the undo with it");

        await bar.Action!();

        editor.Undos.Should().Be(1);
        notices.Items.Should().NotContain(n => n.Kind == NoticeKind.TagEdit, "the offer goes once it has been taken");
    }

    [Fact]
    public void An_edit_that_wrote_nothing_offers_no_undo()
    {
        using var notices = new ShellNotices(source: null, scans: null);
        var report = new TagEditReport("Title on 1 track", [new(TagEditTarget.For(Twelve[0]), TagWriteOutcome.Unchanged)]);

        notices.ShowTagEdit(report, () => Task.CompletedTask);

        notices.Items.Single(n => n.Kind == NoticeKind.TagEdit).HasAction.Should().BeFalse("there is nothing to put back");
    }

    // ---- fakes -----------------------------------------------------------------------------------------------

    private sealed class FakeTagWriter : ITagWriter
    {
        public Dictionary<string, TagSnapshot> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<TagSnapshot?> ReadAsync(string path, CancellationToken ct = default) =>
            Task.FromResult(Files.TryGetValue(path, out TagSnapshot? snapshot) ? snapshot : null);

        public Task<TagWriteResult> WriteAsync(string path, TagEdit edit, CancellationToken ct = default) =>
            throw new NotSupportedException("the view model writes through ITagEditor, never straight to a file");
    }

    private sealed class FakeTagEditor : ITagEditor
    {
        public List<(IReadOnlyList<TagEditTarget> Targets, TagEdit Edit)> Applied { get; } = [];

        public List<TagEditProgress> Progress { get; } = [];

        public int Undos { get; private set; }

        /// <summary>A path whose write fails, so a test can watch a batch survive one.</summary>
        public string? FailPath { get; init; }

        public bool CanUndo => Applied.Count > Undos;

        public string? UndoDescription => CanUndo ? "Album artist on 12 tracks" : null;

        public int DeferredCount => 0;

        public event EventHandler? Changed;

        public Task<TagEditReport> ApplyAsync(IReadOnlyList<TagEditTarget> targets, TagEdit edit, IProgress<TagEditProgress>? progress = null, CancellationToken ct = default)
        {
            Applied.Add((targets, edit));
            var files = new List<TagEditFileResult>(targets.Count);
            for (int i = 0; i < targets.Count; i++)
            {
                Report(progress, new TagEditProgress(i, targets.Count, targets[i].Display));
                bool failed = string.Equals(targets[i].Path, FailPath, StringComparison.OrdinalIgnoreCase);
                files.Add(new TagEditFileResult(targets[i], failed ? TagWriteOutcome.Failed : TagWriteOutcome.Written, failed ? "injected" : null));
            }

            Report(progress, new TagEditProgress(targets.Count, targets.Count, string.Empty));
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.FromResult(new TagEditReport("Album artist on " + targets.Count + " tracks", files));
        }

        private void Report(IProgress<TagEditProgress>? progress, TagEditProgress sample)
        {
            Progress.Add(sample);
            progress?.Report(sample);
        }

        public Task<TagEditReport?> UndoAsync(IProgress<TagEditProgress>? progress = null, CancellationToken ct = default)
        {
            Undos++;
            return Task.FromResult<TagEditReport?>(new TagEditReport("Album artist on 12 tracks", [], IsUndo: true));
        }

        public Task<int> FlushDeferredAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}
