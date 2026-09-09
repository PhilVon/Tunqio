using System.Diagnostics;
using Tunqio.Core.Library;
using Tunqio.FixtureGen;
using Tunqio.Library.Scanning;
using Tunqio.Library.Tags;

namespace Tunqio.Library.Tests.Scanning;

/// <summary>
/// E3-S5: the pipeline over a copy of the fixture library. Cancellation keeps every batch whole or absent
/// (AC-92), removed files are marked missing, hidden and come back when restored (AC-93), an unchanged
/// rescan reads no tags and writes no rows (AC-184), and the report lists per-file failures (AC-185). The
/// timing gate (AC-91) is <see cref="ScanPerformanceTests"/>.
/// </summary>
public class LibraryScannerTests
{
    private static FixtureFileEntry CorruptEntry(FixtureManifest manifest) => manifest.Files.Single(f => f.CorruptTags);

    /// <summary>Entries of the album with no album-artist tag and three or more first artists (the compilation).</summary>
    private static List<FixtureFileEntry> CompilationEntries(FixtureManifest manifest) => manifest.Files
        .Where(f => f.AlbumArtist is null && !f.CorruptTags)
        .GroupBy(f => f.AlbumTitle)
        .Single(g => g.Select(f => f.Artists[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= CompilationRule.MinimumDistinctArtists)
        .ToList();

    /// <summary>A tagged file whose folder has an album-artist tag, so a change to it never drags its siblings back through the reader.</summary>
    private static FixtureFileEntry PlainEntry(FixtureManifest manifest) => manifest.Files.First(f => f.AlbumArtist is not null && !f.CorruptTags && f.Format == "flac");

    [Fact]
    public async Task First_scan_imports_every_fixture_file_and_reports_the_corrupt_one()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        FixtureManifest manifest = ScanHarness.Manifest();

        Stopwatch elapsed = Stopwatch.StartNew();
        ScanReport report = await h.ScanAsync();
        elapsed.Stop();

        report.Outcome.Should().Be(ScanOutcome.Completed);
        report.Error.Should().BeNull();
        report.Seen.Should().Be(manifest.Files.Count);
        report.Processed.Should().Be(manifest.Files.Count);
        report.Added.Should().Be(manifest.Files.Count);
        report.Updated.Should().Be(0);
        report.Unchanged.Should().Be(0);
        report.Missing.Should().Be(0);
        report.Failed.Should().Be(1);
        report.Failures.Should().ContainSingle();
        report.Failures[0].Path.Should().Be(h.PathOf(CorruptEntry(manifest)));
        report.Failures[0].Outcome.Should().Be(TagReadOutcome.CorruptTags);
        report.Failures[0].Error.Should().NotBeNullOrEmpty();
        report.Folders.Should().ContainSingle().Which.Should().BeEquivalentTo(new { FolderId = h.Folder.Id, Offline = false, Added = manifest.Files.Count, Failed = 1 });
        h.Reader.Reads.Should().Be(manifest.Files.Count);
        h.Tracks.Batches.Should().Be(1, "60 files fit one batch");

        IReadOnlyList<TrackDto> tracks = await h.AllTracksAsync();
        tracks.Should().HaveCount(manifest.Files.Count);
        foreach (FixtureFileEntry entry in manifest.Files.Where(f => !f.CorruptTags))
        {
            TrackDto track = await h.TrackAtAsync(h.PathOf(entry));
            track.Title.Should().Be(entry.Title, entry.RelativePath);
            track.AlbumTitle.Should().Be(entry.AlbumTitle, entry.RelativePath);
            track.Codec.Should().Be(FixtureFormats.Codec(entry.Format), entry.RelativePath);
            track.Missing.Should().BeFalse();
        }

        foreach (FixtureFileEntry entry in CompilationEntries(manifest))
        {
            (await h.TrackAtAsync(h.PathOf(entry))).AlbumArtist.Should().Be(CompilationRule.VariousArtists, "the folder rule ran on the reader's output");
        }

        (await h.TrackAtAsync(h.PathOf(CorruptEntry(manifest)))).Title.Should().Be(FileNameMetadata.FromPath(h.PathOf(CorruptEntry(manifest))).Title);

        LibraryFolderDto folder = await h.FolderRowAsync();
        folder.LastScanAt.Should().Be(ScanHarness.Now);
        folder.LastScanStatus.Should().Be("ok, 1 file(s) with unreadable tags");

        // Progress: throttled, monotonic, final.
        h.Progress.Samples.Should().NotBeEmpty();
        h.Progress.Last.Should().BeEquivalentTo(new { Phase = ScanPhase.Finished, Seen = manifest.Files.Count, Processed = manifest.Files.Count, Added = manifest.Files.Count, Failed = 1, CurrentPath = (string?)null });
        h.Progress.Samples.Select(s => s.Seen).Should().BeInAscendingOrder();
        h.Progress.Samples.Count.Should().BeLessThanOrEqualTo((int)(elapsed.Elapsed / LibraryScanner.ProgressInterval) + 3, "at most four reports a second plus the first and the final one");
    }

    [Fact]
    public async Task Second_scan_with_no_changes_reads_nothing_and_writes_nothing()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        IReadOnlyList<TrackFileStamp> before = await h.Service.Tracks.SnapshotAsync(h.Folder.Id);
        h.Reader.Reset();

        ScanReport report = await h.ScanAsync();

        report.Outcome.Should().Be(ScanOutcome.Completed);
        report.Seen.Should().Be(before.Count);
        report.Unchanged.Should().Be(before.Count);
        report.Processed.Should().Be(0);
        report.Added.Should().Be(0);
        report.Updated.Should().Be(0);
        report.Failed.Should().Be(0);
        report.Failures.Should().BeEmpty();
        h.Reader.Reads.Should().Be(0, "every stamp matched the snapshot");
        h.Tracks.Batches.Should().Be(1, "no batch was written by the second scan");
        (await h.Service.Tracks.SnapshotAsync(h.Folder.Id)).Should().BeEquivalentTo(before);
        (await h.FolderRowAsync()).LastScanStatus.Should().Be("ok");
    }

