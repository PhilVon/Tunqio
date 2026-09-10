using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.Core.Library;

namespace Tunqio.Library.Scanning;

/// <summary>The watcher's tunables; the defaults are the documented ones (docs/library-and-data.md "Live updates").</summary>
public sealed record LibraryWatcherOptions
{
    public static LibraryWatcherOptions Default { get; } = new();

    /// <summary>How long a path must be quiet before it is scanned; every event on it restarts the clock.</summary>
    public TimeSpan Debounce { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The watch's kernel buffer. Windows accepts 4 KB to 64 KB; a full buffer is an overflow, answered with a folder rescan.</summary>
    public int BufferSize { get; init; } = 64 * 1024;

    /// <summary>Pending paths per folder beyond which the set collapses into one folder rescan (a mass copy is cheaper to diff than to list).</summary>
    public int MaxPendingPaths { get; init; } = 4096;
}

/// <summary>
/// <see cref="ILibraryWatcher"/> over one <see cref="IFolderWatchSource"/> watch per enabled folder, feeding
/// <see cref="ILibraryScanner"/> with targeted requests (<see cref="ScanRequest.Targeted"/>).
/// <list type="bullet">
/// <item>Events are keyed by path per folder with a due time of now + <see cref="LibraryWatcherOptions.Debounce"/>;
/// a repeat on the same path moves its due time (debounce), so a file being written is read once it has been
/// quiet, and paths due together travel in one request (coalescing).</item>
/// <item>A rename is its old path and its new one; the scanner marks the old rows missing and adds the new
/// file, which is the documented remove-plus-add.</item>
/// <item>A supported file is always taken; a deleted or renamed-away path is always taken because it may have
/// been a directory (a subtree moved out gives one event for the directory only); a created or renamed-in
/// directory is taken; everything else (other files, a directory's own Changed event, which every child event
/// already implies) is ignored.</item>
/// <item>A watch error, which is the buffer overflowing under a mass copy, drops the folder's pending paths and
/// queues one rescan of the whole folder; so does a pending set past <see cref="LibraryWatcherOptions.MaxPendingPaths"/>.
/// Upserts are keyed by path, so the rescan and any targeted scan that already ran never duplicate a row.</item>
/// <item>One pump runs the scans in turn. While another scan is running (the launch scan, Settings) the work
/// waits and nothing is dropped: that is what "paused during a full scan" means here.</item>
/// </list>
/// </summary>
public sealed class LibraryWatcher : ILibraryWatcher
{
    private readonly ILibraryScanner _scanner;
    private readonly ILibraryFolderRepository _folders;
    private readonly LibraryWatcherOptions _options;
    private readonly IFolderWatchSource _source;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<long, FolderWatch> _watches = new();
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly TimeSpan _busyPoll;
    private CancellationTokenSource? _stopping;
    private Task? _pump;
    private long _events;
    private long _ignored;
    private long _scans;
    private long _overflows;
    private long _collapses;
    private long _failures;
    private bool _disposed;

    public LibraryWatcher(ILibraryScanner scanner, ILibraryFolderRepository folders, LibraryWatcherOptions? options = null, TimeProvider? clock = null, ILogger? logger = null)
        : this(scanner, folders, options, FileSystemWatchSource.Instance, clock, logger)
    {
    }

    internal LibraryWatcher(ILibraryScanner scanner, ILibraryFolderRepository folders, LibraryWatcherOptions? options, IFolderWatchSource source, TimeProvider? clock, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(source);
        _scanner = scanner;
        _folders = folders;
        _options = options ?? LibraryWatcherOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.Debounce, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxPendingPaths, 1, nameof(options));
        _source = source;
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _busyPoll = TimeSpan.FromMilliseconds(Math.Clamp(_options.Debounce.TotalMilliseconds / 4, 10, 250));
        _scanner.ScanCompleted += OnScanCompleted;
    }

    public bool IsWatching
    {
        get
        {
            lock (_lock)
            {
                return _pump is not null;
            }
        }
    }

