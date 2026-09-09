using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>
/// The shell's scan triggers (docs/library-and-data.md, "Scheduling"; E3-S12): the launch scan 3 s after the
/// window is up, the scan of a folder just added, and Settings › Library's Rescan. One shell scan runs at a
/// time; a request while one is running is declined (the page already shows it). The watcher's own scans go
/// straight to the scanner, so a shell scan that arrives while one of those is running waits for it to end
/// rather than failing on the scanner's one-at-a-time rule. Whatever started a scan, <see cref="ScanCompleted"/>
/// on the scanner lands here: a report that changed rows bumps <see cref="LibraryVersion"/> and raises
/// <see cref="LibraryChanged"/>, which the pane turns into a refresh of the open view. Events are raised on the
/// UI thread when the coordinator was given its synchronization context, inline otherwise (tests).
/// </summary>
public sealed class LibraryScanCoordinator : IDisposable
{
    /// <summary>How long after the window shows the launch scan starts.</summary>
    public static readonly TimeSpan LaunchDelay = TimeSpan.FromSeconds(3);

    /// <summary>How often a waiting shell scan looks for the watcher's scan to end.</summary>
    public static readonly TimeSpan BusyPoll = TimeSpan.FromMilliseconds(250);

    private readonly ILibraryScanner _scanner;
    private readonly TimeProvider _clock;
    private readonly SynchronizationContext? _ui;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private CancellationTokenSource? _current;
    private long _version;
    private bool _disposed;

    public LibraryScanCoordinator(ILibraryScanner scanner, TimeProvider? clock = null, SynchronizationContext? ui = null, ILogger<LibraryScanCoordinator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        _scanner = scanner;
        _clock = clock ?? TimeProvider.System;
        _ui = ui;
        _logger = logger ?? NullLogger<LibraryScanCoordinator>.Instance;
        _scanner.ScanCompleted += OnScanCompleted;
    }

    /// <summary>Progress, running flag or last report changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>A scan (any origin) or a Settings action changed library rows; open views reload.</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>True while a shell scan (launch, folder, Rescan) is running or waiting to start.</summary>
    public bool IsScanning { get; private set; }

    /// <summary>The latest progress sample of the running shell scan; <c>null</c> when none runs.</summary>
    public ScanProgress? Progress { get; private set; }

    /// <summary>The last shell scan's report and when it ended.</summary>
    public ScanReport? LastReport { get; private set; }

    public DateTimeOffset? LastReportAt { get; private set; }

    /// <summary>Moves whenever <see cref="LibraryChanged"/> is raised; a cached page compares it with the version it loaded.</summary>
    public long LibraryVersion => Interlocked.Read(ref _version);

    /// <summary>Starts the launch scan after <see cref="LaunchDelay"/> (or <paramref name="delay"/>). Fire and forget; a failure is logged.</summary>
    public void StartLaunchScan(TimeSpan? delay = null)
    {
        _ = LaunchAsync(delay ?? LaunchDelay);
    }

