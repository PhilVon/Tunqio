using Microsoft.Extensions.Logging;
using Tunqio.Core.Playback;

namespace Tunqio.App.Playback;

/// <summary>
/// <see cref="IPlaybackCommands"/> until <c>PlaybackSession</c> (E1-S10) replaces it in <c>App.OnLaunched</c>:
/// records each request in the log so the library views' actions can be seen to fire. Nothing is queued and
/// nothing plays.
/// </summary>
public sealed partial class PendingPlaybackCommands : IPlaybackCommands
{
    private readonly ILogger<PendingPlaybackCommands> _logger;

    public PendingPlaybackCommands(ILogger<PendingPlaybackCommands> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task PlayNowAsync(IReadOnlyList<long> trackIds, int startIndex = 0, bool shuffle = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        LogRequest(shuffle ? "shuffle" : "play now", trackIds.Count, startIndex);
        return Task.CompletedTask;
    }

    public Task PlayNextAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        LogRequest("play next", trackIds.Count, 0);
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trackIds);
        LogRequest("enqueue", trackIds.Count, 0);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Playback request '{Action}' for {Count} track(s) from index {StartIndex} ignored: PlaybackSession arrives with E1-S10")]
    private partial void LogRequest(string action, int count, int startIndex);
}
