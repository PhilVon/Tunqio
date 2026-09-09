namespace Tunqio.Core.Playback;

/// <summary>
/// The queue-facing face of <c>PlaybackSession</c> (docs/solution-structure.md, "Key managed contracts"; E1-S10)
/// as the library views call it (E3-S8): a list of track ids to play now, play next or append. Ids are what
/// the views have (<see cref="Library.TrackDto.Id"/>); the session resolves them to files when it opens them,
/// so a track that went missing between the click and the open is skipped there, not here. Until the session
/// lands, the app registers a stand-in that logs the request.
/// </summary>
public interface IPlaybackCommands
{
    /// <summary>
    /// Replaces the queue with <paramref name="trackIds"/> and starts at <paramref name="startIndex"/>: playing
    /// an album from its fourth track keeps tracks one to three behind the current item so Previous reaches them.
    /// With <paramref name="shuffle"/> the session turns shuffle on for the new queue ("Shuffle album").
    /// </summary>
    Task PlayNowAsync(IReadOnlyList<long> trackIds, int startIndex = 0, bool shuffle = false, CancellationToken ct = default);

    /// <summary>Inserts <paramref name="trackIds"/> after the current item.</summary>
    Task PlayNextAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default);

    /// <summary>Appends <paramref name="trackIds"/> to the queue.</summary>
    Task EnqueueAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default);
}