    public LibraryWatcherStats Stats
    {
        get
        {
            lock (_lock)
            {
                int pending = 0;
                foreach (FolderWatch watch in _watches.Values)
                {
                    pending += watch.Pending.Count;
                }

                return new LibraryWatcherStats(_watches.Count, pending, _events, _ignored, _scans, _overflows, _collapses, _failures);
            }
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            if (_pump is null)
            {
                _stopping = new CancellationTokenSource();
                CancellationToken token = _stopping.Token;
                _pump = Task.Run(() => PumpAsync(token), CancellationToken.None);
            }
        }

        await RefreshAsync(ct).ConfigureAwait(false);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        CancellationTokenSource? stopping;
        lock (_lock)
        {
            stopping = _stopping;
        }

        if (stopping is null)
        {
            return;
        }

        IReadOnlyList<LibraryFolderDto> folders = await _folders.ListAsync(ct).ConfigureAwait(false);
        var dropped = new List<FolderWatch>();
        lock (_lock)
        {
            if (!ReferenceEquals(_stopping, stopping))
            {
                return; // stopped meanwhile
            }

            var wanted = folders.Where(f => f.Enabled).ToDictionary(f => f.Id);
            foreach ((long id, FolderWatch watch) in _watches.ToList())
            {
                if (!wanted.TryGetValue(id, out LibraryFolderDto? folder) || !string.Equals(folder.Path, watch.Folder.Path, StringComparison.OrdinalIgnoreCase))
                {
                    _watches.Remove(id);
                    dropped.Add(watch);
                }
            }

            foreach (LibraryFolderDto folder in wanted.Values)
            {
                if (!_watches.ContainsKey(folder.Id))
                {
                    TryWatch(folder);
                }
            }
        }

        foreach (FolderWatch watch in dropped)
        {
            watch.Handle.Dispose();
            _logger.LogInformation("Stopped watching {Path}", watch.Folder.Path);
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? stopping;
        Task? pump;
        List<FolderWatch> watches;
        lock (_lock)
        {
            stopping = _stopping;
            pump = _pump;
            _stopping = null;
            _pump = null;
            watches = _watches.Values.ToList();
            _watches.Clear();
        }

        foreach (FolderWatch watch in watches)
        {
            watch.Handle.Dispose();
        }

        if (stopping is null)
        {
            return;
        }

        await stopping.CancelAsync().ConfigureAwait(false);
        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // the pump ends by cancellation
            }
        }

