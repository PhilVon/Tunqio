using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Tunqio.App.Shell;

/// <summary>
/// The queue panel (E2-S5): the pinned current track, the upcoming items with drag reorder and remove, "clear
/// upcoming" and the time left. Everything it decides is in <see cref="QueueViewModel"/>; what is here is the
/// bindings and the two buttons that need a row to act on.
/// </summary>
/// <remarks>
/// There is no drag handler here. The reorder is the <see cref="ListView"/>'s own, and what it does is move the row
/// in the bound collection — which <see cref="QueueViewModel"/> is watching. So a row dragged with the pointer and a
/// row moved with the keyboard reach the session by the same path, and neither needs a line in this file. Letting the
/// list move the row first is also what keeps it from snapping back for the frame or two before the session answers.
/// </remarks>
public sealed partial class QueuePanel : UserControl
{
    public QueuePanel() => InitializeComponent();

    /// <summary>The panel's state and commands; the shell owns the instance and hands it here.</summary>
    public QueueViewModel? ViewModel
    {
        get => (QueueViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(QueueViewModel), typeof(QueuePanel), new PropertyMetadata(null));

    private void OnClearUpcoming(object sender, RoutedEventArgs e) => Run(vm => vm.ClearUpcomingAsync());

    private void OnRemovePinned(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.NowPlaying is { } row)
        {
            Run(vm => vm.RemoveAsync(row));
        }
    }

    private void OnRemoveRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: QueueRow row })
        {
            Run(vm => vm.RemoveAsync(row));
        }
    }

    /// <summary>
    /// Void over a task, which is the shape XAML gives; the exception is logged rather than lost, per the
    /// error-handling policy in docs/solution-structure.md.
    /// </summary>
    private void Run(Func<QueueViewModel, Task> action)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        _ = SafeAsync(action, vm);

        static async Task SafeAsync(Func<QueueViewModel, Task> action, QueueViewModel vm)
        {
            try
            {
                await action(vm).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Serilog.Log.Error(e, "A queue panel action failed");
            }
        }
    }
}
