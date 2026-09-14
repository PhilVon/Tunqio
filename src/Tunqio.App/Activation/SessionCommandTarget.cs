using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.App.Playback;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Library;

namespace Tunqio.App.Activation;

/// <summary>
/// The app's <see cref="ICommandTarget"/>: the one <see cref="PlaybackSession"/> for the transport and for placing a track,
/// and <see cref="OpenFilesService"/> for turning paths into ids (library rows where they exist, transient tracks where
/// they do not, D-24).
/// </summary>
/// <remarks>
/// A launch's own arguments are routed while audio is still coming up, since the engine is deliberately after the first
/// frame; so every command waits for the session, up to <see cref="SessionWait"/>, rather than being dropped the way a
/// library view's early click is. A session that never arrives is a refusal with a reason, not a hang.
/// </remarks>
public sealed class SessionCommandTarget : ICommandTarget
{
    /// <summary>How long a command waits for audio to come up.</summary>
    public static readonly TimeSpan SessionWait = TimeSpan.FromSeconds(30);

    private readonly IPlaybackSessionSource _source;
    private readonly OpenFilesService _open;
    private readonly ITrackRepository _tracks;
    private readonly IPlaylistRepository _playlists;
    private readonly Action _bringToForeground;
    private readonly TimeSpan _wait;
    private readonly ILogger _log;

    /// <param name="source">Where the session comes from.</param>
    /// <param name="open">Turns paths into ids.</param>
    /// <param name="tracks">Resolves a jump list track item's id (E7-S5).</param>
    /// <param name="playlists">Resolves a jump list playlist item's id (E7-S5).</param>
    /// <param name="bringToForeground">Raises the main window.</param>
    /// <param name="wait">How long to wait for the session; <see cref="SessionWait"/> in the app, shorter in tests.</param>
    /// <param name="log">Where what was played is recorded.</param>
    public SessionCommandTarget(
        IPlaybackSessionSource source,
        OpenFilesService open,
        ITrackRepository tracks,
        IPlaylistRepository playlists,
        Action bringToForeground,
        TimeSpan wait,
        ILogger<SessionCommandTarget>? log)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(playlists);
        ArgumentNullException.ThrowIfNull(bringToForeground);
        _source = source;
        _open = open;
        _tracks = tracks;
        _playlists = playlists;
        _bringToForeground = bringToForeground;
        _wait = wait;
        _log = log ?? NullLogger<SessionCommandTarget>.Instance;
    }

    public async Task PlayFileNowAsync(string file, CancellationToken ct)
    {
        PlaybackSession session = await SessionAsync(ct);
        long id = await _open.ResolveFileAsync(file, ct) ?? throw new InvalidOperationException($"'{file}' is not a playable audio file");
        await PlayAtCurrentPositionAsync(session, id, ct);

        _log.LogInformation("Activation: playing {File} at the current position; queue now {Count} item(s)", file, session.Queue.Items.Count);
    }

    public async Task PlayPathsAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        PlaybackSession session = await SessionAsync(ct);
        OpenResult result = await _open.OpenAsync(paths, ct);
        if (!result.StartedPlaying)
        {
            throw new InvalidOperationException($"nothing playable in {paths.Count} path(s); the queue is unchanged");
        }

#pragma warning disable VSTHRD003 // LastFill is the fill OpenAsync started a line above, in this call; nothing else awaits it here.
        await _open.LastFill;
#pragma warning restore VSTHRD003
        _log.LogInformation(
            "Activation: queue replaced from {Paths} path(s); queue now {Count} item(s), {Skipped} skipped",
            paths.Count, session.Queue.Items.Count, result.Skipped);
    }

    public async Task QueuePathsAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        PlaybackSession session = await SessionAsync(ct);
        OpenResult result = await _open.EnqueueAsync(paths, ct);
        if (result.Playable == 0)
        {
            throw new InvalidOperationException($"nothing playable in {paths.Count} path(s); the queue is unchanged");
        }

        _log.LogInformation("Activation: {Playable} file(s) queued; queue now {Count} item(s)", result.Playable, session.Queue.Items.Count);
    }

    public async Task TogglePlayPauseAsync(CancellationToken ct) => await (await SessionAsync(ct)).TogglePlayPauseAsync(ct);

    public async Task NextAsync(CancellationToken ct) => await (await SessionAsync(ct)).NextAsync(ct);

    public async Task PreviousAsync(CancellationToken ct) => await (await SessionAsync(ct)).PreviousAsync(ct);

    /// <summary>
    /// A jump list track item (E7-S5). The id is checked before waiting for audio, so a track that has left the library is refused
    /// at once, and so is one whose file was missing at the last scan. Placed as a file opened from Explorer is (flow 2).
    /// </summary>
    public async Task PlayTrackAsync(long trackId, CancellationToken ct)
    {
        TrackDto track = await _tracks.GetAsync(trackId, ct) ?? throw new InvalidOperationException($"track {trackId} is not in the library");
        if (track.Missing)
        {
            throw new InvalidOperationException($"track {trackId} ({track.Path}) was missing at the last scan");
        }

        PlaybackSession session = await SessionAsync(ct);
        await PlayAtCurrentPositionAsync(session, track.Id, ct);
        _log.LogInformation("Activation: playing track {Id} ({Title}) at the current position; queue now {Count} item(s)", track.Id, track.Title, session.Queue.Items.Count);
    }

    /// <summary>A jump list playlist item (E7-S5): the playlist replaces the queue and plays from its first track.</summary>
    public async Task PlayPlaylistAsync(long playlistId, CancellationToken ct)
    {
        PlaylistDetailDto detail = await _playlists.GetDetailAsync(playlistId, ct) ?? throw new InvalidOperationException($"playlist {playlistId} does not exist");
        if (detail.Tracks.Count == 0)
        {
            throw new InvalidOperationException($"playlist {playlistId} ({detail.Playlist.Name}) has no tracks");
        }

        PlaybackSession session = await SessionAsync(ct);
        await session.PlayNowAsync([.. detail.Tracks.Select(t => t.Id)], ct: ct);
        _log.LogInformation(
            "Activation: playing playlist {Id} ({Name}); queue now {Count} item(s)", playlistId, detail.Playlist.Name, session.Queue.Items.Count);
    }

    public void BringToForeground() => _bringToForeground();

    private static async Task PlayAtCurrentPositionAsync(PlaybackSession session, long id, CancellationToken ct)
    {
        if (session.Queue.Current is null)
        {
            await session.PlayNowAsync([id], ct: ct);
        }
        else
        {
            // Flow 2: inserted right after the current item and moved to, so what was queued is still queued after it.
            await session.PlayNextAsync([id], ct);
            await session.NextAsync(ct);
        }
    }

    private async Task<PlaybackSession> SessionAsync(CancellationToken ct)
    {
        if (_source.Session is { } session)
        {
            return session;
        }

        var ready = new TaskCompletionSource<PlaybackSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnReady(object? sender, PlaybackSession arrived) => ready.TrySetResult(arrived);
        _source.SessionReady += OnReady;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            // Again after subscribing: the session may have arrived between the first look and the subscription.
            if (_source.Session is { } arrived)
            {
                return arrived;
            }

            Task finished = await Task.WhenAny(ready.Task, Task.Delay(_wait, timeout.Token));
            ct.ThrowIfCancellationRequested();
            if (finished != ready.Task)
            {
                throw new InvalidOperationException($"audio did not start within {_wait.TotalSeconds:0} s, so there is no session to play on");
            }

            return await ready.Task;
        }
        finally
        {
            _source.SessionReady -= OnReady;
            await timeout.CancelAsync();
        }
    }
}
