using Tunqio.Core.Library;
using Tunqio.FixtureGen;
using Tunqio.Library.Scanning;

namespace Tunqio.Library.Tests.Scanning;

/// <summary>
/// E3-S6: the watcher's own rules over a fake file-system watch: one watch per enabled folder, debounce and
/// coalescing per path, rename as old plus new, the filter on what is taken, overflow and collapse into a
/// folder rescan, waiting while another scan runs, and surviving a failed scan. The real
/// <see cref="FileSystemWatcher"/> and the two acceptance criteria are <see cref="LibraryWatcherIntegrationTests"/>.
/// </summary>
public class LibraryWatcherTests
{
    private static FixtureFileEntry PlainEntry(FixtureManifest manifest) => manifest.Files.First(f => f.AlbumArtist is not null && !f.CorruptTags && f.Format == "flac");

    /// <summary>A copy of the plain fixture file under a new name in its own directory.</summary>
    private static string AddCopy(ScanHarness h, string name)
    {
        string source = h.PathOf(PlainEntry(ScanHarness.Manifest()));
        string added = Path.Combine(Path.GetDirectoryName(source)!, name + Path.GetExtension(source));
        File.Copy(source, added);
        return added;
    }

    [Fact]
    public async Task Start_watches_every_enabled_folder_and_refresh_follows_the_folder_list_Async()
    {
        using WatchHarness w = await WatchHarness.CreateAsync(initialScan: false);
        string other = w.Scan.Root + "-other";
        Directory.CreateDirectory(other);
        try
        {
            LibraryFolderDto second = await w.Scan.Service.Folders.AddAsync(other);
            await w.Scan.Service.Folders.SetEnabledAsync(second.Id, false);
            w.Watcher.IsWatching.Should().BeFalse();

            await w.Watcher.StartAsync();

            w.Watcher.IsWatching.Should().BeTrue();
            w.Source.Live.Select(x => x.Root).Should().ContainSingle("only the enabled folder is watched").Which.Should().Be(w.Scan.Folder.Path);
            w.Source.Single.BufferSize.Should().Be(LibraryWatcherOptions.Default.BufferSize);
            w.Watcher.Stats.Folders.Should().Be(1);

            await w.Scan.Service.Folders.SetEnabledAsync(second.Id, true);
            await w.Watcher.RefreshAsync();
            w.Source.Live.Select(x => x.Root).Should().BeEquivalentTo([w.Scan.Folder.Path, second.Path]);

            await w.Scan.Service.Folders.RemoveAsync(w.Scan.Folder.Id);
            await w.Watcher.RefreshAsync();
            w.Source.Live.Select(x => x.Root).Should().ContainSingle().Which.Should().Be(second.Path);
            w.Source.Watches[0].Disposed.Should().BeTrue("the removed folder's watch was dropped");

            await w.Watcher.StartAsync();
            w.Source.Watches.Should().HaveCount(2, "a second start is a refresh, not a second watch");

            await w.Watcher.StopAsync();
            w.Watcher.IsWatching.Should().BeFalse();
            w.Source.Live.Should().BeEmpty();
            w.Watcher.Stats.Folders.Should().Be(0);
        }
        finally
        {
            Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task A_folder_whose_root_is_away_gets_no_watch_until_it_is_back_Async()
    {
        using WatchHarness w = await WatchHarness.CreateAsync(initialScan: false);
        string away = w.Scan.Root + "-away";
        Directory.Move(w.Scan.Root, away);
        try
        {
            await w.Watcher.StartAsync();
            w.Source.Live.Should().BeEmpty();
            w.Watcher.IsWatching.Should().BeTrue();
        }
        finally
        {
            Directory.Move(away, w.Scan.Root);
        }

        await w.Watcher.RefreshAsync();
        w.Source.Live.Should().ContainSingle();
    }

    [Fact]
    public async Task Events_on_one_path_coalesce_into_one_targeted_scan_after_the_debounce_Async()
    {
        using WatchHarness w = await WatchHarness.CreateAsync();
        await w.Watcher.StartAsync();
        string added = AddCopy(w.Scan, "Added by the watcher");
        int before = (await w.Scan.AllTracksAsync()).Count;

        w.Source.Raise(FolderChangeKind.Created, added);
        w.Source.Raise(FolderChangeKind.Changed, added);
        w.Source.Raise(FolderChangeKind.Changed, added);
        w.Watcher.Stats.Pending.Should().Be(1, "three events on one path are one pending entry");
        await w.WaitForScansAsync(1);
        await Task.Delay(WatchHarness.Debounce * 3);

        w.Scanner.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(new { FolderIds = new[] { w.Scan.Folder.Id }, Paths = new[] { added }, ForceReread = false });
        w.Scanner.Reports.Single().Added.Should().Be(1);
        (await w.Scan.AllTracksAsync()).Should().HaveCount(before + 1);
        w.Watcher.Stats.Should().BeEquivalentTo(new { Events = 3L, Ignored = 0L, Scans = 1L, Pending = 0, Overflows = 0L, Failures = 0L });
    }

    [Fact]
    public async Task A_rename_scans_the_old_path_and_the_new_one_Async()
    {
        using WatchHarness w = await WatchHarness.CreateAsync();
        await w.Watcher.StartAsync();
        string oldPath = w.Scan.PathOf(PlainEntry(ScanHarness.Manifest()));
        string newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, "Renamed" + Path.GetExtension(oldPath));
        File.Move(oldPath, newPath);

        w.Source.Raise(FolderChangeKind.Renamed, newPath, oldPath);
        await w.WaitForScansAsync(1);

        w.Scanner.Requests.Single().Paths.Should().BeEquivalentTo([oldPath, newPath]);
        w.Scanner.Reports.Single().Should().BeEquivalentTo(new { Missing = 1, Added = 1 });
        (await w.Scan.TrackAtAsync(oldPath)).Missing.Should().BeTrue();
        (await w.Scan.TrackAtAsync(newPath)).Missing.Should().BeFalse();
    }

    /// <summary>
    /// T-187: the test above failed once in a full suite run with two requests for its one rename. The two sides of
    /// a rename used to get their due times from two clock reads, so time passing between the reads (a thread
    /// descheduled on a busy machine) left the old path due before the new one, and a pump waking in that gap
    /// scanned them apart. Here the clock moves between reads on purpose and the pump wakes exactly when the old
    /// path falls due, so the split is certain on the old code rather than a matter of timing.
    /// </summary>
    [Fact]
    public async Task A_rename_is_one_request_even_when_time_passes_while_it_is_recorded_Async()
    {
        var clock = new ManualWatchClock();
        using WatchHarness w = await WatchHarness.CreateAsync(clock: clock);
        await w.Watcher.StartAsync();
        string oldPath = w.Scan.PathOf(PlainEntry(ScanHarness.Manifest()));
        string newPath = Path.Combine(Path.GetDirectoryName(oldPath)!, "Renamed" + Path.GetExtension(oldPath));
        File.Move(oldPath, newPath);

        using (clock.StepEachReadOnThisThread(TimeSpan.FromMilliseconds(10)))
        {
            w.Source.Raise(FolderChangeKind.Renamed, newPath, oldPath);
        }

        // The pump has looked, found nothing due yet and is waiting for the earliest due time: one debounce after
        // the first clock read the rename made.
        await WatchHarness.WaitUntilAsync(() => clock.ArmedTimers == 1, TimeSpan.FromSeconds(30), "the watcher to wait out the debounce");
        clock.AdvanceTo(WatchHarness.Debounce);
        await w.WaitForScansAsync(1);

        w.Scanner.Requests.Should().ContainSingle().Which.Paths.Should().BeEquivalentTo([oldPath, newPath], "both sides of a rename fall due together");
        w.Watcher.Stats.Pending.Should().Be(0, "nothing of the rename is left waiting for a second scan");
        w.Scanner.Reports.Single().Should().BeEquivalentTo(new { Missing = 1, Added = 1 });
    }

    [Fact]
    public async Task Only_supported_files_gone_paths_and_new_directories_are_taken_Async()
    {
        using WatchHarness w = await WatchHarness.CreateAsync();
        await w.Watcher.StartAsync();
        string directory = Path.GetDirectoryName(w.Scan.PathOf(PlainEntry(ScanHarness.Manifest())))!;
        string created = Path.Combine(w.Scan.Root, "New Album");
        Directory.CreateDirectory(created);

        w.Source.Raise(FolderChangeKind.Created, Path.Combine(directory, "cover.tmp"));
        w.Source.Raise(FolderChangeKind.Changed, Path.Combine(directory, "Thumbs.db"));
        w.Source.Raise(FolderChangeKind.Changed, directory);
        w.Source.Raise(FolderChangeKind.Created, Path.Combine(directory, "notes.txt"));
        await Task.Delay(WatchHarness.Debounce * 3);
        w.Scanner.Requests.Should().BeEmpty("none of those changes the library");
        w.Watcher.Stats.Ignored.Should().Be(4);

        w.Source.Raise(FolderChangeKind.Created, created);
        w.Source.Raise(FolderChangeKind.Deleted, Path.Combine(w.Scan.Root, "Gone"));
        w.Source.Raise(FolderChangeKind.Renamed, Path.Combine(w.Scan.Root, "Moved.here"), Path.Combine(w.Scan.Root, "Moved.there"));
        await w.WaitForScansAsync(1);

        w.Scanner.Requests.Single().Paths.Should().BeEquivalentTo(
            [created, Path.Combine(w.Scan.Root, "Gone"), Path.Combine(w.Scan.Root, "Moved.there")],
            "a new directory, a deleted path and the old side of a rename are taken; the new side is neither a file nor a directory");
        w.Watcher.Stats.Ignored.Should().Be(5);
    }

    [Fact]
    public async Task An_overflow_drops_the_pending_paths_and_rescans_the_whole_folder_Async()
    {
        using WatchHarness w = await WatchHarness.CreateAsync();
        await w.Watcher.StartAsync();
        string added = AddCopy(w.Scan, "Added during the storm");
        int before = (await w.Scan.AllTracksAsync()).Count;

        w.Source.Raise(FolderChangeKind.Created, added);
        w.Source.Fail(new InternalBufferOverflowException("Too many changes at once."));
        w.Source.Raise(FolderChangeKind.Changed, added);
        w.Watcher.Stats.Pending.Should().Be(0);
        await w.WaitForScansAsync(1);
        await Task.Delay(WatchHarness.Debounce * 3);

        w.Scanner.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(new { FolderIds = new[] { w.Scan.Folder.Id }, Paths = (string[]?)null });
        w.Scanner.Reports.Single().Should().BeEquivalentTo(new { Added = 1, Unchanged = before });
        w.Watcher.Stats.Should().BeEquivalentTo(new { Overflows = 1L, Scans = 1L, Events = 2L });
        (await w.Scan.AllTracksAsync()).Should().HaveCount(before + 1);
    }

    [Fact]
    public async Task Too_many_pending_paths_collapse_into_a_folder_rescan_Async()
    {
        using WatchHarness w = await WatchHarness.CreateAsync(maxPendingPaths: 3);
        await w.Watcher.StartAsync();
        string directory = Path.GetDirectoryName(w.Scan.PathOf(PlainEntry(ScanHarness.Manifest())))!;

        for (int i = 0; i < 4; i++)
        {
            w.Source.Raise(FolderChangeKind.Created, Path.Combine(directory, $"{i}.flac"));
        }

        w.Watcher.Stats.Should().BeEquivalentTo(new { Collapses = 1L, Pending = 0, Overflows = 0L });
        await w.WaitForScansAsync(1);
        await Task.Delay(WatchHarness.Debounce * 3);

        w.Scanner.Requests.Should().ContainSingle().Which.IsTargeted.Should().BeFalse();
    }

    [Fact]
    public async Task Work_waits_while_another_scan_is_running_and_nothing_is_dropped_Async()
    {
        using WatchHarness w = await WatchHarness.CreateAsync();
        await w.Watcher.StartAsync();
        string added = AddCopy(w.Scan, "Added mid-scan");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        w.Scan.Reader.BeforeRead = async (_, ct) =>
        {
            started.TrySetResult();
            await gate.Task.WaitAsync(ct);
        };
        Task<ScanReport> manual = w.Scan.ScanAsync(new ScanRequest(ForceReread: true));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        w.Source.Raise(FolderChangeKind.Created, added);

        // Held over the window, not sampled once at the end of it: the claim is that the path is queued the whole
        // time the manual scan is in the way, never briefly neither pending nor running.
        await WatchHarness.StaysTrueAsync(
            () => w.Scanner.Requests.IsEmpty && w.Watcher.Stats.Pending == 1,
            WatchHarness.Debounce * 4,
            () => $"the watcher does not scan while the manual scan runs and the path is still queued, not dropped (requests: {w.Scanner.Requests.Count}, pending: {w.Watcher.Stats.Pending})");

        w.Scan.Reader.BeforeRead = null;
        gate.SetResult();
        (await manual).Outcome.Should().Be(ScanOutcome.Completed);
        await w.WaitForScansAsync(1);

        w.Scanner.Requests.Single().Paths.Should().Equal(added);
        (await w.Scan.TrackAtAsync(added)).Missing.Should().BeFalse();
    }

    [Fact]
    public async Task A_scan_that_throws_is_counted_and_the_watcher_keeps_going_Async()
    {
        using WatchHarness w = await WatchHarness.CreateAsync();
        await w.Watcher.StartAsync();
        string first = AddCopy(w.Scan, "First");
        w.Scanner.FailNext = new IOException("disk went away");

        w.Source.Raise(FolderChangeKind.Created, first);
        await WatchHarness.WaitUntilAsync(() => w.Watcher.Stats.Failures == 1, TimeSpan.FromSeconds(30), "the failed scan");
        w.Scanner.Requests.Should().ContainSingle();

        string second = AddCopy(w.Scan, "Second");
        w.Source.Raise(FolderChangeKind.Created, second);
        await w.WaitForScansAsync(1);

        w.Scanner.Requests.Should().HaveCount(2);
        w.Scanner.Requests.Last().Paths.Should().Equal(second);
        w.Watcher.Stats.Should().BeEquivalentTo(new { Failures = 1L, Scans = 1L });
    }

    [Fact]
    public async Task A_scan_refused_because_one_just_started_is_retried_Async()
    {
        using WatchHarness w = await WatchHarness.CreateAsync();
        await w.Watcher.StartAsync();
        string added = AddCopy(w.Scan, "Retried");
        w.Scanner.FailNext = new InvalidOperationException("A library scan is already running.");

        w.Source.Raise(FolderChangeKind.Created, added);
        await w.WaitForScansAsync(1);

        w.Scanner.Requests.Should().HaveCount(2, "the refused request went back on the queue");
        w.Scanner.Requests.Select(r => r.Paths!.Single()).Should().AllBe(added);
        w.Watcher.Stats.Should().BeEquivalentTo(new { Failures = 0L, Scans = 1L });
    }

    [Fact]
    public async Task Stop_drops_pending_work_and_disposes_the_watches_Async()
    {
        using WatchHarness w = await WatchHarness.CreateAsync();
        await w.Watcher.StartAsync();
        w.Source.Raise(FolderChangeKind.Created, AddCopy(w.Scan, "Never scanned"));

        await w.Watcher.StopAsync();
        await Task.Delay(WatchHarness.Debounce * 3);

        w.Scanner.Requests.Should().BeEmpty();
        w.Source.Live.Should().BeEmpty();
        w.Watcher.Stats.Pending.Should().Be(0);
        w.Watcher.IsWatching.Should().BeFalse();
    }
}
