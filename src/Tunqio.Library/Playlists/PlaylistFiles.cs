using System.Text;
using Microsoft.Extensions.Logging;
using Tunqio.Core.Library;

namespace Tunqio.Library.Playlists;

/// <summary>
/// <see cref="IPlaylistFiles"/> over the playlist and track repositories, plus the auto-export (E6-S2,
/// docs/library-and-data.md, "Durability"): every playlist change is written to <c>exports\playlists</c> within
/// <see cref="DefaultExportWindow"/>, so a database reset loses no playlist that Import playlists from exports cannot
/// bring back.
/// </summary>
/// <remarks>
/// <para>
/// <b>A window, not a debounce.</b> The first change starts the clock and every change inside the window joins the same
/// write. A debounce that restarts on each change would let a long editing session in Curation postpone the export
/// indefinitely, and the promise is that every change is on disk within five seconds of being made (AC-145).
/// </para>
/// <para>
/// <b>Auto-exports use full paths.</b> The data root is on the system drive and music usually is not, so a relative path
/// from one to the other mostly does not exist, and where it does it breaks when either moves. Exports the user asks for
/// are written relative to their own folder instead (<see cref="M3u8.Write"/>).
/// </para>
/// <para>
/// <b>Nothing is deleted that this session did not write.</b> A renamed or deleted playlist's old file goes, but only
/// when this process wrote it. Sweeping the folder for files no playlist matches would be tidier and would, on the
/// first launch after a database reset, delete every export before anyone could import them.
/// </para>
/// </remarks>
public sealed class PlaylistFiles : IPlaylistFiles, IDisposable
{
    /// <summary>How long after a change its export is written: inside AC-145's five seconds with room for the write.</summary>
    public static readonly TimeSpan DefaultExportWindow = TimeSpan.FromSeconds(4);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IPlaylistRepository _playlists;
    private readonly ITrackRepository _tracks;
    private readonly TimeSpan _window;
    private readonly TimeProvider _clock;
    private readonly ILogger<PlaylistFiles>? _logger;
    private readonly object _gate = new();
    private readonly HashSet<long> _pending = [];
    private readonly SemaphoreSlim _writing = new(1, 1);

    // Guarded by _writing: the file each playlist's auto-export was last written to by this process.
    private readonly Dictionary<long, string> _written = [];
    private ITimer? _timer;
    private bool _started;
    private bool _disposed;

    /// <param name="playlists">The playlists; its <see cref="IPlaylistRepository.Changed"/> drives the auto-export.</param>
    /// <param name="tracks">Matches an imported path to a library track.</param>
    /// <param name="exportsDirectory">Where auto-exports go (<c>IAppPaths.ExportsDirectory</c>).</param>
    /// <param name="exportWindow">How long after a change the export is written.</param>
    /// <param name="clock">Runs the window.</param>
    /// <param name="logger">Export and import failures; they are logged, not thrown, from the background write.</param>
    public PlaylistFiles(
        IPlaylistRepository playlists,
        ITrackRepository tracks,
        string exportsDirectory,
        TimeSpan exportWindow,
        TimeProvider clock,
        ILogger<PlaylistFiles>? logger)
    {
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentException.ThrowIfNullOrWhiteSpace(exportsDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(exportWindow, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(clock);
        _playlists = playlists;
        _tracks = tracks;
        ExportsDirectory = exportsDirectory;
        _window = exportWindow;
        _clock = clock;
        _logger = logger;
    }

    public string ExportsDirectory { get; }

    /// <summary>
    /// Starts following playlist changes, and brings every export up to date first: a change made in the last window of
    /// the previous session, before a crash, is written now. Idempotent.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
            {
                return;
            }

            _started = true;
            _playlists.Changed += OnPlaylistChanged;
        }

        IReadOnlyList<PlaylistDto> all = await _playlists.ListAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _pending.UnionWith(all.Select(p => p.Id));
        }

