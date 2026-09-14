using Tunqio.Core.Library;
using Tunqio.Library.Tags;
using Tunqio.Library.Tests.Scanning;

namespace Tunqio.Library.Tests.Tags;

/// <summary>
/// E3-S10's editor half, against a real scanner and database over a copy of the fixture library. AC-107 lives
/// here for the parts a machine can judge: twelve tracks edited, progress reported, and an undo that puts the
/// values back in the files <em>and</em> in the rows. The dialog's own behaviour is
/// <c>Tunqio.App.Tests.TagEditorViewModelTests</c>.
/// </summary>
public class TagEditorTests
{
    /// <summary>Collects progress synchronously; a <c>Progress&lt;T&gt;</c> posts through xunit's context and arrives late.</summary>
    private sealed class ProgressLog : IProgress<TagEditProgress>
    {
        public List<TagEditProgress> Samples { get; } = [];

        public void Report(TagEditProgress value) => Samples.Add(value);
    }

    /// <summary>
    /// T-113: a cover edit reaches the library through the same targeted rescan as a text edit, and that rescan
    /// runs the art stage, so the row's art hash follows the file. This is the "album art cache is told the art
    /// changed" the story asked for; nothing tells it, the scan simply does its job.
    /// </summary>
    [Fact]
    public async Task Removing_a_cover_clears_the_row_art_hash_and_undo_brings_it_back_Async()
    {
        string artRoot = Path.Combine(Path.GetTempPath(), "tunqio-art-" + Guid.NewGuid().ToString("N"));
        try
        {
            using ScanHarness h = await ScanHarness.CreateAsync(artCache: new Tunqio.Library.Art.ArtCache(artRoot));
            await h.ScanAsync();
            TrackDto track = (await h.AllTracksAsync()).First(t => t.Path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) && t.ArtHash is not null);
            var editor = new TagEditor(new TagLibTagWriter(), h.Scanner);
            var target = TagEditTarget.For(track);

            TagEditReport report = await editor.ApplyAsync([target], new TagEdit(Pictures: []));

            report.Written.Should().Be(1);
            report.Description.Should().Be("Cover art on 1 track");
            // The track's own column, not the DTO: TrackDto.ArtHash falls back to the album's art, and the album row
            // keeps the first hash it saw (T-207 is that gap). What this story promises is that the file's own art
            // hash follows the file.
            TrackArtHash(h, track.Id).Should().BeNull("the rescan's art stage found no picture in the file");

            TagEditReport? undone = await editor.UndoAsync();

            undone!.Written.Should().Be(1);
            TrackArtHash(h, track.Id).Should().Be(track.ArtHash, "the same bytes hash to the same cached image");
        }
        finally
        {
            if (Directory.Exists(artRoot))
            {
                Directory.Delete(artRoot, recursive: true);
            }
        }
    }

    private static string? TrackArtHash(ScanHarness h, long trackId)
    {
        using Microsoft.Data.Sqlite.SqliteConnection connection = h.Db.OpenConnection();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT art_hash FROM track WHERE id = $id";
        command.Parameters.AddWithValue("$id", trackId);
        return command.ExecuteScalar() as string;
    }

    [Fact]
    public async Task A_batch_of_12_reports_progress_and_undo_restores_the_files_and_the_rows_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();

        IReadOnlyList<TrackDto> all = await h.AllTracksAsync();
        // Twelve tracks that span two albums, so the edit has to move album identity for real rather than
        // rewriting the same album row twelve times.
        List<TrackDto> chosen = [.. all
            .Where(t => t.Path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) || t.Path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .Take(12)];
        chosen.Should().HaveCount(12, "the fixture library has at least twelve FLAC and MP3 files");
        List<TagEditTarget> targets = [.. chosen.Select(TagEditTarget.For)];
        Dictionary<long, string?> albumArtistBefore = chosen.ToDictionary(t => t.Id, t => t.AlbumArtist);

        var writer = new TagLibTagWriter();
        var editor = new TagEditor(writer, h.Scanner);
        var progress = new ProgressLog();

        TagEditReport report = await editor.ApplyAsync(targets, new TagEdit(AlbumArtist: "Batch Artist"), progress);

        report.Error.Should().BeNull();
        report.Files.Should().HaveCount(12);
        report.Written.Should().Be(12, "no fixture track already has this album artist");
        report.Failed.Should().Be(0);
        report.Description.Should().Be("Album artist on 12 tracks", "the notice must say which field it would undo");

        progress.Samples.Should().HaveCount(13, "one sample before each file and one when the batch is done");
        progress.Samples[0].Should().BeEquivalentTo(new { Completed = 0, Total = 12 });
        progress.Samples[^1].Should().BeEquivalentTo(new { Completed = 12, Total = 12 });

        // In the files.
        foreach (TagEditTarget target in targets)
        {
            (await writer.ReadAsync(target.Path))!.AlbumArtist.Should().Be("Batch Artist", target.Path);
        }

        // And in the database, which is the half a write alone would not give: the targeted rescan did it.
        foreach (TrackDto after in await ReloadAsync(h, chosen))
        {
            after.AlbumArtist.Should().Be("Batch Artist", "flow 8 says the library view updates, not just the files");
        }

        editor.CanUndo.Should().BeTrue();
        editor.UndoDescription.Should().Be("Album artist on 12 tracks");

        TagEditReport? undo = await editor.UndoAsync();

        undo.Should().NotBeNull();
        undo!.IsUndo.Should().BeTrue();
        undo.Written.Should().Be(12);
        undo.Description.Should().Be("Undo album artist on 12 tracks");
        editor.CanUndo.Should().BeFalse("the stack held one batch and it has been reversed");

        foreach (TagEditTarget target in targets)
        {
            TrackDto original = chosen.Single(t => t.Id == target.TrackId);
            (await writer.ReadAsync(target.Path))!.AlbumArtist.Should().Be(original.AlbumArtist, "undo puts the file back, not just the row: " + target.Path);
        }

        foreach (TrackDto after in await ReloadAsync(h, chosen))
        {
            after.AlbumArtist.Should().Be(albumArtistBefore[after.Id], "undo restores the previous values in files and database (flow 8)");
        }
    }

    [Fact]
    public async Task The_watchers_later_pass_over_an_edit_we_made_writes_nothing_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();

        List<TrackDto> chosen = [.. (await h.AllTracksAsync()).Where(t => t.Path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)).Take(4)];
        List<TagEditTarget> targets = [.. chosen.Select(TagEditTarget.For)];
        var editor = new TagEditor(new TagLibTagWriter(), h.Scanner);
        await editor.ApplyAsync(targets, new TagEdit(AlbumArtist: "Batch Artist"));

        // A tag write moves the file's size and modification time, so it fires the watcher, which two seconds
        // later scans exactly these paths. That second pass is this: the diff compares the file against the row
        // the editor's own rescan just wrote, finds them identical, and reads no tags and writes no rows. The
        // watcher's echo of our own edit is therefore harmless rather than a second round of churn.
        h.Reader.Reset();
        ScanReport echo = await h.ScanAsync(ScanRequest.Targeted(h.Folder.Id, [.. targets.Select(t => t.Path)]));

        echo.Outcome.Should().Be(ScanOutcome.Completed);
        echo.Updated.Should().Be(0, "the editor already brought the rows up to date, so the watcher's pass has nothing to write");
        echo.Added.Should().Be(0);
        echo.Failed.Should().Be(0);
        // A targeted scan walks each named file's directory so the compilation rule still sees the folder, so it
        // sees the four edited files' siblings too; every one of them, edited or not, comes back unchanged.
        echo.Seen.Should().BeGreaterThanOrEqualTo(4);
        echo.Unchanged.Should().Be(echo.Seen);
        h.Reader.Reads.Should().Be(0, "an unchanged file is not re-read");
    }

    [Fact]
    public async Task An_undo_restores_a_field_that_was_absent_by_clearing_it_again_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();

        // A track whose comment tag is empty: setting it and undoing must leave it empty, not leave a blank
        // comment behind. This is the case TagEdit's null-means-unchanged shape cannot express on its own.
        var writer = new TagLibTagWriter();
        TrackDto track = (await h.AllTracksAsync())
            .First(t => t.Path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase) && t.Comment is null);
        var editor = new TagEditor(writer, h.Scanner);
        var target = TagEditTarget.For(track);

        await editor.ApplyAsync([target], new TagEdit(Comment: "Added by the test"));
        (await writer.ReadAsync(target.Path))!.Comment.Should().Be("Added by the test");

        await editor.UndoAsync();

        (await writer.ReadAsync(target.Path))!.Comment.Should().BeNull("a field that was absent must be absent again, not blank");
    }

    [Fact]
    public async Task A_file_that_fails_does_not_stop_the_batch_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();

        List<TrackDto> chosen = [.. (await h.AllTracksAsync()).Where(t => t.Path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)).Take(3)];
        var targets = new List<TagEditTarget>(chosen.Select(TagEditTarget.For))
        {
            // A file the library believes in and the disk does not, which is what a track deleted under the
            // dialog looks like.
            new(9999, chosen[0].FolderId, Path.Combine(h.Root, "deleted-under-us.flac"), "Gone"),
        };

        var editor = new TagEditor(new TagLibTagWriter(), h.Scanner);
        TagEditReport report = await editor.ApplyAsync(targets, new TagEdit(Genres: ["Test Genre"]));

        report.Written.Should().Be(3, "the three real files must still be written");
        report.Failed.Should().Be(1);
        report.Files.Single(f => f.Outcome == TagWriteOutcome.Failed).Error.Should().Be("file not found");
        report.Summary().Should().Be("3 files updated, 1 failed.");
    }

    [Fact]
    public async Task A_write_to_the_playing_file_waits_until_playback_moves_on_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();

        List<TrackDto> chosen = [.. (await h.AllTracksAsync()).Where(t => t.Path.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)).Take(2)];
        List<TagEditTarget> targets = [.. chosen.Select(TagEditTarget.For)];
        string playing = targets[0].Path;

        var writer = new TagLibTagWriter();
        bool stillPlaying = true;
        var editor = new TagEditor(writer, h.Scanner, isPlaying: path => stillPlaying && string.Equals(path, playing, StringComparison.OrdinalIgnoreCase));

        TagEditReport report = await editor.ApplyAsync(targets, new TagEdit(AlbumTitle: "Deferred Album"));

        report.Deferred.Should().Be(1, "the engine holds the playing file open, so replacing it now would fail on a sharing violation");
        report.Written.Should().Be(1);
        editor.DeferredCount.Should().Be(1);
        (await writer.ReadAsync(playing))!.AlbumTitle.Should().NotBe("Deferred Album", "the deferred file has not been touched yet");
        report.Summary().Should().Be("1 file updated, 1 waiting for playback to move on.");

        // Playback moves on; the shell calls FlushDeferredAsync from its track-changed handler.
        stillPlaying = false;
        int flushed = await editor.FlushDeferredAsync();

        flushed.Should().Be(1);
        editor.DeferredCount.Should().Be(0);
        (await writer.ReadAsync(playing))!.AlbumTitle.Should().Be("Deferred Album");
        (await h.TrackAtAsync(playing)).AlbumTitle.Should().Be("Deferred Album", "the deferred write refreshes the library too");
    }

    private static async Task<IReadOnlyList<TrackDto>> ReloadAsync(ScanHarness h, IEnumerable<TrackDto> tracks) =>
        await h.Service.Tracks.GetByIdsAsync([.. tracks.Select(t => t.Id)]);
}
