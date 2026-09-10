using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Tunqio.App.Shell;

/// <summary>
/// The shell's notice area (E2-S7): one <see cref="InfoBar"/> per notice. Everything it decides is in
/// <see cref="ShellNotices"/>; see the XAML for the design notes.
/// </summary>
public sealed partial class NoticePanel : UserControl
{
    public NoticePanel() => InitializeComponent();

    /// <summary>The notices to show; the shell owns the instance and hands it here.</summary>
    public ShellNotices? ViewModel
    {
        get => (ShellNotices?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(ShellNotices), typeof(NoticePanel), new PropertyMetadata(null));

    /// <summary>The shell's severity as the <see cref="InfoBar"/>'s, which is the only place the two vocabularies meet.</summary>
    public static InfoBarSeverity Level(StartupNoticeSeverity severity) => severity switch
    {
        StartupNoticeSeverity.Error => InfoBarSeverity.Error,
        StartupNoticeSeverity.Warning => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Informational,
    };

    private void OnClosed(InfoBar sender, object args)
    {
        if (sender?.DataContext is ShellNotice notice)
        {
            ViewModel?.Dismiss(notice);
        }
    }

    /// <summary>
    /// The bar's action — "Use default device", "Switch back". Void over a task, which is the shape XAML gives; the
    /// exception is logged rather than lost, per the error-handling policy in docs/solution-structure.md.
    /// </summary>
    private void OnAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShellNotice { Action: { } action } })
        {
            return;
        }

        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Serilog.Log.Error(ex, "A notice action failed");
            }
        }
    }
}
