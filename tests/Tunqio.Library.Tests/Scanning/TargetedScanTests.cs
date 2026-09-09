using Tunqio.Core.Library;
using Tunqio.FixtureGen;
using Tunqio.Library.Scanning;
using Tunqio.Library.Tags;

namespace Tunqio.Library.Tests.Scanning;

/// <summary>
/// E3-S6: the scanner's targeted mode (<see cref="ScanRequest.Targeted"/>), which the watcher drives. A file
/// path is diffed with its directory, a directory is walked, a gone path is marked missing (a subtree of rows
/// when it was a directory), missing marking never reaches outside the scopes, and the folder's last-scan
/// record is left alone.
/// </summary>
public class TargetedScanTests
{
    /// <summary>A tagged file whose folder has an album-artist tag, so a change to it never drags its siblings through the reader.</summary>
    private static FixtureFileEntry PlainEntry(FixtureManifest manifest) => manifest.Files.First(f => f.AlbumArtist is not null && !f.CorruptTags && f.Format == "flac");

    /// <summary>A tagged file in a different directory from <see cref="PlainEntry"/>.</summary>
    private static FixtureFileEntry ElsewhereEntry(FixtureManifest manifest)
    {
        string plain = Path.GetDirectoryName(PlainEntry(manifest).RelativePath)!;
        return manifest.Files.First(f => f.AlbumArtist is not null && !f.CorruptTags && Path.GetDirectoryName(f.RelativePath) != plain);
    }

    private static List<FixtureFileEntry> CompilationEntries(FixtureManifest manifest) => manifest.Files
        .Where(f => f.AlbumArtist is null && !f.CorruptTags)
        .GroupBy(f => f.AlbumTitle)
        .Single(g => g.Select(f => f.Artists[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= CompilationRule.MinimumDistinctArtists)
        .ToList();

    private static int FilesIn(string directory) => Directory.EnumerateFiles(directory).Count(f => AudioFormats.IsSupported(f));

    [Fact]
    public async Task A_file_path_adds_the_file_reads_nothing_else_and_leaves_the_folder_record_alone()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        FixtureManifest manifest = ScanHarness.Manifest();
        string source = h.PathOf(PlainEntry(manifest));
        string directory = Path.GetDirectoryName(source)!;
        string added = Path.Combine(directory, "99 Added.flac");
        File.Copy(source, added);
        int siblings = FilesIn(directory) - 1;
        h.Reader.Reset();
        var completed = new List<ScanReport>();
        h.Scanner.ScanCompleted += (_, r) => completed.Add(r);

        ScanReport report = await h.ScanAsync(ScanRequest.Targeted(h.Folder.Id, [added]));

        report.Outcome.Should().Be(ScanOutcome.Completed);
        report.Added.Should().Be(1);
        report.Processed.Should().Be(1);
        report.Seen.Should().Be(siblings + 1, "the file's directory is listed, nothing else");
        report.Unchanged.Should().Be(siblings);
        report.Missing.Should().Be(0);
        report.Folders.Should().ContainSingle().Which.FolderId.Should().Be(h.Folder.Id);
        h.Reader.Reads.Should().Be(1);
        h.Reader.Paths.Should().ContainSingle().Which.Should().Be(added);
        (await h.TrackAtAsync(added)).AlbumTitle.Should().Be(PlainEntry(manifest).AlbumTitle);
        (await h.AllTracksAsync()).Should().HaveCount(manifest.Files.Count + 1);
        (await h.FolderRowAsync()).LastScanStatus.Should().Be("ok, 1 file(s) with unreadable tags", "a targeted scan does not record itself on the folder");
        completed.Should().ContainSingle().Which.Should().BeSameAs(report);
    }

    [Fact]
    public async Task Missing_marking_stays_inside_the_scope()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        FixtureManifest manifest = ScanHarness.Manifest();
        string inScope = h.PathOf(PlainEntry(manifest));
        string outside = h.PathOf(ElsewhereEntry(manifest));
        File.Delete(inScope);
        File.Delete(outside);
        h.Reader.Reset();

        ScanReport targeted = await h.ScanAsync(ScanRequest.Targeted(h.Folder.Id, [inScope]));

        targeted.Missing.Should().Be(1, "only the directory of the named file was diffed");
        h.Reader.Reads.Should().Be(0);
        (await h.TrackAtAsync(inScope)).Missing.Should().BeTrue();
        (await h.TrackAtAsync(outside)).Missing.Should().BeFalse("its directory was not in scope");

        ScanReport full = await h.ScanAsync();
        full.Missing.Should().Be(1);
        (await h.TrackAtAsync(outside)).Missing.Should().BeTrue();
    }

