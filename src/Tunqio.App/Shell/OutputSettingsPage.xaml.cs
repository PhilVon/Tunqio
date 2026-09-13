using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Tunqio.App.Controls;

namespace Tunqio.App.Shell;

/// <summary>Code-behind for Settings › Output; see the XAML for the design notes.</summary>
public sealed partial class OutputSettingsPage : Page
{
    private bool _syncingBuffer;

    public OutputSettingsPage()
    {
        ViewModel = App.Services.GetRequiredService<OutputSettingsViewModel>();
        InitializeComponent();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(OutputSettingsViewModel.Exclusive) or nameof(OutputSettingsViewModel.BufferMs))
            {
                FillBuffers(); // the default's label names the mode's default
            }
        };
    }

    public OutputSettingsViewModel ViewModel { get; }

    /// <summary>The selection as an object, because a ComboBox's SelectedItem binding is to object.</summary>
    public object? SelectedDeviceObject
    {
        get => ViewModel.SelectedDevice;
        set
        {
            if (value is OutputDeviceRow row)
            {
                ViewModel.SelectedDevice = row;
            }
        }
    }

    public static Visibility Unless(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Load();
        Bindings.Update();
        FillBuffers();
    }

    private void FillBuffers()
    {
        _syncingBuffer = true;
        try
        {
            BufferList.Items.Clear();
            foreach (int ms in OutputSettingsViewModel.BufferChoices)
            {
                BufferList.Items.Add(OutputSettingsViewModel.BufferLabel(ms, ViewModel.Exclusive));
            }

            int index = OutputSettingsViewModel.BufferChoices.ToList().IndexOf(ViewModel.BufferMs);
            BufferList.SelectedIndex = index >= 0 ? index : 0;
        }
        finally
        {
            _syncingBuffer = false;
        }
    }

    private void OnBufferChosen(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingBuffer && BufferList.SelectedIndex >= 0)
        {
            ViewModel.BufferMs = OutputSettingsViewModel.BufferChoices[BufferList.SelectedIndex];
        }
    }

    private void OnTestTone(object sender, RoutedEventArgs e) => ViewModel.PlayTestToneAsync().Forget("Test tone");

    private void OnUseDefault(object sender, RoutedEventArgs e) => ViewModel.UseSystemDefaultAsync().Forget("Use system default output");
}
