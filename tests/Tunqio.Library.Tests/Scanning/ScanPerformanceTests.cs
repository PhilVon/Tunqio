using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.FixtureGen;
using Tunqio.Library.Database;
using Tunqio.Library.Repositories;
using Xunit.Abstractions;

namespace Tunqio.Library.Tests.Scanning;

/// <summary>
/// E3-S5 gate (AC-91): 10k files scan in under 90 s and a second scan with nothing changed finishes in under
/// 10 s. The tree is generated here (10k tagged WAVs, a few hundred bytes each, which takes longer to write
/// than to scan), against a file database so the writer pays for real fsyncs. The numbers are stated for the
/// reference machine (T-90); the assertions hold the documented budgets, the output line records what this
/// machine did.
/// </summary>
public sealed class ScanPerformanceTests(ITestOutputHelper output) : IDisposable
{
    private const int Files = 10_000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunqio-scan-perf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Ten_thousand_files_scan_in_under_90s_and_rescan_unchanged_in_under_10s_Async()
    {
        string tree = Path.Combine(_root, "music");
        Stopwatch generate = Stopwatch.StartNew();
        ScanTreeBuilder.Build(tree, Files);
        generate.Stop();

        var paths = new AppPaths(Path.Combine(_root, "app"));
        paths.EnsureCreated();
        using LibraryDatabase db = LibraryDatabase.Open(paths);
        var service = new LibraryService(db);
        await service.Folders.AddAsync(tree);

        Stopwatch first = Stopwatch.StartNew();
        ScanReport initial = await service.Scanner.ScanAsync(ScanRequest.All);
        first.Stop();
        Stopwatch second = Stopwatch.StartNew();
        ScanReport rescan = await service.Scanner.ScanAsync(ScanRequest.All);
        second.Stop();

        output.WriteLine($"generate {generate.Elapsed.TotalSeconds:0.0} s; first scan {first.Elapsed.TotalSeconds:0.0} s ({Files / first.Elapsed.TotalSeconds:0} files/s, {initial.Failed} failed); unchanged rescan {second.Elapsed.TotalSeconds:0.00} s; read degree {Tunqio.Library.Scanning.LibraryScanner.DefaultReadDegree}");

        initial.Outcome.Should().Be(ScanOutcome.Completed);
        initial.Added.Should().Be(Files);
        initial.Failed.Should().Be(0);
        (await service.Tracks.CountAsync(new TrackQuery())).Should().Be(Files);
        first.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(90), $"first scan of {Files} files took {first.Elapsed.TotalSeconds:0.0} s");

        rescan.Outcome.Should().Be(ScanOutcome.Completed);
        rescan.Unchanged.Should().Be(Files);
        rescan.Processed.Should().Be(0);
        second.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), $"unchanged rescan of {Files} files took {second.Elapsed.TotalSeconds:0.00} s");
    }
}
