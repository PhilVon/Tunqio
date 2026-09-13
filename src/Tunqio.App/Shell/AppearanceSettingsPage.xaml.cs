using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Tunqio.App.Shell;

/// <summary>Code-behind for Settings › Appearance; see the XAML for the design notes.</summary>
public sealed partial class AppearanceSettingsPage : Page
{
    public AppearanceSettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<AppearanceSettingsViewModel>();
        InitializeComponent();
        foreach (ThemePreference theme in AppearanceSettingsViewModel.Themes)
        {
            ThemeChoices.Items.Add(AppearanceSettingsViewModel.ThemeName(theme));
        }

        ThemeChoices.SelectedIndex = ViewModel.ThemeIndex;
    }

    public AppearanceSettingsViewModel ViewModel { get; }
}
