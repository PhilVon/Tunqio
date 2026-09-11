using System.Globalization;
using Microsoft.Extensions.Logging;
using Tunqio.Core.Library;

namespace Tunqio.Library.Tags;

/// <summary>
/// <see cref="ITagEditor"/>: the story's orchestration, on top of <see cref="ITagWriter"/> for the files and
/// <see cref="ILibraryScanner"/> for the rows. One file at a time, because a batch of twelve must report
/// twelve verdicts and show progress moving, and because writing several files at once only queues them
/// against the same disk.
/// </summary>
public sealed class TagEditor : ITagEditor
{
    private readonly ITagWriter _writer;
    private readonly ILibraryScanner _scanner;
    private readonly Func<string, bool> _isPlaying;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Stack<UndoStep> _undo = new();
    private readonly List<Pending> _pending = [];

    /// <param name="isPlaying">
    /// Whether playback currently holds the file open. The engine keeps the playing file's handle, so replacing
    /// it would fail on a sharing violation — those writes are deferred rather than failed (E3-S10's
    /// "active-track deferral"). Null treats nothing as playing, which is what the library tests want.
    /// </param>
    public TagEditor(ITagWriter writer, ILibraryScanner scanner, Func<string, bool>? isPlaying = null, ILogger<TagEditor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(scanner);
        _writer = writer;
        _scanner = scanner;
        _isPlaying = isPlaying ?? (_ => false);
        _logger = (ILogger?)logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    public bool CanUndo
    {
        get
        {
            lock (_lock)
            {
                return _undo.Count > 0;
            }
        }
    }

    public string? UndoDescription
    {
        get
        {
            lock (_lock)
            {
                return _undo.Count > 0 ? _undo.Peek().Description : null;
            }
        }
    }

    public int DeferredCount
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    public event EventHandler? Changed;

    public async Task<TagEditReport> ApplyAsync(IReadOnlyList<TagEditTarget> targets, TagEdit edit, IProgress<TagEditProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(edit);

        string description = Describe(edit, targets.Count);
        var step = new UndoStep(Guid.NewGuid(), description);
        TagEditReport report = await RunAsync(targets, _ => edit, description, step, isUndo: false, progress, ct).ConfigureAwait(false);

        if (step.Files.Count > 0 || step.Deferred.Count > 0)
        {
            lock (_lock)
            {
                _undo.Push(step);
            }

            Raise();
        }

        return report;
    }

    public async Task<TagEditReport?> UndoAsync(IProgress<TagEditProgress>? progress = null, CancellationToken ct = default)
    {
        UndoStep step;
        lock (_lock)
        {
            if (_undo.Count == 0)
            {
                return null;
            }

            step = _undo.Pop();
            // A write from this batch that never landed is undone by simply not doing it.
            _pending.RemoveAll(p => p.Batch == step.Id);
        }

        Raise();

        // Only the files that actually changed are put back; the ones that already matched were never written,
        // and rewriting them would move their modification time for nothing.
        IReadOnlyList<TagEditTarget> targets = [.. step.Files.Keys];
        var undoStep = new UndoStep(Guid.NewGuid(), step.Description);
        TagEditReport report = await RunAsync(
            targets,
            target => step.Files[target].ToEdit(),
            "Undo " + Lower(step.Description),
            undoStep,
            isUndo: true,
            progress,
            ct).ConfigureAwait(false);

        return report;
    }

    public async Task<int> FlushDeferredAsync(CancellationToken ct = default)
    {
        Pending[] ready;
        lock (_lock)
        {
            ready = [.. _pending.Where(p => !_isPlaying(p.Target.Path))];
            if (ready.Length == 0)
            {
                return 0;
            }

            _pending.RemoveAll(ready.Contains);
        }

        var written = new List<TagEditTarget>();
        foreach (Pending item in ready)
        {
            TagWriteResult result = await _writer.WriteAsync(item.Target.Path, item.Edit, ct).ConfigureAwait(false);
            if (result.Outcome == TagWriteOutcome.Written)
            {
                written.Add(item.Target);
                Record(item.Batch, item.Target, result.Before);
            }
            else if (result.Outcome == TagWriteOutcome.Failed)
            {
                _logger.LogWarning("Deferred tag write of {Path} failed: {Error}", item.Target.Path, result.Error);
            }
        }

        await RefreshAsync(written, ct).ConfigureAwait(false);
        Raise();
        return written.Count;
    }

    // ---- the one loop both apply and undo run ---------------------------------------------------------------

    private async Task<TagEditReport> RunAsync(
        IReadOnlyList<TagEditTarget> targets,
        Func<TagEditTarget, TagEdit> editFor,
        string description,
        UndoStep step,
        bool isUndo,
        IProgress<TagEditProgress>? progress,
        CancellationToken ct)
    {
        var results = new List<TagEditFileResult>(targets.Count);
        var written = new List<TagEditTarget>();

        for (int i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            TagEditTarget target = targets[i];
            progress?.Report(new TagEditProgress(i, targets.Count, target.Display));

            TagEdit edit = editFor(target);
            if (_isPlaying(target.Path))
            {
                // The engine has the file open; replacing it now would fail on a sharing violation. The snapshot
                // still comes from the file, so an undo offered before the write lands knows what to put back.
                TagSnapshot? before = await _writer.ReadAsync(target.Path, ct).ConfigureAwait(false);
                Defer(step, target, edit, before);
                results.Add(new TagEditFileResult(target, TagWriteOutcome.Deferred));
                continue;
            }

            TagWriteResult result = await _writer.WriteAsync(target.Path, edit, ct).ConfigureAwait(false);
            results.Add(new TagEditFileResult(target, result.Outcome, result.Error));
            if (result.Outcome == TagWriteOutcome.Written)
            {
                written.Add(target);
                step.Files[target] = result.Before ?? new TagSnapshot();
            }
            else if (result.Outcome == TagWriteOutcome.Failed)
            {
                _logger.LogWarning("Tag write of {Path} failed: {Error}", target.Path, result.Error);
            }
        }

        progress?.Report(new TagEditProgress(targets.Count, targets.Count, string.Empty));
        string? error = await RefreshAsync(written, ct).ConfigureAwait(false);
        return new TagEditReport(description, results, isUndo, error);
    }

    /// <summary>
    /// Brings the library back into step with the files just written, by rescanning exactly those paths — the
    /// watcher's own request shape. Returns a message when the library could not be updated.
    /// </summary>
    private async Task<string?> RefreshAsync(List<TagEditTarget> written, CancellationToken ct)
    {
        if (written.Count == 0)
        {
            return null;
        }

        string? error = null;
        foreach (IGrouping<long, TagEditTarget> folder in written.GroupBy(t => t.FolderId))
        {
            try
            {
                await _scanner.ScanAsync(ScanRequest.Targeted(folder.Key, [.. folder.Select(t => t.Path)]), null, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException e)
            {
                // One scan at a time is the scanner's contract, and a launch scan can still be running. The files
                // are already written, and the watcher will scan these paths once its debounce elapses, so the
                // library catches up by itself; what the user must not be told is that the edit worked and then
                // see stale rows with no explanation.
                _logger.LogWarning(e, "Tag edit could not refresh folder {Folder} now; the watcher will pick it up", folder.Key);
                error = "The files were written. The library list will catch up in a moment.";
            }
        }

        return error;
    }

    private void Defer(UndoStep step, TagEditTarget target, TagEdit edit, TagSnapshot? before)
    {
        lock (_lock)
        {
            _pending.RemoveAll(p => string.Equals(p.Target.Path, target.Path, StringComparison.OrdinalIgnoreCase));
            _pending.Add(new Pending(step.Id, target, edit));
            step.Deferred.Add(target);
            step.Files.TryAdd(target, before ?? new TagSnapshot());
        }
    }

    private void Record(Guid batch, TagEditTarget target, TagSnapshot? before)
    {
        lock (_lock)
        {
            foreach (UndoStep step in _undo)
            {
                if (step.Id == batch)
                {
                    step.Files[target] = before ?? new TagSnapshot();
                    step.Deferred.Remove(target);
                    return;
                }
            }
        }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// What the notice bar says the undo would reverse. Naming the fields rather than saying "tags" is the
    /// difference between a user who can tell which of two edits they are about to undo and one who cannot.
    /// </summary>
    internal static string Describe(TagEdit edit, int count)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var fields = new List<string>();
        Add(fields, edit.Title, "title");
        Add(fields, edit.Artists, "artist");
        Add(fields, edit.AlbumTitle, "album");
        Add(fields, edit.AlbumArtist, "album artist");
        Add(fields, edit.Year, "year");
        Add(fields, edit.TrackNo, "track number");
        Add(fields, edit.DiscNo, "disc number");
        Add(fields, edit.Genres, "genre");
        Add(fields, edit.Composer, "composer");
        Add(fields, edit.Comment, "comment");

        string what = fields.Count switch
        {
            0 => "Tag edit",
            1 => Capitalise(fields[0]),
            2 => Capitalise(fields[0]) + " and " + fields[1],
            _ => Capitalise(fields[0]) + " and " + (fields.Count - 1).ToString(CultureInfo.InvariantCulture) + " more fields",
        };

        return $"{what} on {count} {(count == 1 ? "track" : "tracks")}";

        static void Add(List<string> fields, object? value, string name)
        {
            if (value is not null)
            {
                fields.Add(name);
            }
        }
    }

    private static string Capitalise(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    private static string Lower(string value) => char.ToLowerInvariant(value[0]) + value[1..];

    /// <summary>One batch on the undo stack: what each file said before it was written.</summary>
    private sealed record UndoStep(Guid Id, string Description)
    {
        public Dictionary<TagEditTarget, TagSnapshot> Files { get; } = [];

        /// <summary>Targets whose write has not happened yet; undoing the batch drops them instead of rewriting them.</summary>
        public List<TagEditTarget> Deferred { get; } = [];
    }

    /// <summary>A write waiting for its file to stop playing.</summary>
    private sealed record Pending(Guid Batch, TagEditTarget Target, TagEdit Edit);
}
