using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.Core.Library;
using Tunqio.Library.Tags;

namespace Tunqio.Library.Scanning;

/// <summary>
/// <see cref="ILibraryScanner"/> as the pipeline of docs/library-and-data.md ("Scanner"), one folder at a time:
/// <code>
/// Enumerate+Diff ──► ReadTags (N workers) ──► ExtractArt ──► Upsert (1 writer, 500 rows per transaction)
///   (1 thread)         per directory          (2 workers)     then MarkMissing, RecordScan
/// </code>
/// The stages are joined by bounded channels so enumeration never runs far ahead of the writer. Enumerate and
/// Diff are one thread: the walk yields size and mtime with each entry, and a file whose stamp matches the
/// snapshot taken at scan start is counted and dropped there. Files travel through ReadTags a directory at a
/// time because <see cref="CompilationRule"/> needs a folder's worth of tracks; when a changed file in a
/// directory has no album artist and the directory also has unchanged siblings, the siblings are read again
/// so the rule sees the whole folder (they are re-upserted unchanged and still counted as unchanged). The
/// tag reader runs on the pool; the documented below-normal priority is not applied because pool threads
/// are shared, and the per-file timeout already bounds the damage one file can do.
/// <para>
/// Consistency under cancellation comes from the repository: every batch is one transaction, so a cancelled
/// scan keeps the batches that committed and nothing of the one in flight. Missing marking only happens
/// after a folder has been walked to the end, so an interrupted walk never flags files it did not reach.
/// </para>
/// <para>
/// A targeted request (<see cref="ScanRequest.Paths"/>, the watcher's) runs the same pipeline over a set of
/// <em>scopes</em> instead of the whole root: a directory that exists is walked (recursively), a file is
/// diffed with the directory it is in (one listing; the compilation rule still sees the folder, and the
/// unchanged siblings are dropped by Diff as usual), and a path that is gone is either a directory the
/// snapshot knows, whose rows are all marked missing, or a file, found missing by the listing of its
/// directory. Only snapshot rows inside a scope can be marked missing, and the folder's last-scan record is
/// not written.
/// </para>
/// </summary>
public sealed class LibraryScanner : ILibraryScanner
{
    /// <summary>Rows per upsert transaction (docs/library-and-data.md "Repository layer").</summary>
    public const int BatchSize = 500;

    /// <summary>The scan status recorded on a folder whose root was not there.</summary>
    public const string StatusOffline = "offline";

