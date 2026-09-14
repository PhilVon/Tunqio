using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tunqio.App.Controls;

namespace Tunqio.App.Shell;

/// <summary>Code-behind for the first-run welcome; see the XAML for the design notes.</summary>
public sealed partial class FirstRunWelcomeDialog : ContentDialog
{
    public FirstRunWelcomeDialog(FirstRunWelcomeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        InitializeComponent();
        foreach (ThemePreference theme in AppearanceSettingsViewModel.Themes)
        {
            ThemeChoices.Items.Add(AppearanceSettingsViewModel.ThemeName(theme));
        }

        ThemeChoices.SelectedIndex = ViewModel.Appearance.ThemeIndex;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FirstRunWelcomeViewModel.Step) or nameof(FirstRunWelcomeViewModel.AddedFolders))
            {
                SyncButtons();
            }
        };
        ViewModel.Output.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OutputSettingsViewModel.SelectedDevice) or nameof(OutputSettingsViewModel.Devices))
            {
                Bindings.Update();
            }
        };
        SyncButtons();
    }

    public FirstRunWelcomeViewModel ViewModel { get; }

    /// <summary>The device selection as an object, because a ComboBox's SelectedItem binding is to object (as on the Output page).</summary>
    public object? SelectedDeviceObject
    {
        get => ViewModel.Output.SelectedDevice;
        set
        {
            if (value is OutputDeviceRow row)
            {
                ViewModel.Output.SelectedDevice = row;
            }
        }
    }

    public static Visibility Shows(int step, int which) => step == which ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Unless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool Not(bool value) => !value;

    private void SyncButtons()
    {
        PrimaryButtonText = ViewModel.NextLabel;
        IsSecondaryButtonEnabled = ViewModel.CanGoBack;
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Next keeps the dialog open; Done on the last step lets it close.
        if (!ViewModel.IsLastStep)
        {
            args.Cancel = true;
        }

        ViewModel.Next();
    }

    private void OnSecondaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true;
        ViewModel.Back();
    }

    private void OnCloseClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) => ViewModel.SkipAll();

    // Esc closes a ContentDialog without a button click; it is Skip all too, so every way out records the welcome.
    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args) => ViewModel.SkipAll();

    private void OnAddFolder(object sender, RoutedEventArgs e) => ViewModel.AddFolderAsync().Forget("First-run add folder");

    private void OnPickFolder(object sender, RoutedEventArgs e) => ViewModel.PickFolderAsync().Forget("First-run pick folder");

    private void OnTestTone(object sender, RoutedEventArgs e) => ViewModel.Output.PlayTestToneAsync().Forget("First-run test tone");
}
