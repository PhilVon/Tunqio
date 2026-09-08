using Microsoft.UI.Xaml;

namespace Tunqio.App;

/// <summary>Application entry. The startup sequence in docs/solution-structure.md lands with E0-S6 and E2-S1.</summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
