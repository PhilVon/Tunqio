using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.FixtureGen;
using Tunqio.Library.Database;
using Tunqio.Library.Repositories;
using Tunqio.Library.Scanning;
using Tunqio.Library.Tags;
using Xunit.Abstractions;

namespace Tunqio.Library.Tests.Scanning;

/// <summary>
/// What one slow upsert batch does to the rest of the library while a scan runs. SQLite checkpoints the WAL on
/// its own once it passes about a thousand pages, so on slow storage roughly every seventh 500-track batch of
/// an import pays for the accumulated writes — 1.04 s to 1.15 s against a 50 ms median on the windows-2025
/// runner (docs/spikes/wal-checkpoint-during-scan.md). The stall is injected here rather than provoked: the
/// development machine checkpoints a full WAL in 69 ms and would never show it, and a test that waits for a
/// real checkpoint would be measuring the runner's disk instead of this claim.
/// <para>
/// The claim is that the stall stays inside the writer. A checkpoint is a write, and in WAL a write never holds
/// up a reader, so the shell's Tracks view and its search go on answering at their usual speed for the whole
/// second. What the stall does reach is the scan's own progress readout (the pipeline's channels fill behind
/// the writer and the counters stop moving) and anything else that wants the writer lease; neither is asserted
/// here, because both depend on how fast the machine reads tags. The output line records them.
/// </para>
/// </summary>
public sealed class ScanWriterStallTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>Ten batches, so a stall in the middle has the pipeline at full speed on either side of it.</summary>
    private const int Files = 5_000;

    private const int StallOnBatch = 4;

    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(1);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunqio-scan-stall-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task A_stalled_upsert_batch_does_not_slow_the_librarys_readers_Async()
    {
        string tree = Path.Combine(_root, "music");
        ScanTreeBuilder.Build(tree, Files);
        var paths = new AppPaths(Path.Combine(_root, "app"));
        paths.EnsureCreated();
        using LibraryDatabase db = LibraryDatabase.Open(paths);
        var tracks = new SqliteTrackRepository(db);
        var folders = new SqliteLibraryFolderRepository(db);
        var search = new SqliteSearchService(db);
        LibraryFolderDto folder = await folders.AddAsync(tree);

        var clock = Stopwatch.StartNew();
        var stalling = new StallingTracks(tracks, db, clock);
        // No art cache and no duration probe: this test measures writer contention, not the pipeline's optional
        // stages, and T-180 makes that a statement rather than an omission.
        var scanner = new LibraryScanner(stalling, folders, new TagLibTagReader(new TagReaderOptions()), artCache: null, durationProbe: null);
        var progress = new ProgressClock(clock);
        var reads = new ConcurrentQueue<Read>();
        using var stop = new CancellationTokenSource();
        Task reader = Task.Run(() => ReadLoopAsync(tracks, search, clock, reads, stop.Token));

        ScanReport report = await scanner.ScanAsync(ScanRequest.Folder(folder.Id), progress);
        await stop.CancelAsync();
        await reader;

        report.Outcome.Should().Be(ScanOutcome.Completed);
        report.Added.Should().Be(Files);
        stalling.StalledFrom.Should().BeGreaterThan(0, $"batch {StallOnBatch} of {stalling.Batches} never ran");

        Read[] during = reads
            .Where(r => r.StartedAt >= stalling.StalledFrom && r.EndedAt <= stalling.StalledUntil)
            .ToArray();
        double longestGap = LongestGap(progress.Ticks);
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{stalling.Batches} batches, batch {StallOnBatch} held the writer for {(stalling.StalledUntil - stalling.StalledFrom) * 1000:0} ms; " +
            $"{during.Length} read(s) ran inside that window, slowest {(during.Length == 0 ? 0 : during.Max(r => r.Milliseconds)):0.0} ms; " +
            $"longest gap between progress reports {longestGap:0} ms"));

        during.Length.Should().BeGreaterThanOrEqualTo(5, "a WAL reader does not wait for the writer, so reads keep completing through the stall");
        // Half the stall, not a latency budget: a read that had queued behind the writer would take the whole
        // second, and a read that did not takes tens of milliseconds even on much slower storage than this.
        during.Max(r => r.Milliseconds).Should().BeLessThan(Stall.TotalMilliseconds / 2, "no read should be waiting on the stalled writer at all");
    }

    private static double LongestGap(IReadOnlyList<double> ticks)
    {
        double longest = 0;
        for (int i = 1; i < ticks.Count; i++)
        {
            longest = Math.Max(longest, (ticks[i] - ticks[i - 1]) * 1000);
        }

        return longest;
    }

    /// <summary>The two reads the shell makes most while a scan runs: a page of the Tracks view, and the search box.</summary>
    private static async Task ReadLoopAsync(SqliteTrackRepository tracks, SqliteSearchService search, Stopwatch clock, ConcurrentQueue<Read> reads, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                double started = clock.Elapsed.TotalSeconds;
                await tracks.ListAsync(new TrackQuery(PageSize: 100), ct);
                await search.SearchAsync("track", SearchLimits.Default, ct);
                double ended = clock.Elapsed.TotalSeconds;
                reads.Enqueue(new Read(started, ended, (ended - started) * 1000));
                await Task.Delay(5, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private sealed record Read(double StartedAt, double EndedAt, double Milliseconds);

    private sealed class ProgressClock(Stopwatch clock) : IProgress<ScanProgress>
    {
        private readonly ConcurrentQueue<double> _ticks = new();

        public IReadOnlyList<double> Ticks => _ticks.ToArray();

        public void Report(ScanProgress value) => _ticks.Enqueue(clock.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Passes every batch through and holds the writer lease for <see cref="Stall"/> after one of them, which
    /// is where an automatic checkpoint's cost lands: inside the commit, with the lease still held.
    /// </summary>
    private sealed class StallingTracks(ITrackRepository inner, LibraryDatabase db, Stopwatch clock) : ITrackRepository
    {
        private int _batches;

        public int Batches => Volatile.Read(ref _batches);

        public double StalledFrom { get; private set; }

        public double StalledUntil { get; private set; }

        public async Task UpsertBatchAsync(IReadOnlyList<ScannedTrack> tracks, CancellationToken ct = default)
        {
            await inner.UpsertBatchAsync(tracks, ct);
            if (Interlocked.Increment(ref _batches) != StallOnBatch)
            {
                return;
            }

            using IDisposable lease = await db.AcquireWriterAsync(ct);
            StalledFrom = clock.Elapsed.TotalSeconds;
            await Task.Delay(Stall, ct);
            StalledUntil = clock.Elapsed.TotalSeconds;
        }

        public Task<TrackDto?> GetAsync(long id, CancellationToken ct = default) => inner.GetAsync(id, ct);

        public Task<IReadOnlyList<TrackDto>> GetByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default) => inner.GetByIdsAsync(ids, ct);

        public Task<TrackDto?> GetByPathAsync(string path, CancellationToken ct = default) => inner.GetByPathAsync(path, ct);

        public Task<IReadOnlyList<TrackDto>> ListAsync(TrackQuery query, CancellationToken ct = default) => inner.ListAsync(query, ct);

        public IAsyncEnumerable<TrackDto> StreamAsync(TrackQuery query, CancellationToken ct = default) => inner.StreamAsync(query, ct);

        public Task<int> CountAsync(TrackQuery query, CancellationToken ct = default) => inner.CountAsync(query, ct);

        public Task MarkMissingAsync(IReadOnlyList<long> ids, bool missing, CancellationToken ct = default) => inner.MarkMissingAsync(ids, missing, ct);

        public Task<int> CountMissingAsync(long missingBefore, CancellationToken ct = default) => inner.CountMissingAsync(missingBefore, ct);

        public Task<int> PurgeMissingAsync(long missingBefore, CancellationToken ct = default) => inner.PurgeMissingAsync(missingBefore, ct);

        public Task<IReadOnlyList<TrackFileStamp>> SnapshotAsync(long folderId, CancellationToken ct = default) => inner.SnapshotAsync(folderId, ct);

        public Task<bool> SetRatingAsync(long id, int? rating, CancellationToken ct = default) => inner.SetRatingAsync(id, rating, ct);
    }
}
