using System.Globalization;

namespace Tunqio.Core.Library;

/// <summary>
/// The two scales a rating lives on (E6-S7; docs/library-and-data.md, <c>rating INTEGER -- 0..100, user data</c>).
/// The database keeps 0..100 so a file's rating can come in on whatever scale its tagger used; the user sees and
/// sets 1..5 stars, and a star is 20 points. <c>null</c> is "not rated", which is not the same as one star and
/// not the same as zero: clearing a rating is forgetting it, and the sort puts unrated tracks after every rated one.
/// </summary>
public static class Ratings
{
    /// <summary>Stars per rating, and the most a track can have.</summary>
    public const int MaxStars = 5;

    /// <summary>What one star is worth on the 0..100 scale.</summary>
    public const int PointsPerStar = 100 / MaxStars;

    /// <summary>A 0..100 rating as 0..5 stars, rounding to the nearest star; <c>null</c> (not rated) is 0.</summary>
    public static int Stars(int? rating) =>
        rating is { } r ? Math.Clamp((r + PointsPerStar / 2) / PointsPerStar, 0, MaxStars) : 0;

    /// <summary>The 0..100 value of <paramref name="stars"/>: 20 per star; 0 or fewer stars is <c>null</c>, the cleared rating.</summary>
    public static int? FromStars(int stars) =>
        stars <= 0 ? null : Math.Clamp(stars, 1, MaxStars) * PointsPerStar;

    /// <summary>
    /// The rating as Narrator says it: "3 of 5 stars", "1 of 5 stars", or "not rated". The word "Rating" is the
    /// control's to put in front, so a row that carries several ratings can still tell them apart by label.
    /// </summary>
    public static string Describe(int stars) =>
        stars <= 0
            ? "not rated"
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Min(stars, MaxStars)} of {MaxStars} stars");
}

/// <summary>
/// What happened to one rating (E6-S7). <see cref="Rating"/> is the value now in the library row (0..100, or
/// <c>null</c> for cleared); <see cref="FileWrite"/> is the tag write's outcome when
/// <c>library.writeRatingsToFiles</c> asked for one, and <c>null</c> when it did not.
/// </summary>
/// <param name="TrackId">The track.</param>
/// <param name="Path">Its file, so a listener can say which file could not be written.</param>
/// <param name="Rating">The rating the row holds now.</param>
/// <param name="FileWrite">The file write's outcome, or <c>null</c> when writing to files is off.</param>
/// <param name="Error">Why the file write failed, for the notice; <c>null</c> otherwise.</param>
public sealed record RatingChange(long TrackId, string Path, int? Rating, TagWriteOutcome? FileWrite = null, string? Error = null)
{
    /// <summary>True when a file write was asked for and did not land.</summary>
    public bool FileWriteFailed => FileWrite == TagWriteOutcome.Failed;
}

/// <summary>
/// Rates a track (E6-S7): the library row first, always, and then the file's tag when
/// <c>library.writeRatingsToFiles</c> is on (OQ-7: opt-in, default off). The row is the source of truth — a file
/// write that fails is reported and the library rating still stands — and it is written before the file so the
/// screen follows the user's click rather than the disk.
/// </summary>
public interface ITrackRater
{
    /// <summary>
    /// The library row's rating changed. Raised after the row is written and before any file write, on the
    /// writer's thread; a view showing the track patches its row from the argument rather than requerying.
    /// </summary>
    event EventHandler<RatingChange>? Changed;

    /// <summary>
    /// A file write finished, with its outcome — including the writes that waited for playback to release the
    /// file. Raised on the writer's thread. The shell shows a notice for a failed one.
    /// </summary>
    event EventHandler<RatingChange>? FileWriteCompleted;

    /// <summary>
    /// Sets the track's rating to <paramref name="stars"/> (1..5), or clears it for 0. Returns what was done; a
    /// track the library no longer has comes back with <see cref="RatingChange.Error"/> set and nothing written.
    /// </summary>
    Task<RatingChange> RateAsync(long trackId, int stars, CancellationToken ct = default);

    /// <summary>
    /// Writes the ratings whose files were open in the engine when they were set (the tag editor's active-track
    /// deferral, E3-S10), for the files that are no longer open. Returns how many were written.
    /// </summary>
    Task<int> FlushDeferredAsync(CancellationToken ct = default);
}
