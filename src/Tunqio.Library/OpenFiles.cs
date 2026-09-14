using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.Library;

/// <summary>What one open-or-drop did, for the shell's notice.</summary>
/// <param name="Playable">How many supported audio files were found.</param>
/// <param name="Skipped">How many of the dropped items were not audio the app can play.</param>
/// <param name="FirstTitle">The title of the track playback started on, or null when nothing was playable.</param>
public sealed record OpenResult(int Playable, int Skipped, string? FirstTitle)
{
    /// <summary>Nothing in the drop was playable.</summary>
    public static OpenResult Nothing(int skipped) => new(0, skipped, null);

    public bool StartedPlaying => Playable > 0;
}

/// <summary>
/// Turns files and folders into a queue (E2-S4): the picker's selection, and whatever is dropped on the window.
/// It lives here rather than in Core because it walks the disk, and Core is not allowed to (ArchitectureTests).
/// The files need not be in the library — <see cref="TransientTrackStore"/> gives the ones that are not an id,
/// and everything downstream stays id-based (docs/ui-screens-and-flows.md flow 2).
/// </summary>
/// <remarks>
/// <para>
/// The order of work is the whole design, and AC-75 is what fixes it: dropping a folder of 200 files has to
/// start the first one within 500 ms. Reading tags for 200 files does not fit in 500 ms and never will, so the
/// first file is read and played on its own, and the other 199 are read afterwards and appended in order while
/// it is already playing. The user hears music while the queue is still filling, which is also simply the nicer
/// behaviour.
/// </para>
/// <para>
/// A file already in the library keeps its library row rather than becoming a second, transient copy of itself:
/// dropping an album you own should give you your play counts and your art, not a stranger's.
/// </para>
/// </remarks>
public sealed class OpenFilesService
{
    /// <summary>How many files are appended per batch after the first; small enough that the queue visibly fills.</summary>
    private const int BatchSize = 25;

    private readonly IPlaybackCommands _playback;
    private readonly ITagReader _tags;
    private readonly TransientTrackStore _transient;
    private readonly ITrackRepository? _library;
    private readonly ILogger _log;

    /// <param name="library">
    /// Consulted so a dropped file that is already indexed plays as its library row. Null skips the lookup, which
    /// is what a session with no database does.
    /// </param>
    public OpenFilesService(
        IPlaybackCommands playback,
        ITagReader tags,
        TransientTrackStore transient,
        ITrackRepository? library,
        ILogger<OpenFilesService>? log = null)
    {
        ArgumentNullException.ThrowIfNull(playback);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(transient);
        _playback = playback;
        _tags = tags;
        _transient = transient;
        _library = library;
        _log = log ?? NullLogger<OpenFilesService>.Instance;
    }

    /// <summary>
    /// Plays what the paths contain, replacing the queue. Folders are walked; anything unsupported is counted and
    /// dropped. Returns as soon as the first track is playing — the rest is appended in the background, so a
    /// caller that wants the whole queue should await <see cref="LastFill"/>.
    /// </summary>
    public async Task<OpenResult> OpenAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        (IReadOnlyList<string> files, int skipped) = Collect(paths);
        if (files.Count == 0)
        {
            _log.LogInformation("Open: nothing playable in {Count} item(s)", paths.Count);
            LastFill = Task.CompletedTask;
            return OpenResult.Nothing(skipped);
        }

        long firstId = await ResolveAsync(files[0], ct).ConfigureAwait(false);
        await _playback.PlayNowAsync([firstId], ct: ct).ConfigureAwait(false);
        string? firstTitle = _transient.Get(firstId)?.Title
            ?? (_library is null ? null : (await _library.GetAsync(firstId, ct).ConfigureAwait(false))?.Title);