    /// <summary>Progress is reported at most this often (plus once at the end).</summary>
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        ReturnSpecialDirectories = false,
    };

    /// <summary>One directory, no descent: a targeted scan's scope for a file event.</summary>
    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        ReturnSpecialDirectories = false,
    };

    private readonly ITrackRepository _tracks;
    private readonly ILibraryFolderRepository _folders;
    private readonly ITagReader _reader;
    private readonly IArtCache? _art;
    private readonly IDurationProbe? _probe;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly int _readDegree;
    private int _scanning;

    /// <param name="artCache">
    /// ExtractArt's destination; <c>null</c> scans without extracting any album art at all. Required and
    /// positional, with no default (T-180) — this is a whole stage of the pipeline, and losing it silently is
    /// the shape of defect T-156 and T-179 both were.
    /// </param>
    /// <param name="durationProbe">
    /// The slow path for a file whose tag carries no duration; <c>null</c> leaves such a track at
    /// <c>duration_ms = 0</c>. Required and positional for the same reason, and today every caller passes null
    /// because nothing implements <see cref="IDurationProbe"/> yet — which is now said at each call site rather
    /// than hidden in a default (T-180).
    /// </param>
    /// <param name="readDegree">ReadTags workers; <c>null</c> means <see cref="DefaultReadDegree"/>.</param>
    public LibraryScanner(
        ITrackRepository tracks,
        ILibraryFolderRepository folders,
        ITagReader reader,
        IArtCache? artCache,
        IDurationProbe? durationProbe,
        TimeProvider? clock = null,
        ILogger? logger = null,
        int? readDegree = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(reader);
        _tracks = tracks;
        _folders = folders;
        _reader = reader;
        _art = artCache;
        _probe = durationProbe;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _readDegree = readDegree ?? DefaultReadDegree;
        ArgumentOutOfRangeException.ThrowIfLessThan(_readDegree, 1, nameof(readDegree));
    }

    /// <summary>The documented ReadTags parallelism: <c>max(2, cores / 2)</c>.</summary>
    public static int DefaultReadDegree => Math.Max(2, Environment.ProcessorCount / 2);

    public bool IsScanning => Volatile.Read(ref _scanning) == 1;

    public event EventHandler<ScanReport>? ScanCompleted;

    public async Task<ScanReport> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.IsTargeted && request.FolderIds is not { Count: 1 })
        {
            throw new ArgumentException("A targeted scan names exactly one folder.", nameof(request));
        }

        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0)
        {
            throw new InvalidOperationException("A library scan is already running.");
        }

        ScanReport report;
        using (var run = new ScanRun(_clock, progress, _readDegree))
        {
            try
            {
                report = await RunAsync(request, run, ct).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _scanning, 0);
            }
        }

        RaiseCompleted(report);
        return report;
    }

    private void RaiseCompleted(ScanReport report)
    {
        try
        {
            ScanCompleted?.Invoke(this, report);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogError(e, "A ScanCompleted handler threw");
        }
    }

    private async Task<ScanReport> RunAsync(ScanRequest request, ScanRun run, CancellationToken ct)
    {
        IReadOnlyList<LibraryFolderDto> all = await _folders.ListAsync(ct).ConfigureAwait(false);
        List<LibraryFolderDto> selected = all
            .Where(f => f.Enabled && (request.FolderIds is null || request.FolderIds.Contains(f.Id)))
            .ToList();
        if (request.IsTargeted)
        {
            _logger.LogDebug("Targeted library scan of {Count} path(s) under folder {FolderId}", request.Paths!.Count, request.FolderIds![0]);
        }
        else
        {
            _logger.LogInformation("Library scan starting over {Count} folder(s){Force}", selected.Count, request.ForceReread ? " (full re-read)" : string.Empty);
        }

        ScanOutcome outcome = ScanOutcome.Completed;
        string? error = null;
        try
        {
            foreach (LibraryFolderDto folder in selected)
            {
                await ScanFolderAsync(folder, request, run, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            outcome = ScanOutcome.Cancelled;
            _logger.LogInformation("Library scan cancelled");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            outcome = ScanOutcome.Failed;
            error = e.Message;
            _logger.LogError(e, "Library scan failed");
        }

        ScanReport report = run.Finish(outcome, error);
        _logger.Log(
            request.IsTargeted ? LogLevel.Debug : LogLevel.Information,
            "Library scan {Outcome} in {Elapsed:0.0} s: {Seen} seen, {Added} added, {Updated} updated, {Unchanged} unchanged, {Failed} failed, {Missing} missing, {Restored} restored",
            report.Outcome, report.Elapsed.TotalSeconds, report.Seen, report.Added, report.Updated, report.Unchanged, report.Failed, report.Missing, report.Restored);
        return report;
    }

    private async Task ScanFolderAsync(LibraryFolderDto folder, ScanRequest request, ScanRun run, CancellationToken ct)
    {
        bool forceReread = request.ForceReread;
        FolderRun state = run.BeginFolder(folder);
        IReadOnlyList<TrackFileStamp> stamps = await _tracks.SnapshotAsync(folder.Id, ct).ConfigureAwait(false);
        var snapshot = new Dictionary<string, TrackFileStamp>(stamps.Count, StringComparer.Ordinal);
        foreach (TrackFileStamp stamp in stamps)
        {
            snapshot[stamp.Path] = stamp;
        }

        if (!Directory.Exists(folder.Path))
        {
            // Offline share or unplugged drive: everything under it is away, nothing is purged.
            _logger.LogWarning("Library folder {Path} is not there; marking its {Count} track(s) missing", folder.Path, stamps.Count);
            state.Offline = true;
            await MarkAsync(stamps.Where(s => !s.Missing).Select(s => s.Id).ToList(), missing: true, state, ct).ConfigureAwait(false);
            await _folders.RecordScanAsync(folder.Id, _clock.GetUtcNow().ToUnixTimeMilliseconds(), StatusOffline, ct).ConfigureAwait(false);
            state.Ended = true;
            return;
        }

        IReadOnlyList<ScanScope> scopes = request.IsTargeted
            ? ScanScope.Resolve(folder.Path, request.Paths!, snapshot.Keys, _logger)
            : [new ScanScope(folder.Path, Recursive: true)];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var restore = new List<long>();
        Channel<DirectoryBatch> directories = Channel.CreateBounded<DirectoryBatch>(new BoundedChannelOptions(64) { SingleWriter = true });
        Channel<IReadOnlyList<ScanResult>> batches = Channel.CreateBounded<IReadOnlyList<ScanResult>>(new BoundedChannelOptions(16) { SingleReader = true });

        using var stages = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationToken token = stages.Token;
        try
        {
            Task enumerate = Task.Run(() => StageAsync(() => EnumerateAsync(scopes, snapshot, forceReread, seen, restore, directories.Writer, run, token), directories.Writer.TryComplete, stages), CancellationToken.None);
            Task read = Task.Run(() => StageAsync(() => ReadAsync(folder.Id, directories.Reader, batches.Writer, run, token), batches.Writer.TryComplete, stages), CancellationToken.None);
            Task upsert = StageAsync(() => UpsertAsync(batches.Reader, run, token), null, stages);
            await WhenAllStagesAsync(ct, enumerate, read, upsert).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await _folders.RecordScanAsync(folder.Id, _clock.GetUtcNow().ToUnixTimeMilliseconds(), "cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            await _folders.RecordScanAsync(folder.Id, _clock.GetUtcNow().ToUnixTimeMilliseconds(), "failed: " + e.Message, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        run.Phase = ScanPhase.MarkingMissing;
        List<long> gone = snapshot.Values
            .Where(s => !s.Missing && !seen.Contains(s.Path) && (!request.IsTargeted || ScanScope.Covers(scopes, s.Path)))
            .Select(s => s.Id)
            .ToList();
        await MarkAsync(gone, missing: true, state, ct).ConfigureAwait(false);
        await MarkAsync(restore, missing: false, state, ct).ConfigureAwait(false);

        if (!request.IsTargeted)
        {
            string status = state.Failed.Value == 0 ? "ok" : $"ok, {state.Failed.Value} file(s) with unreadable tags";
            await _folders.RecordScanAsync(folder.Id, _clock.GetUtcNow().ToUnixTimeMilliseconds(), status, ct).ConfigureAwait(false);
        }

        state.Ended = true;
    }

    /// <summary>
    /// Runs one stage. On failure it cancels the other stages and faults the channel it writes so a reader is
    /// never left waiting; on success it completes that channel.
    /// </summary>
    private static async Task StageAsync(Func<Task> body, Func<Exception?, bool>? complete, CancellationTokenSource stages)
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            complete?.Invoke(e);
            if (!stages.IsCancellationRequested)
            {
                await stages.CancelAsync().ConfigureAwait(false);
            }

            throw;
        }

        complete?.Invoke(null);
    }

    /// <summary>Awaits every stage; surfaces the first real failure over the cancellations it caused.</summary>
    private static async Task WhenAllStagesAsync(CancellationToken ct, params Task[] stages)
    {
        try
        {
            await Task.WhenAll(stages).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            Exception? cause = stages
                .Where(t => t.IsFaulted)
                .SelectMany(t => t.Exception!.InnerExceptions)
                .FirstOrDefault(e => e is not OperationCanceledException and not ChannelClosedException);
            if (cause is not null)
            {
                ExceptionDispatchInfo.Throw(cause);
            }

            throw;
        }
    }

    /// <summary>Enumerate and Diff, on one thread: walks each scope, drops unchanged files, groups the rest by directory.</summary>
    private static async Task EnumerateAsync(IReadOnlyList<ScanScope> scopes, Dictionary<string, TrackFileStamp> snapshot, bool forceReread, HashSet<string> seen, List<long> restore, ChannelWriter<DirectoryBatch> writer, ScanRun run, CancellationToken ct)
    {
        run.Phase = ScanPhase.Enumerating;
        string? currentDirectory = null;
        var toRead = new List<FileStamp>();
        var unchanged = new List<FileStamp>();
        foreach (ScanScope scope in scopes)
        {
            if (!Directory.Exists(scope.Directory))
            {
                continue; // a removed directory: its rows are marked missing after the walk
            }

            var walk = new FileSystemEnumerable<FileStamp>(
                scope.Directory,
                (ref FileSystemEntry entry) => new FileStamp(entry.ToFullPath(), entry.Length, entry.LastWriteTimeUtc.ToUnixTimeMilliseconds()),
                scope.Recursive ? WalkOptions : ListOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && AudioFormats.IsSupported(entry.FileName),
            };

            await WalkAsync(walk, scope.Directory).ConfigureAwait(false);
        }

        await FlushAsync().ConfigureAwait(false);

        async Task WalkAsync(FileSystemEnumerable<FileStamp> walk, string scopeDirectory)
        {
            foreach (FileStamp file in walk)
            {
                ct.ThrowIfCancellationRequested();
                string directory = Path.GetDirectoryName(file.Path) ?? scopeDirectory;
                if (!string.Equals(directory, currentDirectory, StringComparison.Ordinal))
                {
                    await FlushAsync().ConfigureAwait(false);
                    currentDirectory = directory;
                }

                seen.Add(file.Path);
                run.Folder.Seen.Increment();
                run.CurrentPath = file.Path;
                TrackFileStamp? known = snapshot.GetValueOrDefault(file.Path);
                if (!forceReread && known is not null && known.FileSize == file.Size && known.FileMtime == file.Mtime)
                {
                    run.Folder.Unchanged.Increment();
                    if (known.Missing)
                    {
                        restore.Add(known.Id);
                    }

                    unchanged.Add(file);
                }
                else
                {
                    toRead.Add(file with { IsNew = known is null });
                }

                run.Report();
            }
        }

        async Task FlushAsync()
        {
            if (toRead.Count > 0)
            {
                await writer.WriteAsync(new DirectoryBatch(toRead.ToArray(), unchanged.ToArray()), ct).ConfigureAwait(false);
            }

            toRead.Clear();
            unchanged.Clear();
        }
    }

    /// <summary>ReadTags and ExtractArt: directories in parallel, files within a directory in parallel, reads bounded overall by the read degree.</summary>
    private async Task ReadAsync(long folderId, ChannelReader<DirectoryBatch> directories, ChannelWriter<IReadOnlyList<ScanResult>> batches, ScanRun run, CancellationToken ct)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = _readDegree, CancellationToken = ct };
        await Parallel.ForEachAsync(
            directories.ReadAllAsync(ct),
            options,
            async (batch, token) =>
            {
                run.Phase = ScanPhase.Reading;
                var results = new ConcurrentBag<ScanResult>();
                await ReadFilesAsync(batch.ToRead, folderId, sibling: false, results, run, token).ConfigureAwait(false);
                if (batch.Unchanged.Length > 0 && results.Any(r => r.Track.AlbumArtist is null && r.Track.AlbumTitle is not null))
                {
                    // The compilation rule needs the whole folder; the unchanged siblings are read again for it.
                    await ReadFilesAsync(batch.Unchanged, folderId, sibling: true, results, run, token).ConfigureAwait(false);
                }

                ScanResult[] ordered = results.OrderBy(r => r.Track.Path, StringComparer.Ordinal).ToArray();
                IReadOnlyList<ScannedTrack> tracks = CompilationRule.Apply(ordered.Select(r => r.Track).ToArray());
                for (int i = 0; i < ordered.Length; i++)
                {
                    ordered[i] = ordered[i] with { Track = tracks[i] };
                }

                await batches.WriteAsync(ordered, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    private async Task ReadFilesAsync(FileStamp[] files, long folderId, bool sibling, ConcurrentBag<ScanResult> results, ScanRun run, CancellationToken ct)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = _readDegree, CancellationToken = ct };
        await Parallel.ForEachAsync(
            files,
            options,
            async (file, token) =>
            {
                results.Add(await ReadOneAsync(file, folderId, sibling, run, token).ConfigureAwait(false));
            }).ConfigureAwait(false);
    }

    private async Task<ScanResult> ReadOneAsync(FileStamp file, long folderId, bool sibling, ScanRun run, CancellationToken ct)
    {
        TagReadResult read;
        await run.ReadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            read = await _reader.ReadAsync(file.Path, folderId, ct).ConfigureAwait(false);
        }
        finally
        {
            run.ReadGate.Release();
        }

        ScannedTrack track = read.Track;
        if (track.DurationMs <= 0 && _probe is not null)
        {
            run.Folder.SlowPath.Increment();
            int? ms = await ProbeAsync(file.Path, ct).ConfigureAwait(false);
            if (ms > 0)
            {
                track = track with { DurationMs = ms.Value };
            }
        }

        if (_art is not null)
        {
            ArtHashes hashes = await StoreArtAsync(read.Picture, file.Path, run, ct).ConfigureAwait(false);
            track = track with
            {
                TrackArtHash = hashes.TrackArtHash ?? track.TrackArtHash,
                AlbumArtHash = hashes.AlbumArtHash ?? track.AlbumArtHash,
            };
        }

        if (!sibling)
        {
            run.Folder.Processed.Increment();
            if (read.IsFailure)
            {
                run.Folder.Failed.Increment();
                run.Failures.Enqueue(new ScanFailure(file.Path, read.Outcome, read.Error));
            }
        }

        run.CurrentPath = file.Path;
        run.Report();
        return new ScanResult(track, file.IsNew, sibling);
    }

    private async Task<int?> ProbeAsync(string path, CancellationToken ct)
    {
        try
        {
            return await _probe!.ProbeAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogWarning(e, "Duration probe of {Path} failed", path);
            return null;
        }
    }

    private async Task<ArtHashes> StoreArtAsync(EmbeddedPicture? picture, string path, ScanRun run, CancellationToken ct)
    {
        await run.ArtGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _art!.StoreAsync(picture, path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogWarning(e, "Art extraction for {Path} failed", path);
            return ArtHashes.None;
        }
        finally
        {
            run.ArtGate.Release();
        }
    }

    /// <summary>
    /// The single writer: fills batches of exactly <see cref="BatchSize"/> rows, one transaction each, and the
    /// remainder at the end. Nothing here checkpoints the WAL: SQLite does it on its own about every seventh
    /// batch, and on slow storage that batch takes about a second. Measured, with the three remedies that were
    /// considered and rejected, in docs/spikes/wal-checkpoint-during-scan.md — the short of it is that a
    /// checkpoint is a write, so it never reaches a reader, and the rest is not worth restructuring for.
    /// </summary>
    private async Task UpsertAsync(ChannelReader<IReadOnlyList<ScanResult>> batches, ScanRun run, CancellationToken ct)
    {
        var buffer = new List<ScanResult>(BatchSize * 2);
        await foreach (IReadOnlyList<ScanResult> batch in batches.ReadAllAsync(ct).ConfigureAwait(false))
        {
            buffer.AddRange(batch);
            while (buffer.Count >= BatchSize)
            {
                await FlushAsync(buffer, BatchSize, run, ct).ConfigureAwait(false);
            }
        }

        if (buffer.Count > 0)
        {
            await FlushAsync(buffer, buffer.Count, run, ct).ConfigureAwait(false);
        }
    }

    private async Task FlushAsync(List<ScanResult> buffer, int count, ScanRun run, CancellationToken ct)
    {
        var rows = new ScannedTrack[count];
        for (int i = 0; i < count; i++)
        {
            rows[i] = buffer[i].Track;
        }

        await _tracks.UpsertBatchAsync(rows, ct).ConfigureAwait(false);
        for (int i = 0; i < count; i++)
        {
            ScanResult result = buffer[i];
            if (result.Sibling)
            {
                continue;
            }

            if (result.IsNew)
            {
                run.Folder.Added.Increment();
            }
            else
            {
                run.Folder.Updated.Increment();
            }
        }

        buffer.RemoveRange(0, count);
        run.Report();
    }

    private async Task MarkAsync(List<long> ids, bool missing, FolderRun state, CancellationToken ct)
    {
        for (int offset = 0; offset < ids.Count; offset += BatchSize)
        {
            int count = Math.Min(BatchSize, ids.Count - offset);
            await _tracks.MarkMissingAsync(ids.Skip(offset).Take(count).ToArray(), missing, ct).ConfigureAwait(false);
            (missing ? state.Missing : state.Restored).Add(count);
        }
    }

    /// <summary>A file as the walk reports it; <see cref="IsNew"/> is set by Diff when the path is not in the snapshot.</summary>
    private sealed record FileStamp(string Path, long Size, long Mtime, bool IsNew = false);

    /// <summary>The files of one directory: those to read, and the unchanged siblings the compilation rule may need.</summary>
    private sealed record DirectoryBatch(FileStamp[] ToRead, FileStamp[] Unchanged);

    /// <summary>A read file on its way to the writer. <see cref="Sibling"/> rows are unchanged files re-read for the compilation rule.</summary>
    private sealed record ScanResult(ScannedTrack Track, bool IsNew, bool Sibling);

    /// <summary>A counter written from the pipeline's threads.</summary>
    private sealed class Counter
    {
        private int _value;

        public int Value => Volatile.Read(ref _value);

        public void Increment() => Interlocked.Increment(ref _value);

        public void Add(int count) => Interlocked.Add(ref _value, count);
    }

    /// <summary>Per-folder counters.</summary>
    private sealed class FolderRun
    {
        public FolderRun(LibraryFolderDto folder)
        {
            Folder = folder;
        }

        public LibraryFolderDto Folder { get; }

        public bool Offline { get; set; }

        /// <summary>True once the folder's scan ran to its end (missing marked, status recorded); only ended folders appear in the report.</summary>
        public volatile bool Ended;

        public Counter Seen { get; } = new();

        public Counter Processed { get; } = new();

        public Counter Added { get; } = new();

        public Counter Updated { get; } = new();

        public Counter Unchanged { get; } = new();

        public Counter Failed { get; } = new();

        public Counter SlowPath { get; } = new();

        public Counter Missing { get; } = new();

        public Counter Restored { get; } = new();

        public ScanFolderReport ToReport() => new(Folder.Id, Folder.Path, Offline, Seen.Value, Added.Value, Updated.Value, Unchanged.Value, Failed.Value, Missing.Value, Restored.Value);
    }

    /// <summary>One scan's state: every folder begun so far (the last one in flight), the worker gates and the throttled progress sink.</summary>
    private sealed class ScanRun : IDisposable
    {
        private readonly TimeProvider _clock;
        private readonly IProgress<ScanProgress>? _progress;
        private readonly long _started;
        private readonly ConcurrentQueue<FolderRun> _folders = new();
        private long _lastReport = long.MinValue;
        private volatile FolderRun _folder = new(new LibraryFolderDto(0, string.Empty, true, null, null));
        private volatile string? _currentPath;
        private volatile ScanPhase _phase = ScanPhase.Enumerating;

        public ScanRun(TimeProvider clock, IProgress<ScanProgress>? progress, int readDegree)
        {
            _clock = clock;
            _progress = progress;
            _started = clock.GetTimestamp();
            ReadGate = new SemaphoreSlim(readDegree, readDegree);
        }

        /// <summary>Bounds concurrent tag reads to the read degree across every directory in flight.</summary>
        public SemaphoreSlim ReadGate { get; }

        /// <summary>The ExtractArt stage's two workers.</summary>
        public SemaphoreSlim ArtGate { get; } = new(2, 2);

        public ConcurrentQueue<ScanFailure> Failures { get; } = new();

        public void Dispose()
        {
            ReadGate.Dispose();
            ArtGate.Dispose();
        }

        /// <summary>The folder in flight.</summary>
        public FolderRun Folder => _folder;

        public ScanPhase Phase { get => _phase; set => _phase = value; }

        public string? CurrentPath { get => _currentPath; set => _currentPath = value; }

        public FolderRun BeginFolder(LibraryFolderDto folder)
        {
            var run = new FolderRun(folder);
            _folders.Enqueue(run);
            _folder = run;
            return run;
        }

        /// <summary>Reports if <see cref="ProgressInterval"/> has passed since the last report (or always when <paramref name="force"/>).</summary>
        public void Report(bool force = false)
        {
            if (_progress is null)
            {
                return;
            }

            long now = _clock.GetTimestamp();
            long last = Volatile.Read(ref _lastReport);
            if (!force)
            {
                if (last != long.MinValue && _clock.GetElapsedTime(last, now) < ProgressInterval)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _lastReport, now, last) != last)
                {
                    return; // another thread just reported
                }
            }
            else
            {
                Volatile.Write(ref _lastReport, now);
            }

            Totals t = Sum();
            _progress.Report(new ScanProgress(_phase, t.Seen, t.Processed, t.Added, t.Updated, t.Unchanged, t.Failed, t.SlowPath, _currentPath));
        }

        public ScanReport Finish(ScanOutcome outcome, string? error)
        {
            _phase = ScanPhase.Finished;
            _currentPath = null;
            Report(force: true);
            Totals t = Sum();
            return new ScanReport(
                outcome,
                _clock.GetElapsedTime(_started),
                t.Seen,
                t.Processed,
                t.Added,
                t.Updated,
                t.Unchanged,
                t.Failed,
                t.Missing,
                t.Restored,
                t.SlowPath,
                Failures.ToArray(),
                _folders.Where(f => f.Ended).Select(f => f.ToReport()).ToArray(),
                error);
        }

        private Totals Sum()
        {
            var t = default(Totals);
            foreach (FolderRun f in _folders)
            {
                t.Seen += f.Seen.Value;
                t.Processed += f.Processed.Value;
                t.Added += f.Added.Value;
                t.Updated += f.Updated.Value;
                t.Unchanged += f.Unchanged.Value;
                t.Failed += f.Failed.Value;
                t.SlowPath += f.SlowPath.Value;
                t.Missing += f.Missing.Value;
                t.Restored += f.Restored.Value;
            }

            return t;
        }

        private struct Totals
        {
            public int Seen;
            public int Processed;
            public int Added;
            public int Updated;
            public int Unchanged;
            public int Failed;
            public int SlowPath;
            public int Missing;
            public int Restored;
        }
    }
}
