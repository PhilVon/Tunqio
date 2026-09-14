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

    private int _artLoad;

    public TagEditorDialog(TagEditorViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelChanged;
    }

    public TagEditorViewModel ViewModel { get; }

    /// <summary>Visible when <paramref name="value"/> equals <paramref name="shown"/>; a function that returns Visibility itself, because a bool one cast in XAML does not compile (see HasError).</summary>
    public static Visibility When(bool value, bool shown) => value == shown ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The placeholder a box shows: "(multiple values)" while the selection disagrees on its field and the box is
    /// untouched, the same flag the help text reads. It was once the batch flag, which put the placeholder on a field
    /// every track agrees is blank and read to a sighted user as "these differ" when they do not (T-204).
    /// </summary>
    public static string Placeholder(bool isMixed) => isMixed ? TagEditorViewModel.MultipleValues : string.Empty;

    /// <summary>
    /// The box's automation help text: the placeholder's meaning, for a screen reader that cannot reach the
    /// placeholder (T-123). Empty unless the selection disagrees on the field and the user has not typed in it, so it
    /// is not read out for a field they agree on or one that will now be written.
    /// </summary>
    public static string MixedHelp(bool isMixed) => isMixed ? TagEditorViewModel.MultipleValuesHelp : string.Empty;

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

    private void OnReplaceArt(object sender, RoutedEventArgs e) => ViewModel.ReplaceArtAsync().Forget("Replace cover art");

    private void OnRemoveArt(object sender, RoutedEventArgs e) => ViewModel.RemoveArt();

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TagEditorViewModel.Art))
        {
            LoadArtAsync(ViewModel.Art).Forget("Load cover art preview");
        }
    }

    /// <summary>
    /// Shows the view model's cover in the preview. The bytes are decoded from memory rather than from the art
    /// cache, because a picture picked for Replace is in no cache yet. A load that finishes after a newer one
    /// began is dropped, so a slow decode cannot paint a picture the user has since removed.
    /// </summary>
    private async Task LoadArtAsync(Core.Library.EmbeddedPicture? picture)
    {
        int load = ++_artLoad;
        if (picture is null)
        {
            ArtImage.Source = null;
            return;
        }

        var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 192 };
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(picture.Bytes.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        try
        {
            await image.SetSourceAsync(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            // Not an image the decoder knows. The summary still says what the file holds; the box stays empty.
            Serilog.Log.Warning(ex, "The tag editor could not decode the embedded picture for its preview");
            return;
        }

        if (load == _artLoad)
        {
            ArtImage.Source = image;
        }
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
