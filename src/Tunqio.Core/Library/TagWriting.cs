namespace Tunqio.Core.Library;

/// <summary>
/// Writes one audio file's editable tags (E3-S10; docs/roadmap-and-backlog.md "E3-S10 · Tag writer and editor").
/// The counterpart of <see cref="ITagReader"/>, and like it, it does not throw for a file it cannot handle:
/// every failure comes back as a <see cref="TagWriteResult"/> so a batch of twelve does not stop at the third.
/// Only cancellation propagates.
/// <para>
/// The write is temp-copy, save, verify, replace — never an edit in place. TagLibSharp rewrites the tag region
/// of the file it is given, which for an MP3 whose ID3v2 tag has to grow means moving every audio frame; a
/// power cut or a thrown exception halfway through that leaves the user's music truncated. Working on a copy
/// means the only irreversible moment is the final <c>File.Replace</c>, and everything before it can fail
/// freely (AC-106).
/// </para>
/// </summary>
public interface ITagWriter
{
    /// <summary>
    /// The editable fields as they stand in the file now, or <c>null</c> when the file does not exist or cannot
    /// be parsed. This is what the editor captures before a write so it can put them back (AC-107's undo), and
    /// what the dialog fills its boxes from.
    /// </summary>
    Task<TagSnapshot?> ReadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Applies <paramref name="edit"/> to the file at <paramref name="path"/>. Fields left <c>null</c> on the
    /// edit are not touched; see <see cref="TagSnapshot"/> for how a field is cleared.
    /// </summary>
    Task<TagWriteResult> WriteAsync(string path, TagEdit edit, CancellationToken ct = default);
}

/// <summary>How a tag write ended.</summary>
public enum TagWriteOutcome
{
    /// <summary>The file on disk now carries the edited values, verified by reading them back.</summary>
    Written,

    /// <summary>The edit asked for nothing the file did not already say; the file was not touched.</summary>
    Unchanged,

    /// <summary>
    /// The file is the one playback currently holds open, so replacing it would fail on a sharing violation.
    /// The edit is kept and applied when the file is released (the "active-track deferral" of E3-S10).
    /// </summary>
    Deferred,

    /// <summary>The write failed. The original file is untouched — that is what the temp-and-replace shape buys.</summary>
    Failed,
}

/// <summary>
/// What <see cref="ITagWriter.WriteAsync"/> returns for one file. <see cref="Before"/> is the snapshot taken
/// just before the write and is what an undo replays; it is present for every outcome that read the file,
/// including <see cref="TagWriteOutcome.Deferred"/>, so the editor can offer undo for a deferred write too.
/// </summary>
public sealed record TagWriteResult(string Path, TagWriteOutcome Outcome, TagSnapshot? Before = null, string? Error = null)
{
    /// <summary>True when the bytes on disk changed.</summary>
    public bool Changed => Outcome == TagWriteOutcome.Written;
}

/// <summary>
/// The editable field set (docs/product-scope.md: "title, artist, album, album artist, year, genre, track/disc
/// number") as one file holds it, plus the two extra text fields <see cref="TagEdit"/> already carries. An
/// absent tag is <c>null</c> (or an empty list); that is the distinction the undo needs, because restoring a
/// field that was absent means clearing it, not leaving it alone.
/// </summary>
/// <remarks>
/// <para>
/// <b>How a field is cleared.</b> <see cref="TagEdit"/>'s <c>null</c> already means "unchanged", so it cannot
/// also mean "clear". For the writer, the empty value is the clear: <c>""</c> for text, <c>0</c> for a number,
/// an empty list for artists or genres. This is not a sentinel invented for the API — it is what the formats
/// themselves do, since a tag frame holding nothing is a frame that gets dropped on save. <see cref="ToEdit"/>
/// builds exactly such an edit from a snapshot, which is how undo restores a previously absent field.
/// </para>
/// <para>
/// <b>The rating is the exception to undo.</b> <see cref="Rating"/> (0..100, in whole stars — see
/// <see cref="Ratings"/>) is read and verified like the other fields, but <see cref="ToEdit"/> leaves it alone: the
/// tag editor never edits ratings, so its undo has no business putting one back, and a rating set from the star
/// control between an edit and its undo must survive the undo (E6-S7).
/// </para>
/// </remarks>
public sealed record TagSnapshot(
    string? Title = null,
    IReadOnlyList<string>? Artists = null,
    string? AlbumTitle = null,
    string? AlbumArtist = null,
    int? Year = null,
    int? TrackNo = null,
    int? DiscNo = null,
    IReadOnlyList<string>? Genres = null,
    string? Composer = null,
    string? Comment = null,
    int? Rating = null)
{
    /// <summary>
    /// An edit that puts every one of these values back, clearing the ones that were absent. Undo is this edit
    /// applied to the file the snapshot came from. The rating is not part of it (see the type's remarks).
    /// </summary>
    public TagEdit ToEdit() => new(
        Title: Title ?? string.Empty,
        Artists: Artists ?? [],
        AlbumTitle: AlbumTitle ?? string.Empty,
        AlbumArtist: AlbumArtist ?? string.Empty,
        Year: Year ?? 0,
        TrackNo: TrackNo ?? 0,
        DiscNo: DiscNo ?? 0,
        Genres: Genres ?? [],
        Composer: Composer ?? string.Empty,
        Comment: Comment ?? string.Empty);

    /// <summary>The snapshot <paramref name="edit"/> would produce from this one, for the verify step and for "is this a change at all".</summary>
    public TagSnapshot With(TagEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return new TagSnapshot(
            Title: Text(edit.Title, Title),
            Artists: List(edit.Artists, Artists),
            AlbumTitle: Text(edit.AlbumTitle, AlbumTitle),
            AlbumArtist: Text(edit.AlbumArtist, AlbumArtist),
            Year: Number(edit.Year, Year),
            TrackNo: Number(edit.TrackNo, TrackNo),
            DiscNo: Number(edit.DiscNo, DiscNo),
            Genres: List(edit.Genres, Genres),
            Composer: Text(edit.Composer, Composer),
            Comment: Text(edit.Comment, Comment),
            // Whole stars, because that is all any container keeps: an edit asking for 45 is verified against the 40 the file can say.
            Rating: Number(edit.Rating, Rating) is { } rating ? Ratings.FromStars(Ratings.Stars(rating)) : null);
    }

    /// <summary>Value equality over the fields, treating null and empty as the same absence (a format that cannot hold an empty frame reads one back as null).</summary>
    public bool Matches(TagSnapshot other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Same(Title, other.Title)
            && Same(AlbumTitle, other.AlbumTitle)
            && Same(AlbumArtist, other.AlbumArtist)
            && Year == other.Year
            && TrackNo == other.TrackNo
            && DiscNo == other.DiscNo
            && Same(Composer, other.Composer)
            && Same(Comment, other.Comment)
            && Same(Artists, other.Artists)
            && Same(Genres, other.Genres)
            && Rating == other.Rating;
    }

    private static string? Text(string? edited, string? current) =>
        edited is null ? current : edited.Length == 0 ? null : edited;

    private static int? Number(int? edited, int? current) =>
        edited is null ? current : edited.Value <= 0 ? null : edited;

    private static IReadOnlyList<string>? List(IReadOnlyList<string>? edited, IReadOnlyList<string>? current) =>
        edited is null ? current : edited.Count == 0 ? null : edited;

    private static bool Same(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.Ordinal);

    private static bool Same(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a ?? []).SequenceEqual(b ?? [], StringComparer.Ordinal);
}
