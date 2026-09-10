namespace Tunqio.Core.Library;

/// <summary>
/// One finished listen (E3-S11, docs/library-and-data.md, "Play history rules"): a row of <c>play_event</c>.
/// <c>PlaybackSession</c> (E1-S10) records one whenever a track stops being current — skipped, finished, or
/// interrupted by the user picking something else — so a track played twice is two events, and a repeat-one
/// track is one event per pass.
/// </summary>
/// <param name="TrackId">The track that was heard.</param>
/// <param name="StartedAt">When the listen began, Unix milliseconds UTC; also what "Recently played" sorts by.</param>
/// <param name="PlayedMs">Heard time — time the track was actually audible, so it excludes pauses and does not
/// advance while seeking backwards over the same audio twice.</param>
/// <param name="Completed">Whether the listen counts as a play (see <see cref="PlayCompletion"/>).</param>
public sealed record PlayEvent(long TrackId, long StartedAt, long PlayedMs, bool Completed)
{
    /// <summary>
    /// The event for a listen of <paramref name="heard"/> on a track of <paramref name="duration"/>, with
    /// <see cref="Completed"/> decided by <see cref="PlayCompletion"/>. Negative heard time is clamped to zero.
    /// </summary>
    public static PlayEvent For(long trackId, long startedAt, TimeSpan heard, TimeSpan duration) => new(
        trackId,
        startedAt,
        (long)Math.Max(0, heard.TotalMilliseconds),
        PlayCompletion.IsComplete(heard, duration));
}

/// <summary>
/// When a listen counts as a play (docs/library-and-data.md): heard time past half the track, or past four
/// minutes, whichever comes first. It is the Last.fm scrobbling rule deliberately, so 1.1 scrobbling can send the
/// events this rule already recorded rather than re-deciding them against a different threshold.
/// </summary>
public static class PlayCompletion
{
    /// <summary>The cap: no track has to be heard for longer than this to count, however long it runs.</summary>
    public static readonly TimeSpan FullPlayCap = TimeSpan.FromMinutes(4);

    /// <summary>
    /// How much of a track of <paramref name="duration"/> has to be heard for the listen to count. A duration
    /// that is unknown or nonsense (zero or negative — a stream, or a file the tag reader could not measure)
    /// falls back to the cap alone: without a length there is no half to be past, and completing every such
    /// listen the instant it starts would inflate the counts the "Most played" view is built on.
    /// </summary>
    public static TimeSpan Threshold(TimeSpan duration) =>
        duration <= TimeSpan.Zero ? FullPlayCap : Min(duration / 2, FullPlayCap);

    /// <summary>Whether <paramref name="heard"/> of a track of <paramref name="duration"/> counts as a play.</summary>
    public static bool IsComplete(TimeSpan heard, TimeSpan duration) => heard > Threshold(duration);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