        stopping.Dispose();
        _logger.LogInformation("Library watcher stopped after {Scans} scan(s), {Overflows} overflow(s)", Volatile.Read(ref _scans), Volatile.Read(ref _overflows));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _scanner.ScanCompleted -= OnScanCompleted;
        _ = StopAsync();
    }

    /// <summary>Creates the watch for a folder; called under the lock. A root that is not there gets no watch until the next refresh.</summary>
    private void TryWatch(LibraryFolderDto folder)
    {
        if (!Directory.Exists(folder.Path))
        {
            _logger.LogWarning("Library folder {Path} is not there; not watching it until it comes back", folder.Path);
            return;
        }

        try
        {
            long id = folder.Id;
            IDisposable handle = _source.Watch(
                folder.Path,
                _options.BufferSize,
                (kind, path, oldPath) => OnChange(id, kind, path, oldPath),
                e => OnError(id, e));
            _watches[id] = new FolderWatch(folder, handle);
            _logger.LogInformation("Watching {Path}", folder.Path);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogWarning(e, "Could not watch library folder {Path}", folder.Path);
        }
    }

    private void OnChange(long folderId, FolderChangeKind kind, string path, string? oldPath)
    {
        bool accepted = false;
        if (kind == FolderChangeKind.Renamed && oldPath is not null)
        {
            accepted |= Touch(folderId, oldPath);
        }

        if (Accepts(kind, path))
        {
            accepted |= Touch(folderId, path);
        }
        else
        {
            Interlocked.Increment(ref _ignored);
        }

        if (accepted)
        {
            Signal();
        }
    }

    /// <summary>The filter described on the class: supported files always, gone paths always, new directories, nothing else.</summary>
    private static bool Accepts(FolderChangeKind kind, string path)
    {
        if (kind is FolderChangeKind.Deleted)
        {
            return true;
        }

        if (AudioFormats.IsSupported(path))
        {
            return true;
        }

        return kind is FolderChangeKind.Created or FolderChangeKind.Renamed && Directory.Exists(path);
    }

    private bool Touch(long folderId, string path)
    {
        long due = Due(_clock.GetTimestamp());
        lock (_lock)
        {
            if (!_watches.TryGetValue(folderId, out FolderWatch? watch))
            {
                return false;
            }

            _events++;
            if (watch.RescanDue is not null)
            {
                watch.RescanDue = due; // the folder is being rescanned anyway; let it settle first
                return true;
            }

            watch.Pending[path] = due;
            if (watch.Pending.Count > _options.MaxPendingPaths)
            {
                _collapses++;
                _logger.LogInformation("{Count} paths pending under {Path}; collapsing into a folder rescan", watch.Pending.Count, watch.Folder.Path);
                watch.Pending.Clear();
                watch.RescanDue = due;
            }

            return true;
        }
    }

    private void OnError(long folderId, Exception error)
    {
        long due = Due(_clock.GetTimestamp());
        lock (_lock)
        {
            if (!_watches.TryGetValue(folderId, out FolderWatch? watch))
            {
                return;
            }

            _overflows++;
            _logger.LogWarning(error, "Watch on {Path} reported an error ({Kind}); its pending paths are dropped and the folder will be rescanned", watch.Folder.Path, error.GetType().Name);
            watch.Pending.Clear();
            watch.RescanDue = due;
        }

        Signal();
    }

    private void OnScanCompleted(object? sender, ScanReport report) => Signal();

    private void Signal() => _signal.Writer.TryWrite(true);

    private long Due(long now) => now + (long)(_options.Debounce.TotalSeconds * _clock.TimestampFrequency);

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (await _signal.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                _signal.Reader.TryRead(out _);
                await DrainAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopped
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogError(e, "Library watcher stopped on an unexpected error; changes are no longer applied until the next launch");
        }
    }

    /// <summary>Runs every due piece of work, sleeping until the next one is due, until nothing is pending.</summary>
    private async Task DrainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Work? work = TakeDue(_scanner.IsScanning, out TimeSpan? wait);
            if (work is null)
            {
                if (wait is null)
                {
                    return;
                }

                await Task.Delay(wait.Value, _clock, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                ScanReport report = await _scanner.ScanAsync(work.Request, null, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _scans);
                if (report.Outcome == ScanOutcome.Failed)
                {
                    Interlocked.Increment(ref _failures);
                    _logger.LogWarning("Watcher scan of folder {FolderId} failed: {Error}", work.FolderId, report.Error);
                }
                else
                {
                    _logger.LogDebug(
                        "Watcher scan of folder {FolderId} ({What}): {Added} added, {Updated} updated, {Missing} missing, {Restored} restored",
                        work.FolderId, work.Request.IsTargeted ? work.Request.Paths!.Count + " path(s)" : "whole folder", report.Added, report.Updated, report.Missing, report.Restored);
                }
            }
            catch (InvalidOperationException)
            {
                // Another scan started between the check and the call; try again once it has ended.
                Requeue(work);
                await Task.Delay(_busyPoll, _clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Interlocked.Increment(ref _failures);
                _logger.LogWarning(e, "Watcher scan of folder {FolderId} threw; its paths are dropped", work.FolderId);
            }
        }
    }

    /// <summary>
    /// One due piece of work (a folder rescan first, else the folder's due paths), or the time until the earliest
    /// one. When <paramref name="busy"/> (another scan is running), due work is left where it is rather than taken
    /// and put back, so it is never briefly neither pending nor running; the caller comes back in a busy poll.
    /// </summary>
    private Work? TakeDue(bool busy, out TimeSpan? wait)
    {
        long now = _clock.GetTimestamp();
        long? earliest = null;
        lock (_lock)
        {
            foreach (FolderWatch watch in _watches.Values)
            {
                if (watch.RescanDue is { } rescan)
                {
                    if (rescan <= now)
                    {
                        if (busy)
                        {
                            wait = _busyPoll;
                            return null;
                        }

                        watch.RescanDue = null;
                        watch.Pending.Clear();
                        wait = null;
                        return new Work(watch.Folder.Id, ScanRequest.Folder(watch.Folder.Id));
                    }

                    earliest = Math.Min(earliest ?? rescan, rescan);
                    continue;
                }

                if (watch.Pending.Count == 0)
                {
                    continue;
                }

                List<string>? ready = null;
                foreach ((string path, long due) in watch.Pending)
                {
                    if (due <= now)
                    {
                        (ready ??= []).Add(path);
                    }
                    else
                    {
                        earliest = Math.Min(earliest ?? due, due);
                    }
                }

                if (ready is not null)
                {
                    if (busy)
                    {
                        wait = _busyPoll;
                        return null;
                    }

                    foreach (string path in ready)
                    {
                        watch.Pending.Remove(path);
                    }

                    wait = null;
                    return new Work(watch.Folder.Id, ScanRequest.Targeted(watch.Folder.Id, ready));
                }
            }
        }

        if (earliest is null)
        {
            wait = null;
            return null;
        }

        TimeSpan until = _clock.GetElapsedTime(now, earliest.Value);
        wait = until > TimeSpan.FromMilliseconds(1) ? until : TimeSpan.FromMilliseconds(1);
        return null;
    }

    /// <summary>Puts work back, due now, for after a scan that started between the check and the call.</summary>
    private void Requeue(Work work)
    {
        long now = _clock.GetTimestamp();
        lock (_lock)
        {
            if (!_watches.TryGetValue(work.FolderId, out FolderWatch? watch))
            {
                return; // the folder went away; its work goes with it
            }

            if (!work.Request.IsTargeted)
            {
                watch.RescanDue = Math.Min(watch.RescanDue ?? now, now);
                return;
            }

            if (watch.RescanDue is not null)
            {
                return; // covered by the rescan
            }

            foreach (string path in work.Request.Paths!)
            {
                watch.Pending.TryAdd(path, now);
            }
        }
    }

    private sealed record Work(long FolderId, ScanRequest Request);

    /// <summary>One folder's watch and what is waiting on it.</summary>
    private sealed class FolderWatch(LibraryFolderDto folder, IDisposable handle)
    {
        public LibraryFolderDto Folder { get; } = folder;

        public IDisposable Handle { get; } = handle;

        /// <summary>Path to the timestamp it is due at.</summary>
        public Dictionary<string, long> Pending { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>When a whole-folder rescan is due; set by an overflow or a collapse, and it supersedes the pending paths.</summary>
        public long? RescanDue { get; set; }
    }
}
