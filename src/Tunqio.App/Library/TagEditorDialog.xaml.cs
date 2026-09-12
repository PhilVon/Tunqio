using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tunqio.App.Controls;
using Tunqio.App.Shell;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>
/// The tag editor dialog (docs/ui-screens-and-flows.md, "Tag editor dialog"; flow 8). It owns nothing but the
/// layout: the values, the "(multiple values)" rule, the progress and the write are
/// <see cref="TagEditorViewModel"/>'s, and the undo is offered afterwards on the shell's notice bar rather than
/// here, because the dialog is gone by the time the user decides they did not mean it.
/// </summary>
public sealed partial class TagEditorDialog : ContentDialog
{
    private TagEditReport? _report;

    public TagEditorDialog(TagEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();
    }

    public TagEditorViewModel ViewModel { get; }

    /// <summary>The placeholder a batch's boxes show for a field the selection does not agree on.</summary>
    public static string Placeholder(bool isBatch) => isBatch ? TagEditorViewModel.MultipleValues : string.Empty;

    /// <summary>
    /// Opens the editor over <paramref name="tracks"/> and, once it has written something, leaves the undo on
    /// the shell's notice bar. Everything is resolved here rather than injected because the caller is a row
    /// menu in a list control, which has no container of its own.
    /// </summary>
    public static async Task ShowAsync(XamlRoot xamlRoot, IReadOnlyList<TrackDto> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            return;
        }

        // Touched so it exists and is watching before a write can be deferred; nothing else constructs it.
        _ = App.Services.GetRequiredService<ActiveTrackFile>();
        var dialog = new TagEditorDialog(App.Services.GetRequiredService<TagEditorViewModel>()) { XamlRoot = xamlRoot };
        await dialog.ViewModel.LoadAsync(tracks);
        await dialog.ShowAsync();

        if (dialog._report is not { } report || report.Written == 0)
        {
            return;
        }

        ITagEditor editor = App.Services.GetRequiredService<ITagEditor>();
        App.Services.GetRequiredService<ShellNotices>().ShowTagEdit(report, () => UndoAsync(editor));
    }

    private static async Task UndoAsync(ITagEditor editor)
    {
        TagEditReport? undone = await editor.UndoAsync();
        if (undone is not null)
        {
            App.Services.GetRequiredService<ShellNotices>().ShowTagEdit(undone, () => Task.CompletedTask);
        }
    }

    /// <summary>
    /// Confirm writes without closing the dialog: the progress bar and the per-file verdicts are inside it, and
    /// a dialog that vanished on the first click would take the report of the three files that failed with it.
    /// The user closes it when they have read what happened.
    /// </summary>
    private void OnConfirm(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ConfirmAsync().Forget("Tag edit");
    }

    private async Task ConfirmAsync()
    {
        TagEditReport? report = await ViewModel.ConfirmAsync();
        if (report is null)
        {
            return;
        }

        _report = report;

        // A clean write closes the dialog, and the shell's Undo bar is the report the user reads: it says how many
        // tracks were updated and it stays up long enough to be read, which a dialog dismissed by its own success
        // does not (Q-31 on T-137). The consequence is that the per-file verdict column is a FAILURE surface - on a
        // clean batch Apply(report) writes a verdict onto every row and this line takes them off the screen in the
        // same turn. That is deliberate, and it is why the column looks dead when you go looking for it.
        //
        // Which makes the other branch the one that matters: a failure must keep the dialog up, or the only
        // statement of what went wrong goes with it. tools/check-tag-editor.ps1 holds that still by making a file
        // read-only and asserting the dialog stays open with its verdict readable.
        if (report.Failed == 0 && report.Error is null)
        {
            Hide();
        }
    }
}
