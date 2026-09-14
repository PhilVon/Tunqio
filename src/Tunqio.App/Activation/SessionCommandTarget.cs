using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.App.Playback;
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
    private readonly Action _bringToForeground;
    private readonly TimeSpan _wait;
    private readonly ILogger _log;

    /// <param name="wait">How long to wait for the session; <see cref="SessionWait"/> in the app, shorter in tests.</param>
    public SessionCommandTarget(
        IPlaybackSessionSource source, OpenFilesService open, Action bringToForeground, TimeSpan wait, ILogger<SessionCommandTarget>? log)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(bringToForeground);
        _source = source;
        _open = open;
        _bringToForeground = bringToForeground;
        _wait = wait;
        _log = log ?? NullLogger<SessionCommandTarget>.Instance;
    }

    public async Task PlayFileNowAsync(string file, CancellationToken ct)
    {
        PlaybackSession session = await SessionAsync(ct);
        long id = await _open.ResolveFileAsync(file, ct) ?? throw new InvalidOperationException($"'{file}' is not a playable audio file");
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

    public void BringToForeground() => _bringToForeground();

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
