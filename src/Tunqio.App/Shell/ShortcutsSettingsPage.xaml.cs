using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;

namespace Tunqio.App.Shell;

/// <summary>Code-behind for Settings › Shortcuts; see the XAML for the design notes.</summary>
public sealed partial class ShortcutsSettingsPage : Page
{
    public ShortcutsSettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<ShortcutsSettingsViewModel>();
        InitializeComponent();
    }

    public ShortcutsSettingsViewModel ViewModel { get; }

    /// <summary>Collapsed while <paramref name="condition"/> holds.</summary>
    public static Visibility Unless(bool condition) => condition ? Visibility.Collapsed : Visibility.Visible;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // For the harness, which counts the rows it finds against the rows the table has.
        Serilog.Log.Debug("Shortcuts settings page shows {Rows} rows", ViewModel.Rows.Count);
    }

    private void OnCaptured(object sender, KeyChord chord)
    {
        if (sender is not FrameworkElement { Tag: ShortcutRow row })
        {
            return;
        }

        if (ViewModel.Bind(row, chord) == BindOutcome.Conflict)
        {
            AskAboutConflictAsync().Forget("Resolve a shortcut conflict");
        }
    }

    /// <summary>
    /// The conflict, put as a question before anything is written: the title names the holder, the body says what
    /// taking the key leaves behind. A dialog rather than an InfoBar because the row that asked may be well below
    /// the top of the page, and a message that is scrolled out of sight is a message that was not given.
    /// </summary>
    private async Task AskAboutConflictAsync()
    {
        if (ViewModel.Conflict is not { } conflict)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = conflict.Title,
            Content = conflict.Consequence,
            PrimaryButtonText = "Use it anyway",
            CloseButtonText = "Keep " + conflict.Chord + " for " + conflict.Holder.Name,
            DefaultButton = ContentDialogButton.Close,
        };
        AutomationProperties.SetName(dialog, "Shortcut conflict");
        ContentDialogResult answer = await dialog.ShowAsync();
        if (answer == ContentDialogResult.Primary)
        {
            ViewModel.TakeConflictingKey();
        }
        else
        {
            ViewModel.KeepConflictingKey();
        }
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ShortcutRow row })
        {
            ViewModel.Unbind(row);
        }
    }

    private void OnReset(object sender, RoutedEventArgs e) => ViewModel.RestoreDefaults();
}
