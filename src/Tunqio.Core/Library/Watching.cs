namespace Tunqio.Core.Library;

/// <summary>
/// Live updates (E3-S6; docs/library-and-data.md "Live updates"): one file-system watch per enabled library
/// folder, feeding the scanner. Events are debounced per path and coalesced, a rename is its old path and its
/// new one, and a watch that overflowed (or died) has its whole folder rescanned. The watcher never scans while
/// another scan is running: its work waits and runs when that scan ends, so nothing is dropped. Start it once
/// the shell is up; call <see cref="RefreshAsync"/> after Settings changes the folder list.
/// </summary>
public interface ILibraryWatcher : IDisposable
{
    /// <summary>True between <see cref="StartAsync"/> and <see cref="StopAsync"/>.</summary>
    bool IsWatching { get; }

    /// <summary>Counters for the diagnostics page and tests.</summary>
    LibraryWatcherStats Stats { get; }

    /// <summary>Starts a watch on every enabled folder whose root exists. Idempotent.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Re-reads the folder list and adds, keeps or drops watches to match it. A no-op before <see cref="StartAsync"/>.</summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>Stops every watch and drops the pending work; a scan in flight is cancelled (it leaves the database consistent).</summary>
    Task StopAsync();
}

/// <summary>What the watcher has done so far.</summary>
/// <param name="Folders">Folders with a live watch.</param>
/// <param name="Pending">Paths waiting for their debounce to elapse.</param>
/// <param name="Events">File-system events accepted (a rename counts twice).</param>
/// <param name="Ignored">Events dropped for a file the library does not index.</param>
/// <param name="Scans">Scans the watcher ran, targeted and whole-folder together.</param>
/// <param name="Overflows">Watch buffer overflows (and other watch errors), each answered with a folder rescan.</param>
/// <param name="Collapses">Times the pending set grew past its cap and became a folder rescan.</param>
/// <param name="Failures">Scans that ended in an error; the paths were logged and dropped.</param>
public sealed record LibraryWatcherStats(int Folders, int Pending, long Events, long Ignored, long Scans, long Overflows, long Collapses, long Failures)
{
    public static LibraryWatcherStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);
}