    [Fact]
    public async Task A_changed_file_is_read_again_and_keeps_its_id()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        FixtureFileEntry entry = PlainEntry(ScanHarness.Manifest());
        string path = h.PathOf(entry);
        TrackDto before = await h.TrackAtAsync(path);
        h.Reader.Reset();

        ScanHarness.Retitle(path, "Retitled");
        ScanReport report = await h.ScanAsync();

        report.Processed.Should().Be(1);
        report.Updated.Should().Be(1);
        report.Added.Should().Be(0);
        report.Unchanged.Should().Be(before.Id > 0 ? 59 : 0);
        h.Reader.Reads.Should().Be(1);
        h.Reader.Paths.Should().ContainSingle().Which.Should().Be(path);
        TrackDto after = await h.TrackAtAsync(path);
        after.Id.Should().Be(before.Id);
        after.Title.Should().Be("Retitled");
        after.AddedAt.Should().Be(before.AddedAt);
        after.FileMtime.Should().BeGreaterThan(before.FileMtime);
    }

    [Fact]
    public async Task Files_removed_from_disk_are_marked_missing_hidden_from_views_and_reappear_when_restored()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        FixtureManifest manifest = ScanHarness.Manifest();
        List<FixtureFileEntry> away = manifest.Files.Where(f => f.AlbumArtist is not null && !f.CorruptTags).Take(2).ToList();
        string parked = Path.Combine(Path.GetTempPath(), "tunqio-parked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parked);
        try
        {
            foreach (FixtureFileEntry entry in away)
            {
                File.Move(h.PathOf(entry), Path.Combine(parked, Path.GetFileName(entry.RelativePath)));
            }

            h.Reader.Reset();
            ScanReport gone = await h.ScanAsync();
            gone.Missing.Should().Be(2);
            gone.Restored.Should().Be(0);
            gone.Seen.Should().Be(manifest.Files.Count - 2);
            h.Reader.Reads.Should().Be(0);
            (await h.AllTracksAsync()).Should().HaveCount(manifest.Files.Count - 2, "missing tracks are hidden from views");
            (await h.AllTracksAsync(includeMissing: true)).Should().HaveCount(manifest.Files.Count, "and retained");
            foreach (FixtureFileEntry entry in away)
            {
                (await h.TrackAtAsync(h.PathOf(entry))).Missing.Should().BeTrue();
            }

            // Still missing on the next scan: not counted again.
            (await h.ScanAsync()).Missing.Should().Be(0);

            foreach (FixtureFileEntry entry in away)
            {
                File.Move(Path.Combine(parked, Path.GetFileName(entry.RelativePath)), h.PathOf(entry));
            }

            ScanReport back = await h.ScanAsync();
            back.Restored.Should().Be(2);
            back.Missing.Should().Be(0);
            back.Processed.Should().Be(0, "the files came back unchanged, so their tags were not read again");
            h.Reader.Reads.Should().Be(0);
            (await h.AllTracksAsync()).Should().HaveCount(manifest.Files.Count);
            foreach (FixtureFileEntry entry in away)
            {
                (await h.TrackAtAsync(h.PathOf(entry))).Missing.Should().BeFalse();
            }
        }
        finally
        {
            Directory.Delete(parked, recursive: true);
        }
    }

    [Fact]
    public async Task A_file_that_changed_while_it_was_away_is_read_again_and_no_longer_missing()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        string path = h.PathOf(PlainEntry(ScanHarness.Manifest()));
        string parkedDirectory = Path.Combine(Path.GetTempPath(), "tunqio-parked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parkedDirectory);
        string parked = Path.Combine(parkedDirectory, Path.GetFileName(path)); // same extension, so the tag writer still knows the format
        ScanReport report;
        try
        {
            File.Move(path, parked);
            (await h.ScanAsync()).Missing.Should().Be(1);
            ScanHarness.Retitle(parked, "Changed while away");
            File.Move(parked, path);

            report = await h.ScanAsync();
        }
        finally
        {
            Directory.Delete(parkedDirectory, recursive: true);
        }

        report.Updated.Should().Be(1);
        report.Restored.Should().Be(0, "a changed file goes through the reader and the upsert clears the flag");
        TrackDto track = await h.TrackAtAsync(path);
        track.Missing.Should().BeFalse();
        track.Title.Should().Be("Changed while away");
    }

    [Fact]
    public async Task Cancelling_mid_scan_leaves_every_batch_fully_applied_or_not_at_all()
    {
        using ScanHarness h = await ScanHarness.CreateAsync(copyFixtures: false);
        const int Files = 1_200;
        ScanTreeBuilder.Build(h.Root, Files);
        using var cts = new CancellationTokenSource();
        h.Tracks.BeforeBatch = (ordinal, _) =>
        {
            if (ordinal == 2)
            {
                cts.Cancel(); // the second transaction sees the cancelled token part-way and rolls back
            }
        };

        ScanReport report = await h.ScanAsync(ct: cts.Token);

        report.Outcome.Should().Be(ScanOutcome.Cancelled);
        report.Added.Should().Be(LibraryScanner.BatchSize, "only the batch that committed is counted");
        report.Folders.Should().BeEmpty("the folder did not run to its end");
        h.Tracks.Batches.Should().Be(2);
        (await h.FolderRowAsync()).LastScanStatus.Should().Be("cancelled");

        // Consistency: the first batch is entirely there with its links and its search rows; nothing of the second.
        h.Scalar("SELECT count(*) FROM track").Should().Be(LibraryScanner.BatchSize);
        h.Scalar("SELECT count(*) FROM track WHERE album_id IS NULL").Should().Be(0);
        h.Scalar("SELECT count(*) FROM track_artist WHERE role = 'artist'").Should().Be(LibraryScanner.BatchSize);
        h.Scalar("SELECT count(*) FROM track_genre").Should().Be(LibraryScanner.BatchSize);
        h.Scalar("SELECT count(*) FROM track_fts").Should().Be(LibraryScanner.BatchSize);
        h.Scalar("SELECT count(*) FROM album").Should().Be(LibraryScanner.BatchSize / ScanTreeBuilder.TracksPerAlbum);

        // The next scan finishes the job without re-reading what committed.
        h.Tracks.BeforeBatch = null;
        h.Reader.Reset();
        ScanReport rest = await h.ScanAsync();
        rest.Outcome.Should().Be(ScanOutcome.Completed);
        rest.Unchanged.Should().Be(LibraryScanner.BatchSize);
        rest.Added.Should().Be(Files - LibraryScanner.BatchSize);
        h.Reader.Reads.Should().Be(Files - LibraryScanner.BatchSize);
        h.Scalar("SELECT count(*) FROM track").Should().Be(Files);
        h.Scalar("SELECT count(*) FROM track_fts").Should().Be(Files);
    }

    [Fact]
    public async Task Cancellation_before_the_first_batch_writes_nothing()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        using var cts = new CancellationTokenSource();
        h.Reader.BeforeRead = async (_, _) => await cts.CancelAsync();

        ScanReport report = await h.ScanAsync(ct: cts.Token);

        report.Outcome.Should().Be(ScanOutcome.Cancelled);
        report.Added.Should().Be(0);
        h.Tracks.Batches.Should().Be(0);
        h.Scalar("SELECT count(*) FROM track").Should().Be(0);
        h.Scanner.IsScanning.Should().BeFalse();
        h.Progress.Last.Phase.Should().Be(ScanPhase.Finished);
    }

    [Fact]
    public async Task An_offline_folder_marks_its_tracks_missing_without_purging_them()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        int count = (await h.AllTracksAsync()).Count;
        string away = h.Root + "-away";
        Directory.Move(h.Root, away);
        try
        {
            h.Reader.Reset();
            ScanReport offline = await h.ScanAsync();
            offline.Outcome.Should().Be(ScanOutcome.Completed);
            offline.Folders.Should().ContainSingle().Which.Offline.Should().BeTrue();
            offline.Missing.Should().Be(count);
            offline.Seen.Should().Be(0);
            h.Reader.Reads.Should().Be(0);
            (await h.FolderRowAsync()).LastScanStatus.Should().Be(LibraryScanner.StatusOffline);
            (await h.AllTracksAsync()).Should().BeEmpty();
            (await h.AllTracksAsync(includeMissing: true)).Should().HaveCount(count);
        }
        finally
        {
            Directory.Move(away, h.Root);
        }

        ScanReport back = await h.ScanAsync();
        back.Restored.Should().Be(count);
        back.Processed.Should().Be(0);
        (await h.AllTracksAsync()).Should().HaveCount(count);
    }

    [Fact]
    public async Task ForceReread_reads_every_file_again()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        IReadOnlyList<TrackDto> before = await h.AllTracksAsync();
        h.Reader.Reset();

        ScanReport report = await h.ScanAsync(new ScanRequest(ForceReread: true));

        report.Processed.Should().Be(before.Count);
        report.Updated.Should().Be(before.Count);
        report.Added.Should().Be(0);
        report.Unchanged.Should().Be(0);
        h.Reader.Reads.Should().Be(before.Count);
        (await h.AllTracksAsync()).Select(t => (t.Id, t.AddedAt)).Should().BeEquivalentTo(before.Select(t => (t.Id, t.AddedAt)), "a re-upsert keeps ids and added_at");
    }

    [Fact]
    public async Task Only_one_scan_runs_at_a_time()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Reader.BeforeRead = async (_, ct) =>
        {
            started.TrySetResult();
            await gate.Task.WaitAsync(ct);
        };

        Task<ScanReport> first = h.ScanAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        h.Scanner.IsScanning.Should().BeTrue();
        Func<Task> second = () => h.ScanAsync();
        await second.Should().ThrowAsync<InvalidOperationException>();

        gate.SetResult();
        ScanReport report = await first;
        report.Outcome.Should().Be(ScanOutcome.Completed);
        h.Scanner.IsScanning.Should().BeFalse();
    }

    [Fact]
    public async Task Disabled_and_unselected_folders_are_skipped()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        string other = Path.Combine(h.Root, "..", Path.GetFileName(h.Root) + "-other");
        Directory.CreateDirectory(other);
        try
        {
            LibraryFolderDto second = await h.Service.Folders.AddAsync(other);

            ScanReport only = await h.ScanAsync(ScanRequest.Folder(second.Id));
            only.Folders.Should().ContainSingle().Which.FolderId.Should().Be(second.Id);
            only.Seen.Should().Be(0);
            h.Reader.Reads.Should().Be(0);

            await h.Service.Folders.SetEnabledAsync(h.Folder.Id, false);
            ScanReport all = await h.ScanAsync();
            all.Folders.Select(f => f.FolderId).Should().Equal(second.Id);
            (await h.ScanAsync(ScanRequest.Folder(h.Folder.Id))).Folders.Should().BeEmpty("a disabled folder is skipped even when named");
            h.Reader.Reads.Should().Be(0);
            (await h.FolderRowAsync()).LastScanAt.Should().BeNull();
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task Art_cache_and_duration_probe_fill_what_the_tag_reader_could_not()
    {
        var art = new FakeArtCache();
        var probe = new FakeProbe(1234);
        using ScanHarness h = await ScanHarness.CreateAsync(artCache: art, durationProbe: probe);
        FixtureManifest manifest = ScanHarness.Manifest();

        ScanReport report = await h.ScanAsync();

        report.SlowPath.Should().Be(1, "only the corrupt file came back without a duration");
        probe.Paths.Should().ContainSingle().Which.Should().Be(h.PathOf(CorruptEntry(manifest)));
        (await h.TrackAtAsync(h.PathOf(CorruptEntry(manifest)))).DurationMs.Should().Be(1234);
        art.Calls.Should().Be(manifest.Files.Count);
        art.WithPicture.Should().Be(manifest.Files.Count(f => f.EmbeddedArt && !f.CorruptTags));
        foreach (TrackDto track in await h.AllTracksAsync())
        {
            track.ArtHash.Should().Be("track:" + Path.GetFileName(track.Path));
        }
    }

    [Fact]
    public async Task A_changed_file_in_an_untagged_compilation_folder_keeps_Various_Artists()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        List<FixtureFileEntry> compilation = CompilationEntries(ScanHarness.Manifest());
        string path = h.PathOf(compilation[0]);
        h.Reader.Reset();

        ScanHarness.Retitle(path, "Retitled compilation track");
        ScanReport report = await h.ScanAsync();

        report.Processed.Should().Be(1);
        report.Updated.Should().Be(1);
        report.Unchanged.Should().Be(59, "the siblings read again for the folder rule stay unchanged in the report");
        h.Reader.Reads.Should().Be(compilation.Count, "the changed file plus its siblings, so the rule sees the whole folder");
        TrackDto track = await h.TrackAtAsync(path);
        track.Title.Should().Be("Retitled compilation track");
        track.AlbumArtist.Should().Be(CompilationRule.VariousArtists);
        foreach (FixtureFileEntry entry in compilation)
        {
            (await h.TrackAtAsync(h.PathOf(entry))).AlbumId.Should().Be(track.AlbumId, "the folder stays one album");
        }
    }

    [Fact]
    public async Task A_folder_with_no_supported_files_completes_with_nothing_to_do()
    {
        using ScanHarness h = await ScanHarness.CreateAsync(copyFixtures: false);
        File.WriteAllText(Path.Combine(h.Root, "notes.txt"), "not audio");
        Directory.CreateDirectory(Path.Combine(h.Root, "empty"));

        ScanReport report = await h.ScanAsync();

        report.Outcome.Should().Be(ScanOutcome.Completed);
        report.Seen.Should().Be(0);
        h.Tracks.Batches.Should().Be(0);
        (await h.FolderRowAsync()).LastScanStatus.Should().Be("ok");
    }

    private sealed class FakeArtCache : IArtCache
    {
        private int _calls;
        private int _withPicture;

        public int Calls => _calls;

        public int WithPicture => _withPicture;

        public Task<ArtHashes> StoreAsync(EmbeddedPicture? picture, string audioPath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            if (picture is not null)
            {
                Interlocked.Increment(ref _withPicture);
            }

            return Task.FromResult(new ArtHashes("track:" + Path.GetFileName(audioPath), "album:" + Path.GetFileName(Path.GetDirectoryName(audioPath)!)));
        }
    }

    private sealed class FakeProbe(int durationMs) : IDurationProbe
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Paths { get; } = new();

        public Task<int?> ProbeAsync(string path, CancellationToken ct = default)
        {
            Paths.Enqueue(path);
            return Task.FromResult<int?>(durationMs);
        }
    }
}
