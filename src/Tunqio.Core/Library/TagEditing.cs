namespace Tunqio.Core.Library;

/// <summary>
/// Single and batch tag editing (E3-S10; docs/ui-screens-and-flows.md flow 8), and the undo that goes with it.
/// One edit is applied to every target file through <see cref="ITagWriter"/>, then the library is brought back
/// into step by rescanning exactly those paths, and the values each file held beforehand are pushed on an undo
/// stack that lives as long as the session.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the library is updated by a scan and not by a row write.</b> The obvious thing would be
/// <see cref="ITrackRepository.UpdateTagsAsync"/>, but a tag edit changes more of the row than the fields the
/// user typed: the file's size and modification time, and the album the track now belongs to. It also must not
/// change things that are not tags — <c>art_hash</c> is the sharp one, since it is written by the scanner's
/// art stage and an upsert built from the tag reader alone would clear it. A targeted rescan is the path that
/// already gets every one of those right, and it is the same path the file-system watcher would have taken
/// anyway once its debounce elapsed; doing it here just means the view updates now rather than in two seconds.
/// </para>
/// <para>
/// <b>Undo.</b> Each file's previous values are captured before it is written, so undo is the same machinery
/// run backwards: write the old values back, rescan, done. That is why it restores the files as well as the
/// database, which is what flow 8 asks for. It is deliberately session-scoped and not persisted: an undo stack
/// that survived a restart would be offering to undo a change the user has since made on purpose in another
/// program.
/// </para>
/// </remarks>
public interface ITagEditor
{
    /// <summary>True when there is a batch on the stack that <see cref="UndoAsync"/> would reverse.</summary>
    bool CanUndo { get; }

    /// <summary>What the top of the undo stack would undo ("Album artist on 12 tracks"), for the notice's text.</summary>
    string? UndoDescription { get; }

    /// <summary>Writes waiting for their file to stop playing (see <see cref="TagWriteOutcome.Deferred"/>).</summary>
    int DeferredCount { get; }

    /// <summary>Raised when <see cref="CanUndo"/> or <see cref="DeferredCount"/> may have changed. Raised on a pool thread.</summary>
    event EventHandler? Changed;

    /// <summary>
    /// Applies <paramref name="edit"/> to every target. Fields left <c>null</c> on the edit are not written,
    /// which is what makes a batch touch only the boxes the user actually filled in.
    /// </summary>
    Task<TagEditReport> ApplyAsync(IReadOnlyList<TagEditTarget> targets, TagEdit edit, IProgress<TagEditProgress>? progress = null, CancellationToken ct = default);

    /// <summary>Reverses the most recent batch, or returns <c>null</c> when there is nothing to reverse.</summary>
    Task<TagEditReport?> UndoAsync(IProgress<TagEditProgress>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Retries the writes that were deferred because their file was playing. The shell calls this when the
    /// current track changes. Returns how many files were written.
    /// </summary>
    Task<int> FlushDeferredAsync(CancellationToken ct = default);
}

/// <summary>One file an edit applies to: what to write, and what the library calls it.</summary>
/// <param name="Display">The name the dialog's preview list and the failure messages use (usually the title).</param>
public sealed record TagEditTarget(long TrackId, long FolderId, string Path, string Display)
{
    public static TagEditTarget For(TrackDto track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return new TagEditTarget(track.Id, track.FolderId, track.Path, track.Title);
    }
}

/// <summary>A progress sample for the dialog's bar: <paramref name="Completed"/> of <paramref name="Total"/> files done.</summary>
public sealed record TagEditProgress(int Completed, int Total, string Current);

/// <summary>What happened to one file.</summary>
public sealed record TagEditFileResult(TagEditTarget Target, TagWriteOutcome Outcome, string? Error = null);

/// <summary>
/// The outcome of one batch (or of undoing one). <see cref="Error"/> is set only for a failure of the whole
/// operation, such as the library refusing a rescan; a file that failed on its own is in <see cref="Files"/>.
/// </summary>
public sealed record TagEditReport(string Description, IReadOnlyList<TagEditFileResult> Files, bool IsUndo = false, string? Error = null)
{
    public int Written => Count(TagWriteOutcome.Written);

    public int Unchanged => Count(TagWriteOutcome.Unchanged);

    public int Deferred => Count(TagWriteOutcome.Deferred);

    public int Failed => Count(TagWriteOutcome.Failed);

    /// <summary>A sentence for the notice bar: what was written, and what was not.</summary>
    public string Summary()
    {
        var parts = new List<string> { $"{Written} {(Written == 1 ? "file" : "files")} updated" };
        if (Unchanged > 0)
        {
            parts.Add($"{Unchanged} already matched");
        }

        if (Deferred > 0)
        {
            parts.Add($"{Deferred} waiting for playback to move on");
        }

        if (Failed > 0)
        {
            parts.Add($"{Failed} failed");
        }

        return string.Join(", ", parts) + ".";
    }

    private int Count(TagWriteOutcome outcome) => Files.Count(f => f.Outcome == outcome);
}