    /// <summary>
    /// Runs <paramref name="request"/> as the shell's scan. Returns <c>null</c> when a shell scan is already
    /// running or the request was cancelled before it started; otherwise the report, whatever its outcome.
    /// </summary>
    public async Task<ScanReport?> ScanAsync(ScanRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_current is not null || _disposed)
            {
                return null;
            }

            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _current = cts;
        }

        Post(() =>
        {
            IsScanning = true;
            Progress = null;
            RaiseStateChanged();
        });

        ScanReport? report = null;
        try
        {
            report = await RunAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled while waiting for the watcher's scan; nothing ran.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogError(e, "Library scan failed to run");
            report = new ScanReport(ScanOutcome.Failed, TimeSpan.Zero, 0, 0, 0, 0, 0, 0, 0, 0, 0, [], [], e.Message);
        }
        finally
        {
            lock (_lock)
            {
                _current = null;
            }

            cts.Dispose();
            ScanReport? final = report;
            Post(() =>
            {
                IsScanning = false;
                Progress = null;
                if (final is not null)
                {
                    LastReport = final;
                    LastReportAt = _clock.GetUtcNow();
                }

                RaiseStateChanged();
            });
        }

        return report;
    }

    /// <summary>Cancels the running shell scan (the scanner keeps every batch that committed). No-op when none runs.</summary>
    public void Cancel()
    {
        lock (_lock)
        {
            _current?.Cancel();
        }
    }

    /// <summary>A Settings action (folder removed, missing tracks purged) changed rows without a scan.</summary>
    public void NotifyLibraryChanged()
    {
        Interlocked.Increment(ref _version);
        Post(() => LibraryChanged?.Invoke(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _current?.Cancel();
        }

        _scanner.ScanCompleted -= OnScanCompleted;
    }

    /// <summary>A report that added, updated, flagged or restored anything.</summary>
    public static bool ChangedRows(ScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Added + report.Updated + report.Missing + report.Restored > 0;
    }

    /// <summary>One line for the sidebar footer and the settings page: phase, counts and the file in hand.</summary>
    public static string Describe(ScanProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        string phase = progress.Phase switch
        {
            ScanPhase.Enumerating => "Looking for files",
            ScanPhase.Reading => "Reading tags",
            ScanPhase.MarkingMissing => "Checking for missing files",
            _ => "Finishing",
        };
        var parts = new List<string> { phase, N(progress.Seen) + " files seen" };
        if (progress.Processed > 0)
        {
            parts.Add(N(progress.Processed) + " read");
        }

        if (progress.Added > 0)
        {
            parts.Add(N(progress.Added) + " added");
        }

        if (progress.Updated > 0)
        {
            parts.Add(N(progress.Updated) + " updated");
        }

        if (progress.Failed > 0)
        {
            parts.Add(N(progress.Failed) + " failed");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>One line for the last report: outcome, elapsed and every non-zero count.</summary>
    public static string Describe(ScanReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string outcome = report.Outcome switch
        {
            ScanOutcome.Completed => "Completed",
            ScanOutcome.Cancelled => "Cancelled",
            _ => "Failed" + (report.Error is { Length: > 0 } error ? ": " + error : string.Empty),
        };
        var parts = new List<string> { outcome + " in " + Elapsed(report.Elapsed), N(report.Seen) + " files" };
        Add(parts, report.Added, "added");
        Add(parts, report.Updated, "updated");
        Add(parts, report.Unchanged, "unchanged");
        Add(parts, report.Failed, "failed");
        Add(parts, report.Missing, "missing");
        Add(parts, report.Restored, "restored");
        int offline = report.Folders.Count(f => f.Offline);
        if (offline > 0)
        {
            parts.Add(N(offline) + (offline == 1 ? " folder offline" : " folders offline"));
        }

        return string.Join(" · ", parts);
    }

    private static void Add(List<string> parts, int count, string what)
    {
        if (count > 0)
        {
            parts.Add(N(count) + " " + what);
        }
    }

    private static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

    private static string Elapsed(TimeSpan elapsed) => elapsed.TotalSeconds < 10
        ? elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture) + " s"
        : elapsed.TotalMinutes < 1
            ? elapsed.TotalSeconds.ToString("0", CultureInfo.CurrentCulture) + " s"
            : elapsed.ToString(@"m\:ss", CultureInfo.CurrentCulture) + " min";

    private async Task LaunchAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _clock).ConfigureAwait(false);
            ScanReport? report = await ScanAsync(ScanRequest.All).ConfigureAwait(false);
            if (report is not null)
            {
                _logger.LogInformation("Launch scan: {Summary}", Describe(report));
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogError(e, "Launch scan did not run");
        }
    }

    private async Task<ScanReport> RunAsync(ScanRequest request, CancellationToken ct)
    {
        var progress = new Relay(this);
        while (true)
        {
            // The watcher may be mid-scan; its pump waits for us the same way, so neither starves the other.
            while (_scanner.IsScanning)
            {
                await Task.Delay(BusyPoll, _clock, ct).ConfigureAwait(false);
            }

            try
            {
                return await _scanner.ScanAsync(request, progress, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The watcher got in between the check and the call; wait for it.
                await Task.Delay(BusyPoll, _clock, ct).ConfigureAwait(false);
            }
        }
    }

    private void OnScanCompleted(object? sender, ScanReport report)
    {
        if (ChangedRows(report))
        {
            NotifyLibraryChanged();
        }
    }

    private void OnProgress(ScanProgress sample)
    {
        Post(() =>
        {
            if (IsScanning)
            {
                Progress = sample;
                RaiseStateChanged();
            }
        });
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void Post(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            // No JoinableTaskFactory in this app; the context is the XAML thread's DispatcherQueue one and Post never blocks the caller.
#pragma warning disable VSTHRD001
            _ui.Post(_ => action(), null);
#pragma warning restore VSTHRD001
        }
    }

    /// <summary><see cref="Progress{T}"/> posts to whatever context created it; this one always goes through <see cref="Post"/>.</summary>
    private sealed class Relay : IProgress<ScanProgress>
    {
        private readonly LibraryScanCoordinator _owner;

        public Relay(LibraryScanCoordinator owner) => _owner = owner;

        public void Report(ScanProgress value) => _owner.OnProgress(value);
    }
}

/// <summary>Settings › Library › Add folder: the folder the user chose, or <c>null</c> when they cancelled.</summary>
public interface ILibraryFolderPicker
{
    Task<string?> PickFolderAsync(CancellationToken ct = default);
}
