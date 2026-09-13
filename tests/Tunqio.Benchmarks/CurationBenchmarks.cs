using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.Library.Database;
using Tunqio.Library.Repositories;

namespace Tunqio.Benchmarks;

/// <summary>
/// E5-S4 gate, AC-140: "dragging 500 selected tracks completes in &lt; 1 s". The drop is <see cref="PlaylistEditor.AddAsync"/>:
/// the whole list written back in one transaction, then read back with its 500 new rows resolved to tracks, on the 100k
/// library, into a playlist that already holds 500 so the rewrite is of 1000 rows and not a toy.
/// <para>
/// This is the data half of the second. The other half is the target list rebinding 1000 rows, which is virtualised and
/// realises only the rows in view; the pane logs the whole gesture ("Curation added 500 track(s) by drag in N ms") for a
/// live run to read, because a benchmark cannot hold a XAML list.
/// </para>
/// <para>
/// Measured on the dev machine when this was added (2026-09-13): median 17.1 ms, p95 26.1 ms, max 32.1 ms, n = 30. The
/// 500 ms budget is half the criterion's second, left for the rebind, and fails a drop whose write or read-back has
/// regressed by an order of magnitude; it is not a claim that 500 ms is what the data half needs.
/// </para>
/// <para>
/// The WAL is checkpointed after every iteration and the playlist reset to its 500 before the next, neither timed, for
/// the reason <see cref="UpsertBenchmarks"/> gives: otherwise one iteration in several pays for everyone's writes.
/// </para>
/// </summary>
[Config(typeof(CurationConfig))]
#pragma warning disable CA1001 // BenchmarkDotNet's [GlobalCleanup] is the teardown; it disposes the editor.
public class CurationBenchmarks
#pragma warning restore CA1001
{
    private string _path = null!;
    private string _temporary = null!;
    private LibraryDatabase _db = null!;
    private SqlitePlaylistRepository _playlists = null!;
    private PlaylistEditor _editor = null!;
    private long _playlist;
    private long[] _existing = null!;
    private long[] _dragged = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        (_path, _temporary) = BenchmarkLibrary.AcquireWritable(Console.Out);
        _db = LibraryDatabase.Open(_path);
        var tracks = new SqliteTrackRepository(_db);
        _playlists = new SqlitePlaylistRepository(_db, tracks);
        long[] ids = [.. (await tracks.ListAsync(new TrackQuery(TrackSort.Album, PageSize: 1000))).Select(t => t.Id)];
        _existing = ids[..500];
        _dragged = ids[500..1000];
        _playlist = (await _playlists.CreateAsync("Curation benchmark")).Id;
        _editor = new PlaylistEditor(_playlists);
    }

    /// <summary>Not timed: back to the 500 it started with, opened fresh, so every iteration is the same drop.</summary>
    [IterationSetup]
    public void Reset()
    {
#pragma warning disable VSTHRD002 // BenchmarkDotNet's iteration hooks are synchronous; nothing else is running
        _playlists.ReplaceTracksAsync(_playlist, _existing).GetAwaiter().GetResult();
        _editor.OpenAsync(_playlist).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    [IterationCleanup]
    public void Checkpoint()
    {
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        command.ExecuteNonQuery();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _editor.Dispose();
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        BenchmarkLibrary.Delete(_temporary);
    }

    [Benchmark(Description = "PlaylistEditor.AddAsync, 500 tracks dropped mid-way into a 500-track playlist, 100k library")]
    [Budget(500)]
    public Task<bool> DropFiveHundredAsync() => _editor.AddAsync(_dragged, position: 250);

    private sealed class CurationConfig : ManualConfig
    {
        public CurationConfig() => LibraryLatencyJob.Apply(this, 30);
    }
}