    [Fact]
    public async Task A_rename_is_the_old_path_marked_missing_and_the_new_path_added()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        string oldPath = h.PathOf(PlainEntry(ScanHarness.Manifest()));
        string newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, "Renamed" + Path.GetExtension(oldPath));
        File.Move(oldPath, newPath);
        h.Reader.Reset();

        ScanReport report = await h.ScanAsync(ScanRequest.Targeted(h.Folder.Id, [oldPath, newPath]));

        report.Missing.Should().Be(1);
        report.Added.Should().Be(1);
        report.Updated.Should().Be(0);
        h.Reader.Reads.Should().Be(1);
        (await h.TrackAtAsync(oldPath)).Missing.Should().BeTrue();
        (await h.TrackAtAsync(newPath)).Missing.Should().BeFalse();
    }

    [Fact]
    public async Task A_removed_directory_marks_every_track_under_it_missing_and_a_new_one_is_walked()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        FixtureManifest manifest = ScanHarness.Manifest();
        string directory = Path.GetDirectoryName(h.PathOf(PlainEntry(manifest)))!;
        int count = FilesIn(directory);
        string copy = directory + " (copy)";
        ScanHarness.CopyDirectory(directory, copy);
        Directory.Delete(directory, recursive: true);
        h.Reader.Reset();

        ScanReport report = await h.ScanAsync(ScanRequest.Targeted(h.Folder.Id, [directory, copy]));

        report.Missing.Should().Be(count, "the snapshot knew the removed path as a directory");
        report.Added.Should().Be(count, "the new directory was walked");
        h.Reader.Reads.Should().Be(count);
        (await h.AllTracksAsync()).Should().HaveCount(manifest.Files.Count);
        (await h.AllTracksAsync(includeMissing: true)).Should().HaveCount(manifest.Files.Count + count);
        (await h.AllTracksAsync()).Count(t => t.Path.StartsWith(copy + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).Should().Be(count);
    }

    [Fact]
    public async Task A_changed_file_in_an_untagged_compilation_folder_still_gets_Various_Artists()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        List<FixtureFileEntry> compilation = CompilationEntries(ScanHarness.Manifest());
        string path = h.PathOf(compilation[0]);
        ScanHarness.Retitle(path, "Retitled by the watcher");
        h.Reader.Reset();

        ScanReport report = await h.ScanAsync(ScanRequest.Targeted(h.Folder.Id, [path]));

        report.Updated.Should().Be(1);
        h.Reader.Reads.Should().Be(compilation.Count, "the directory listing gives the rule its siblings");
        TrackDto track = await h.TrackAtAsync(path);
        track.Title.Should().Be("Retitled by the watcher");
        track.AlbumArtist.Should().Be(CompilationRule.VariousArtists);
    }

    [Fact]
    public async Task Paths_outside_the_folder_are_ignored_and_a_targeted_request_names_exactly_one_folder()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        h.Reader.Reset();

        ScanReport report = await h.ScanAsync(ScanRequest.Targeted(h.Folder.Id, [Path.Combine(Path.GetTempPath(), "elsewhere.flac"), h.Root + "-not-this"]));

        report.Seen.Should().Be(0);
        report.Missing.Should().Be(0);
        h.Reader.Reads.Should().Be(0);

        Func<Task> noFolder = () => h.ScanAsync(new ScanRequest(Paths: [h.Root]));
        await noFolder.Should().ThrowAsync<ArgumentException>();
        Func<Task> twoFolders = () => h.ScanAsync(new ScanRequest([h.Folder.Id, h.Folder.Id + 1], Paths: [h.Root]));
        await twoFolders.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task The_folder_root_as_a_path_is_a_whole_folder_walk()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        FixtureManifest manifest = ScanHarness.Manifest();

        ScanReport report = await h.ScanAsync(ScanRequest.Targeted(h.Folder.Id, [h.Root]));

        report.Added.Should().Be(manifest.Files.Count);
        (await h.FolderRowAsync()).LastScanAt.Should().BeNull("still not recorded: only a full request writes the folder record");
    }
}
