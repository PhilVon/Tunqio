using Microsoft.Extensions.Logging;
using Tunqio.Core.Playback;

namespace Tunqio.App.Playback;

/// <summary>
/// The <see cref="IPlaybackCommands"/> the library views hold (E3-S8). It forwards to the
/// <see cref="PlaybackSession"/> as soon as <see cref="AudioStartup"/> has one, and logs and drops the request
/// until then — the views are transient and a user can reach a play button in the window's first frames, before
/// audio has finished coming up, and for the whole session when audio could not come up at all.
/// </summary>
/// <remarks>
/// The indirection is what lets the views take a plain singleton dependency while the session's lifetime is
/// decided after the host is built: the alternative is a container registration that throws when the engine is
/// missing, which turns a survivable "no audio this session" into a crash on the first navigation.
/// </remarks>
public sealed partial class AppPlaybackCommands : IPlaybackCommands
{
    private readonly IPlaybackSessionSource _audio;
    private readonly ILogger<AppPlaybackCommands> _logger;

    public AppPlaybackCommands(IPlaybackSessionSource audio, ILogger<AppPlaybackCommands> logger)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(logger);
        _audio = audio;
        _logger = logger;
    }

    public Task PlayNowAsync(IReadOnlyList<long> trackIds, int startIndex = 0, bool shuffle = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        return _audio.Session is { } session
            ? session.PlayNowAsync(trackIds, startIndex, shuffle, ct)
            : DropAsync(shuffle ? "shuffle" : "play now", trackIds.Count, startIndex);
    }

    public Task PlayNextAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        return _audio.Session is { } session ? session.PlayNextAsync(trackIds, ct) : DropAsync("play next", trackIds.Count, 0);
    }

    public Task EnqueueAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        return _audio.Session is { } session ? session.EnqueueAsync(trackIds, ct) : DropAsync("enqueue", trackIds.Count, 0);
    }

    private Task DropAsync(string action, int count, int startIndex)
    {
        LogDropped(action, count, startIndex, _audio.Started ? "audio is unavailable this session" : "audio is still starting");
        return Task.CompletedTask;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Playback request '{Action}' for {Count} track(s) from index {StartIndex} dropped: {Reason}")]
    private partial void LogDropped(string action, int count, int startIndex, string reason);
}
