using System.Collections.Concurrent;
using System.Diagnostics;
using Tunqio.Core.Library;
using Tunqio.Library.Scanning;

namespace Tunqio.Library.Tests.Scanning;

/// <summary>
/// A <see cref="LibraryWatcher"/> over a <see cref="ScanHarness"/>: the scanner is wrapped so tests can see the
/// requests the watcher made and make a scan fail, and the file-system watch is a fake the test raises events
/// on. A short debounce keeps the tests quick; the documented 2 s is exercised by the integration tests.
/// </summary>
internal sealed class WatchHarness : IDisposable
{
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);

    private WatchHarness(ScanHarness scan, RecordingScanner scanner, FakeWatchSource source, LibraryWatcher watcher)
    {
        Scan = scan;
        Scanner = scanner;
        Source = source;
        Watcher = watcher;
    }

    public ScanHarness Scan { get; }

    public RecordingScanner Scanner { get; }

    public FakeWatchSource Source { get; }

    public LibraryWatcher Watcher { get; }

    public static async Task<WatchHarness> CreateAsync(bool copyFixtures = true, int maxPendingPaths = 4096, bool initialScan = true)
    {
        ScanHarness scan = await ScanHarness.CreateAsync(copyFixtures);
        if (initialScan)
        {
            await scan.ScanAsync();
        }

        var scanner = new RecordingScanner(scan.Scanner);
        var source = new FakeWatchSource();
        var watcher = new LibraryWatcher(scanner, scan.Service.Folders, new LibraryWatcherOptions { Debounce = Debounce, MaxPendingPaths = maxPendingPaths }, source, clock: null, logger: null);
        return new WatchHarness(scan, scanner, source, watcher);
    }

    /// <summary>Waits until <paramref name="count"/> watcher scans have completed.</summary>
    public Task WaitForScansAsync(int count, TimeSpan? timeout = null) =>
        WaitUntilAsync(() => Scanner.Completed >= count, timeout ?? TimeSpan.FromSeconds(30), $"{count} scan(s); saw {Scanner.Completed}");

    /// <summary>
    /// Fails the moment <paramref name="condition"/> stops holding, sampling until <paramref name="duration"/> is
    /// up. For claims that must be true throughout a window rather than true at one sampled instant.
    /// </summary>
    public static async Task StaysTrueAsync(Func<bool> condition, TimeSpan duration, Func<string> what)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        do
        {
            if (!condition())
            {
                throw new InvalidOperationException($"After {elapsed.ElapsedMilliseconds} ms this stopped holding: {what()}");
            }

            await Task.Delay(10);
        }
        while (elapsed.Elapsed < duration);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > timeout)
            {
                throw new TimeoutException($"Waited {timeout.TotalSeconds:0} s for {what}");
            }

            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        Watcher.Dispose();
        Scan.Dispose();
    }

    /// <summary>Passes scans through and records them; a test can make the next one throw.</summary>
    public sealed class RecordingScanner(ILibraryScanner inner) : ILibraryScanner
    {
        private int _completed;
        private Exception? _failNext;

        public ConcurrentQueue<ScanRequest> Requests { get; } = new();

        public ConcurrentQueue<ScanReport> Reports { get; } = new();

        /// <summary>Thrown by the next <see cref="ScanAsync"/> instead of scanning; consumed once.</summary>
        public Exception? FailNext
        {
            get => Volatile.Read(ref _failNext);
            set => Volatile.Write(ref _failNext, value);
        }

        /// <summary>Scans that returned (thrown ones excluded).</summary>
        public int Completed => Volatile.Read(ref _completed);

        public bool IsScanning => inner.IsScanning;

        public event EventHandler<ScanReport>? ScanCompleted
        {
            add => inner.ScanCompleted += value;
            remove => inner.ScanCompleted -= value;
        }

        public async Task<ScanReport> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            if (Interlocked.Exchange(ref _failNext, null) is { } fault)
            {
                throw fault;
            }

            ScanReport report = await inner.ScanAsync(request, progress, ct);
            Reports.Enqueue(report);
            Interlocked.Increment(ref _completed);
            return report;
        }
    }

    /// <summary>The watch the test raises events on.</summary>
    public sealed class FakeWatchSource : IFolderWatchSource
    {
        public List<Handle> Watches { get; } = new();

        public IEnumerable<Handle> Live => Watches.Where(w => !w.Disposed);

        public Handle Single => Live.Single();

        public IDisposable Watch(string root, int bufferSize, Action<FolderChangeKind, string, string?> onChange, Action<Exception> onError)
        {
            var watch = new Handle(root, bufferSize, onChange, onError);
            lock (Watches)
            {
                Watches.Add(watch);
            }

            return watch;
        }

        public void Raise(FolderChangeKind kind, string path, string? oldPath = null) => Single.OnChange(kind, path, oldPath);

        public void Fail(Exception error) => Single.OnError(error);

        public sealed class Handle(string root, int bufferSize, Action<FolderChangeKind, string, string?> onChange, Action<Exception> onError) : IDisposable
        {
            public string Root { get; } = root;

            public int BufferSize { get; } = bufferSize;

            public Action<FolderChangeKind, string, string?> OnChange { get; } = onChange;

            public Action<Exception> OnError { get; } = onError;

            public bool Disposed { get; private set; }

            public void Dispose() => Disposed = true;
        }
    }
}
