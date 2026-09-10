using System.Diagnostics;
using Tunqio.Core.Library;
using Tunqio.FixtureGen;
using Tunqio.Library.Scanning;
using Xunit.Abstractions;

namespace Tunqio.Library.Tests.Scanning;

/// <summary>
/// E3-S6 acceptance over a real <see cref="FileSystemWatcher"/>: a file added to a watched folder is in the
/// library within 5 s with no manual rescan (AC-94), and 5k files copied at once end as a complete library
/// with no duplicates (AC-95). The watch buffer is set to its 4 KB minimum for the second to make an overflow
/// likely, though whether one happens is the operating system's call and the outcome must hold either way;
/// the first keeps the documented 2 s debounce.
/// </summary>
public sealed class LibraryWatcherIntegrationTests(ITestOutputHelper output)
{
    private static FixtureFileEntry PlainEntry(FixtureManifest manifest) => manifest.Files.First(f => f.AlbumArtist is not null && !f.CorruptTags && f.Format == "flac");

    [Fact]
    public async Task A_file_added_to_a_watched_folder_is_in_the_library_within_5_s_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync();
        await h.ScanAsync();
        int before = (await h.AllTracksAsync()).Count;
        using var watcher = new LibraryWatcher(h.Scanner, h.Service.Folders);
        await watcher.StartAsync();
        watcher.Stats.Folders.Should().Be(1);
        string source = h.PathOf(PlainEntry(ScanHarness.Manifest()));
        string added = Path.Combine(Path.GetDirectoryName(source)!, "Dropped in" + Path.GetExtension(source));

        Stopwatch elapsed = Stopwatch.StartNew();
        File.Copy(source, added);
        await WatchHarness.WaitUntilAsync(() => h.Tracks.Batches > 1, TimeSpan.FromSeconds(5), "the watcher's upsert");
        elapsed.Stop();

        output.WriteLine($"in the library after {elapsed.Elapsed.TotalSeconds:0.00} s (debounce {LibraryWatcherOptions.Default.Debounce.TotalSeconds:0} s)");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        (await h.AllTracksAsync()).Should().HaveCount(before + 1);
        (await h.TrackAtAsync(added)).Missing.Should().BeFalse();
        watcher.Stats.Scans.Should().BeGreaterThanOrEqualTo(1);
        await watcher.StopAsync();
    }

    [Fact]
    public async Task Five_thousand_files_copied_at_once_give_a_complete_library_with_no_duplicates_Async()
    {
        const int Files = 5_000;
        using ScanHarness h = await ScanHarness.CreateAsync(copyFixtures: false);
        await h.ScanAsync();
        using var watcher = new LibraryWatcher(h.Scanner, h.Service.Folders, new LibraryWatcherOptions { BufferSize = 4096 });
        await watcher.StartAsync();

        Stopwatch elapsed = Stopwatch.StartNew();
        IReadOnlyList<string> written = ScanTreeBuilder.Build(Path.Combine(h.Root, "Import"), Files);
        TimeSpan copy = elapsed.Elapsed;
        await WatchHarness.WaitUntilAsync(
            () => watcher.Stats.Pending == 0 && !h.Scanner.IsScanning && h.Scalar("SELECT count(*) FROM track") >= Files,
            TimeSpan.FromSeconds(120),
            $"{Files} rows; have {h.Scalar("SELECT count(*) FROM track")}, watcher {watcher.Stats}");
        await Task.Delay(LibraryWatcherOptions.Default.Debounce + TimeSpan.FromSeconds(1)); // let a trailing rescan settle
        await WatchHarness.WaitUntilAsync(() => watcher.Stats.Pending == 0 && !h.Scanner.IsScanning, TimeSpan.FromSeconds(60), "the watcher to go idle");
        elapsed.Stop();
        LibraryWatcherStats stats = watcher.Stats;
        output.WriteLine($"copied {Files} files in {copy.TotalSeconds:0.0} s; complete after {elapsed.Elapsed.TotalSeconds:0.0} s; {stats}");

        // Whether a 4 KB buffer actually overflows is the operating system's decision, not this test's. On a
        // machine whose watcher drains faster than the copy writes, 5k files arrive as 5k events and none are
        // lost: 0 overflows on both a Windows 10 19045 dev box and windows-2025-vs2026, with 15 526 events seen
        // and every row correct. Requiring an overflow failed the test for something that says nothing about the
        // library, and the overflow path is already pinned where it can be made to happen on demand -
        // LibraryWatcherTests raises InternalBufferOverflowException at the source and asserts the rescan. The
        // small buffer stays because it makes the overflow likelier, and the claim AC-95 actually makes is the
        // outcome below, which holds either way.
        output.WriteLine($"overflows: {stats.Overflows} (the rescan path ran {(stats.Overflows > 0 ? "and was exercised here" : "in LibraryWatcherTests, not here")})");
        h.Scalar("SELECT count(*) FROM track").Should().Be(Files, "every file is there once");
        h.Scalar("SELECT count(DISTINCT path) FROM track").Should().Be(Files);
        h.Scalar("SELECT count(*) FROM track WHERE missing = 1").Should().Be(0);
        h.Scalar("SELECT count(*) FROM track_fts").Should().Be(Files);
        (await h.AllTracksAsync()).Select(t => t.Path).Order(StringComparer.Ordinal).Should().Equal(written.Order(StringComparer.Ordinal));

        // Nothing was missed: a full scan now finds everything unchanged.
        ScanReport check = await h.ScanAsync();
        check.Should().BeEquivalentTo(new { Added = 0, Updated = 0, Missing = 0, Unchanged = Files });
        await watcher.StopAsync();
    }
}