        await FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Writes every change still waiting for its window now. The shell calls it on the way out.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        long[] ids;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            ids = [.. _pending];
            _pending.Clear();
        }

        if (ids.Length == 0)
        {
            return;
        }

        await _writing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AutoExportAsync(ids, ct).ConfigureAwait(false);
        }
        finally
        {
            _writing.Release();
        }
    }

    public async Task<PlaylistExportResult?> ExportAsync(long playlistId, string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        PlaylistDetailDto? detail = await _playlists.GetDetailAsync(playlistId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            return null;
        }

        string full = Path.GetFullPath(filePath);
        await WriteAsync(full, M3u8.Write(detail.Playlist.Name, Entries(detail), Path.GetDirectoryName(full)), ct).ConfigureAwait(false);
        _logger?.LogInformation("Exported playlist {Id} ({Count} tracks) to {Path}", playlistId, detail.Tracks.Count, full);
        return new PlaylistExportResult(full, detail.Tracks.Count);
    }

    public async Task<IReadOnlyList<PlaylistExportResult>> ExportAllAsync(string directory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        IReadOnlyList<PlaylistDto> all = await _playlists.ListAsync(ct).ConfigureAwait(false);
        var results = new List<PlaylistExportResult>(all.Count);
        foreach (PlaylistDto playlist in all)
        {
            if (await ExportAsync(playlist.Id, Path.Combine(directory, FileNameAmong(playlist, all)), ct).ConfigureAwait(false) is { } result)
            {
                results.Add(result);
            }
        }

        return results;
    }

    public Task<PlaylistImportResult> ImportAsync(string filePath, CancellationToken ct = default) => ImportAsync(filePath, skipExisting: false, ct);

    public async Task<IReadOnlyList<PlaylistImportResult>> ImportExportsAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(ExportsDirectory))
        {
            return [];
        }

        var results = new List<PlaylistImportResult>();
        foreach (string file in Directory.EnumerateFiles(ExportsDirectory, "*" + M3u8.Extension).Order(StringComparer.OrdinalIgnoreCase))
        {
            results.Add(await ImportAsync(file, skipExisting: true, ct).ConfigureAwait(false));
        }

        return results;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _playlists.Changed -= OnPlaylistChanged;
            _timer?.Dispose();
            _timer = null;
        }

        _writing.Dispose();
    }

    /// <summary>
    /// The file a playlist is written to among <paramref name="all"/>: its name made legal, and when two playlists come out
    /// the same (names are not unique, and "a/b" and "a_b" collide), every one but the oldest gets its id added.
    /// </summary>
    public static string FileNameAmong(PlaylistDto playlist, IReadOnlyList<PlaylistDto> all)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(all);
        string name = M3u8.FileNameFor(playlist.Name);
        long oldest = all
            .Where(p => string.Equals(M3u8.FileNameFor(p.Name), name, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .DefaultIfEmpty(playlist.Id)
            .Min();
        return oldest == playlist.Id ? name : Path.GetFileNameWithoutExtension(name) + " (" + playlist.Id + ")" + M3u8.Extension;
    }

    private static IEnumerable<M3u8Entry> Entries(PlaylistDetailDto detail) =>
        detail.Tracks.Select(t => new M3u8Entry(t.Path, t.DurationMs, t.Title, t.ArtistNames));

    private void OnPlaylistChanged(object? sender, long id)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending.Add(id);
            _timer ??= _clock.CreateTimer(_ => _ = FlushInBackgroundAsync(), null, _window, Timeout.InfiniteTimeSpan);
        }

        // The start of AC-145's five seconds, so a live check can measure change-to-file from the log.
        _logger?.LogDebug("Playlist {Id} changed; auto-export queued", id);
    }

    private async Task FlushInBackgroundAsync()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Shut down between the timer firing and the write starting; the shell's own flush ran first.
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger?.LogError(e, "Playlist auto-export failed");
        }
    }

    private async Task AutoExportAsync(IReadOnlyList<long> ids, CancellationToken ct)
    {
        IReadOnlyList<PlaylistDto> all = await _playlists.ListAsync(ct).ConfigureAwait(false);
        foreach (long id in ids)
        {
            try
            {
                PlaylistDetailDto? detail = all.Any(p => p.Id == id) ? await _playlists.GetDetailAsync(id, ct).ConfigureAwait(false) : null;
                if (detail is null)
                {
                    ForgetWritten(id);
                    continue;
                }

                string path = Path.Combine(ExportsDirectory, FileNameAmong(detail.Playlist, all));
                string text = M3u8.Write(detail.Playlist.Name, Entries(detail), baseDirectory: null);
                bool wrote = await WriteIfChangedAsync(path, text, ct).ConfigureAwait(false);
                if (_written.TryGetValue(id, out string? previous) && !string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
                {
                    ForgetWritten(id); // renamed: the old name's file goes
                }

                _written[id] = path;
                if (wrote)
                {
                    _logger?.LogDebug("Auto-exported playlist {Id} ({Count} tracks) to {Path}", id, detail.Tracks.Count, path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _logger?.LogWarning(e, "Could not auto-export playlist {Id}", id);
            }
        }
    }

    /// <summary>Deletes the file this process last wrote for <paramref name="id"/>, unless another playlist's export is now that file.</summary>
    private void ForgetWritten(long id)
    {
        if (!_written.Remove(id, out string? path))
        {
            return;
        }

        if (_written.Values.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        File.Delete(path);
        _logger?.LogDebug("Removed the auto-export of playlist {Id} at {Path}", id, path);
    }

    private async Task<PlaylistImportResult> ImportAsync(string filePath, bool skipExisting, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string full = Path.GetFullPath(filePath);
        // ReadAllText honours a byte-order mark and reads UTF-8 without one, which is what .m3u8 means.
        string text = await File.ReadAllTextAsync(full, ct).ConfigureAwait(false);
        M3u8Document document = M3u8.Parse(text, Path.GetDirectoryName(full)!);
        string name = document.Name ?? Path.GetFileNameWithoutExtension(full);

        IReadOnlyList<PlaylistDto> existing = await _playlists.ListAsync(ct).ConfigureAwait(false);
        bool taken = existing.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (taken && skipExisting)
        {
            return new PlaylistImportResult(full, name, null, 0, 0, AlreadyExists: true);
        }

        var ids = new List<long>(document.Paths.Count);
        foreach (string path in document.Paths)
        {
            if (await _tracks.GetByPathAsync(path, ct).ConfigureAwait(false) is { } track)
            {
                ids.Add(track.Id);
            }
        }

        int unmatched = document.Paths.Count - ids.Count + document.Skipped;
        if (ids.Count == 0 && unmatched > 0)
        {
            // Every entry is missing: most likely the library has not been rescanned since a reset. An empty playlist
            // made now would take the name, and the import that should follow the rescan would skip it as present.
            _logger?.LogInformation("Did not import {Path}: none of its {Count} entries is in the library", full, unmatched);
            return new PlaylistImportResult(full, name, null, 0, unmatched, AlreadyExists: false);
        }

        string unique = taken ? UniqueName(name, existing) : name;
        PlaylistDto created = await _playlists.CreateAsync(unique, ct).ConfigureAwait(false);
        await _playlists.AddTracksAsync(created.Id, ids, ct).ConfigureAwait(false);
        _logger?.LogInformation("Imported {Path} as playlist {Id} ({Matched} tracks, {Unmatched} not in the library)", full, created.Id, ids.Count, unmatched);
        return new PlaylistImportResult(full, unique, created with { TrackCount = ids.Count }, ids.Count, unmatched, AlreadyExists: false);
    }

    private static string UniqueName(string name, IReadOnlyList<PlaylistDto> existing)
    {
        for (int n = 2; ; n++)
        {
            string candidate = name + " (" + n + ")";
            if (!existing.Any(p => string.Equals(p.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    private static async Task<bool> WriteIfChangedAsync(string path, string text, CancellationToken ct)
    {
        if (File.Exists(path) && await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) == text)
        {
            return false;
        }

        await WriteAsync(path, text, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Writes beside the target and moves it over, so a crash mid-write leaves the old file rather than half a new one.</summary>
    private static async Task WriteAsync(string path, string text, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, text, Utf8NoBom, ct).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }
}