        // The rest, behind the music. Not awaited: the criterion is about when sound starts, and 199 tag reads
        // are not going to happen inside it.
        LastFill = FillAsync(files, ct);
        _log.LogInformation("Open: playing {First}, filling {Rest} more ({Skipped} skipped)", files[0], files.Count - 1, skipped);
        return new OpenResult(files.Count, skipped, firstTitle);
    }

    /// <summary>
    /// Appends what the paths contain to the end of the queue without touching what is playing (E7-S1,
    /// <c>tunqio://queue</c>). Unlike <see cref="OpenAsync"/> this waits for every file: nothing is starting, so there
    /// is no first sound to hurry towards.
    /// </summary>
    public async Task<OpenResult> EnqueueAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        (IReadOnlyList<string> files, int skipped) = Collect(paths);
        if (files.Count == 0)
        {
            _log.LogInformation("Enqueue: nothing playable in {Count} item(s)", paths.Count);
            return OpenResult.Nothing(skipped);
        }

        var ids = new List<long>(files.Count);
        foreach (string file in files)
        {
            ids.Add(await ResolveAsync(file, ct).ConfigureAwait(false));
        }

        await _playback.EnqueueAsync(ids, ct).ConfigureAwait(false);
        _log.LogInformation("Enqueue: {Count} file(s) appended ({Skipped} skipped)", files.Count, skipped);
        return new OpenResult(files.Count, skipped, null);
    }

    /// <summary>
    /// The id one supported audio file plays under (its library row, else a transient track), or null when the path
    /// is not a file this app can play. For a caller that places the track itself: E7-S1's open-from-Explorer inserts
    /// it at the current position rather than replacing the queue (docs/ui-screens-and-flows.md flow 2).
    /// </summary>
    public async Task<long?> ResolveFileAsync(string path, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path) || !AudioFormats.IsSupported(path))
        {
            return null;
        }

        return await ResolveAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The background fill from the last <see cref="OpenAsync"/>, so a test — or anything that wants the queue
    /// complete rather than merely started — has something to await. Completed before the first call.
    /// </summary>
    public Task LastFill { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Every supported audio file the paths name, in file-name order, and how many items were not playable.
    /// A folder is walked in full (a dropped album is often disc sub-folders), ordered by path so the discs
    /// stay in order and the tracks inside each stay in theirs.
    /// </summary>
    internal static (IReadOnlyList<string> Files, int Skipped) Collect(IReadOnlyList<string> paths)
    {
        var files = new List<string>();
        int skipped = 0;
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                var found = new List<string>();
                foreach (string file in SafeWalk(path))
                {
                    if (AudioFormats.IsSupported(file))
                    {
                        found.Add(file);
                    }
                }

                if (found.Count == 0)
                {
                    skipped++;
                    continue;
                }

                found.Sort(StringComparer.OrdinalIgnoreCase);
                files.AddRange(found);
            }
            else if (File.Exists(path) && AudioFormats.IsSupported(path))
            {
                files.Add(path);
            }
            else
            {
                skipped++;
            }
        }

        // Loose files are sorted among themselves; a folder's contents keep the order the walk gave them, because
        // dropping "Disc 1" and "Disc 2" together should play disc 1 first whatever the two are called.
        return (files, skipped);
    }

    /// <summary>
    /// A recursive walk that survives what a real drive has in it: a folder the user cannot read, or a junction
    /// pointing somewhere they cannot. One unreadable sub-folder must not lose the other 199 files.
    /// </summary>
    private static IEnumerable<string> SafeWalk(string root) =>
        Directory.EnumerateFiles(root, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        });

    private async Task FillAsync(IReadOnlyList<string> files, CancellationToken ct)
    {
        var batch = new List<long>(BatchSize);
        for (int i = 1; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            batch.Add(await ResolveAsync(files[i], ct).ConfigureAwait(false));
            if (batch.Count == BatchSize)
            {
                await _playback.EnqueueAsync([.. batch], ct).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await _playback.EnqueueAsync([.. batch], ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The id to queue for one file: its library row if it has one, else a transient track read from the file.
    /// A tag read never fails outright — the reader falls back to the file name — so this always yields an id.
    /// </summary>
    private async Task<long> ResolveAsync(string path, CancellationToken ct)
    {
        if (_library is not null && await _library.GetByPathAsync(path, ct).ConfigureAwait(false) is { } indexed)
        {
            return indexed.Id;
        }

        TagReadResult read = await _tags.ReadAsync(path, TransientTrackStore.NoFolder, ct).ConfigureAwait(false);
        return _transient.Add(read.Track);
    }
}
