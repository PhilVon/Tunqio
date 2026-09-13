using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;

namespace Tunqio.App.Shell;

/// <summary>Code-behind for Settings › Playback; see the XAML for the design notes.</summary>
public sealed partial class PlaybackSettingsPage : Page
{
    public PlaybackSettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<PlaybackSettingsViewModel>();
        InitializeComponent();
        // Filled here rather than bound: the names are the view model's, and SelectedIndex is bound after the items exist.
        foreach (Core.Playback.ReplayGainMode mode in PlaybackSettingsViewModel.Modes)
        {
            ReplayGainMode.Items.Add(PlaybackSettingsViewModel.ModeName(mode));
        }

        ReplayGainMode.SelectedIndex = ViewModel.ReplayGainIndex;
    }

    public PlaybackSettingsViewModel ViewModel { get; }
}
