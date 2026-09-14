using Microsoft.UI.Xaml.Controls;

namespace Tunqio.App.Crash;

/// <summary>Code-behind for the crash report dialog; the choices are the view model's.</summary>
public sealed partial class CrashReportDialog : ContentDialog
{
    public CrashReportDialog(CrashReportViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();
    }

    public CrashReportViewModel ViewModel { get; }

    private void OnKeep(ContentDialog sender, ContentDialogButtonClickEventArgs args) => ViewModel.Keep();

    private void OnDelete(ContentDialog sender, ContentDialogButtonClickEventArgs args) => ViewModel.Delete();

    // Every way out records a choice: after a button this does nothing; after Esc it keeps.
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args) => ViewModel.CloseWithoutChoice();
}
