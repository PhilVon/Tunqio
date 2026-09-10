using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Tunqio.App.Shell;

/// <summary>
/// The diagnostics overlay (E2-S8). Everything it says is <see cref="DiagnosticsViewModel"/>'s; what is here is
/// the bindings and the one thing a view model cannot do, which is reach the clipboard.
/// </summary>
public sealed partial class DiagnosticsOverlay : UserControl
{
    public DiagnosticsOverlay()
    {
        InitializeComponent();
        AttributionText.Text = Core.ThirdPartyAttribution.Bass;
    }

    /// <summary>The values to show; the shell owns the instance and hands it here.</summary>
    public DiagnosticsViewModel? ViewModel
    {
        get => (DiagnosticsViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(DiagnosticsViewModel), typeof(DiagnosticsOverlay), new PropertyMetadata(null));

    /// <summary>
    /// Puts the whole overlay on the clipboard — what someone pastes into a bug report. The text is the view
    /// model's, so what is copied is exactly what is on the screen and not a second rendering of it.
    /// </summary>
    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(vm.Text);
        Clipboard.SetContent(package);
    }
}
