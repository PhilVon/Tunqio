using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.Library;

namespace Tunqio.App.Shell;

/// <summary>
/// The shell's half of "open files and drop" (E2-S4): runs a picker or takes what was dropped, hands the paths
/// to <see cref="OpenFilesService"/>, and turns the answer into the notice the window shows.
/// </summary>
/// <remarks>
/// It exists so that everything between the user's gesture and the queue is testable without a window. The one
/// thing it will not do is stay silent about a drop that did nothing: a folder of photographs landing on the
/// window and simply being ignored is indistinguishable, from where the user is sitting, from a bug.
/// </remarks>
public sealed class OpenCoordinator
{
    private readonly OpenFilesService _open;
    private readonly IAudioFilePicker _picker;
    private readonly ILogger _log;

    public OpenCoordinator(OpenFilesService open, IAudioFilePicker picker, ILogger<OpenCoordinator>? log = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(picker);
        _open = open;
        _picker = picker;
        _log = log ?? NullLogger<OpenCoordinator>.Instance;
    }

    /// <summary>The notice to show for the last open, or null when there is nothing worth saying.</summary>
    public StartupNotice? LastNotice { get; private set; }

    /// <summary>Runs the file picker and plays what was chosen.</summary>
    public async Task<OpenResult?> OpenFilesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> paths = await _picker.PickFilesAsync(ct).ConfigureAwait(false);
        return paths.Count == 0 ? Cancelled() : await OpenAsync(paths, ct).ConfigureAwait(false);
    }

    /// <summary>Runs the folder picker and plays what is in the folder.</summary>
    public async Task<OpenResult?> OpenFolderAsync(CancellationToken ct = default)
    {
        string? folder = await _picker.PickFolderAsync(ct).ConfigureAwait(false);
        return folder is null ? Cancelled() : await OpenAsync([folder], ct).ConfigureAwait(false);
    }

    /// <summary>Plays what was dropped on the window: files, folders, or a mixture.</summary>
    public Task<OpenResult?> OpenDroppedAsync(IReadOnlyList<string> paths, CancellationToken ct = default) =>
        OpenAsync(paths, ct);

    private async Task<OpenResult?> OpenAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(paths);
        OpenResult result = await _open.OpenAsync(paths, ct).ConfigureAwait(false);
        LastNotice = NoticeFor(result, paths.Count);
        _log.LogInformation(
            "Open: {Playable} playable, {Skipped} skipped, from {Items} item(s)", result.Playable, result.Skipped, paths.Count);
        return result;
    }

    /// <summary>
    /// What to tell the user. Nothing at all for the ordinary case — the music starting is the feedback, and an
    /// InfoBar saying "playing" over the top of it is noise. Something only when the answer was surprising: a
    /// drop with nothing playable in it, or one where part of what they dropped was left out.
    /// </summary>
    public static StartupNotice? NoticeFor(OpenResult result, int itemCount)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.StartedPlaying)
        {
            return new StartupNotice(
                "Nothing to play",
                itemCount == 1
                    ? "That item has no audio Tunqio can play, so the queue is unchanged."
                    : string.Create(
                        CultureInfo.InvariantCulture,
                        $"None of those {itemCount} items has audio Tunqio can play, so the queue is unchanged."),
                StartupNoticeSeverity.Warning);
        }

        if (result.Skipped == 0)
        {
            return null;
        }

        return new StartupNotice(
            "Some items were skipped",
            string.Create(
                CultureInfo.InvariantCulture,
                $"Playing {Files(result.Playable)}. {Items(result.Skipped)} had no audio Tunqio can play."),
            StartupNoticeSeverity.Informational);
    }

    private static string Files(int count) =>
        count == 1 ? "1 file" : count.ToString(CultureInfo.InvariantCulture) + " files";

    private static string Items(int count) =>
        count == 1 ? "1 item" : count.ToString(CultureInfo.InvariantCulture) + " items";

    /// <summary>A cancelled picker is not an event: no notice, and whatever was playing keeps playing.</summary>
    private OpenResult? Cancelled()
    {
        LastNotice = null;
        return null;
    }
}
