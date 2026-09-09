using Tunqio.App.Library;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

/// <summary>E3-S12: the shell's scan triggers, their status, and the library-changed signal the views refresh from.</summary>
public class LibraryScanCoordinatorTests
{
    private readonly FakeScanner _scanner = new();

    [Fact]
    public async Task A_scan_reports_progress_then_its_report_and_a_second_request_meanwhile_is_declined_Async()
    {
        using var scans = new LibraryScanCoordinator(_scanner, new FixedClock(1_000));
        int stateChanges = 0;
        scans.StateChanged += (_, _) => stateChanges++;

        Task<ScanReport?> first = scans.ScanAsync(new ScanRequest(ForceReread: true));
        await WaitUntilAsync(() => _scanner.Requests.Count == 1);
        scans.IsScanning.Should().BeTrue();
        _scanner.Requests[0].ForceReread.Should().BeTrue();
        (await scans.ScanAsync(ScanRequest.All)).Should().BeNull("one shell scan at a time; the page shows the running one");
        _scanner.Requests.Should().HaveCount(1);

        _scanner.ReportProgress(new ScanProgress(ScanPhase.Reading, 120, 40, 30, 5, 80, 1, 0, @"D:\Music\x.flac"));
        scans.Progress!.Seen.Should().Be(120);
        LibraryScanCoordinator.Describe(scans.Progress).Should().Be("Reading tags · 120 files seen · 40 read · 30 added · 5 updated · 1 failed");

        ScanReport report = FakeScanner.Report(added: 30, updated: 5, unchanged: 80, failed: 1);
        _scanner.Finish(report);
        (await first).Should().BeSameAs(report);
        scans.IsScanning.Should().BeFalse();
        scans.Progress.Should().BeNull();
        scans.LastReport.Should().BeSameAs(report);
        scans.LastReportAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_000));
        stateChanges.Should().BeGreaterThanOrEqualTo(3, "started, progressed, finished");
        LibraryScanCoordinator.Describe(report).Should().Be("Completed in 1.5 s · 116 files · 30 added · 5 updated · 80 unchanged · 1 failed");

        Task<ScanReport?> second = scans.ScanAsync(ScanRequest.All);
        await WaitUntilAsync(() => _scanner.Requests.Count == 2);
        _scanner.Finish(FakeScanner.Report());
        (await second).Should().NotBeNull("the slot is free again");
    }

    [Fact]
    public async Task A_shell_scan_waits_for_the_watchers_scan_and_retries_a_refusal_Async()
    {
        using var scans = new LibraryScanCoordinator(_scanner);
        _scanner.IsScanning = true; // the watcher is mid-scan
        _scanner.RefuseNext = true; // and gets in again between our check and our call

        Task<ScanReport?> pending = scans.ScanAsync(ScanRequest.All);
        await Task.Delay(LibraryScanCoordinator.BusyPoll * 2);
        _scanner.Requests.Should().BeEmpty("nothing is asked of a busy scanner");
        scans.IsScanning.Should().BeTrue("the shell scan is queued, so the page shows it as running");

        _scanner.IsScanning = false;
        await WaitUntilAsync(() => _scanner.Requests.Count == 1);
        _scanner.RefuseNext.Should().BeFalse("the first attempt was refused and retried");
        _scanner.Finish(FakeScanner.Report());
        (await pending)!.Outcome.Should().Be(ScanOutcome.Completed);
    }

    [Fact]
    public async Task Cancel_ends_the_running_scan_with_a_cancelled_report_Async()
    {
        using var scans = new LibraryScanCoordinator(_scanner);
        Task<ScanReport?> pending = scans.ScanAsync(ScanRequest.All);
        await WaitUntilAsync(() => _scanner.Requests.Count == 1);

        scans.Cancel();

        (await pending)!.Outcome.Should().Be(ScanOutcome.Cancelled);
        scans.IsScanning.Should().BeFalse();
        scans.LastReport!.Outcome.Should().Be(ScanOutcome.Cancelled);
        scans.Cancel(); // nothing running: no-op
    }

    [Fact]
    public void Any_scan_that_changed_rows_moves_the_version_and_raises_library_changed()
    {
        using var scans = new LibraryScanCoordinator(_scanner);
        int raised = 0;
        scans.LibraryChanged += (_, _) => raised++;
        long before = scans.LibraryVersion;

        _scanner.Complete(FakeScanner.Report(unchanged: 500)); // the watcher's scan found nothing new
        raised.Should().Be(0);
        scans.LibraryVersion.Should().Be(before);

        _scanner.Complete(FakeScanner.Report(missing: 1));
        raised.Should().Be(1);
        scans.LibraryVersion.Should().Be(before + 1);

        scans.NotifyLibraryChanged(); // a folder removed, a purge
        raised.Should().Be(2);
        scans.LibraryVersion.Should().Be(before + 2);
    }

    [Fact]
    public async Task The_launch_scan_covers_every_folder_after_its_delay_Async()
    {
        using var scans = new LibraryScanCoordinator(_scanner);

        scans.StartLaunchScan(TimeSpan.Zero);

        await WaitUntilAsync(() => _scanner.Requests.Count == 1);
        _scanner.Requests[0].Should().Be(ScanRequest.All);
        _scanner.Finish(FakeScanner.Report(added: 3));
        await WaitUntilAsync(() => scans.LastReport is not null);
        scans.LibraryVersion.Should().Be(1);
    }

    [Fact]
    public void Reports_describe_their_outcome_and_only_the_counts_that_moved()
    {
        LibraryScanCoordinator.Describe(FakeScanner.Report(ScanOutcome.Cancelled, added: 2)).Should().Be("Cancelled in 1.5 s · 2 files · 2 added");
        LibraryScanCoordinator.Describe(FakeScanner.Report(ScanOutcome.Failed, error: "disk full")).Should().Be("Failed: disk full in 1.5 s · 0 files");
        var offline = new ScanReport(ScanOutcome.Completed, TimeSpan.FromSeconds(75), 0, 0, 0, 0, 0, 0, 12, 0, 0, [], [new ScanFolderReport(1, @"Z:\", true, 0, 0, 0, 0, 0, 12, 0)]);
        LibraryScanCoordinator.Describe(offline).Should().Be("Completed in 1:15 min · 0 files · 12 missing · 1 folder offline");
        LibraryScanCoordinator.ChangedRows(offline).Should().BeTrue();
        LibraryScanCoordinator.ChangedRows(FakeScanner.Report(unchanged: 9)).Should().BeFalse();
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the condition should hold within two seconds");
    }
}

/// <summary>A clock that stands still; timers are the system's.</summary>
internal sealed class FixedClock(long unixMilliseconds) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
}
