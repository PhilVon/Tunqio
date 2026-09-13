using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.Core;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Shell;

/// <summary>
/// Settings › Appearance (E6-S3): the theme, and the two audio-reactive theming settings E4-S9 put on the Visualization
/// page because there was no Appearance page yet (T-151). Each is written to the store as it changes; the window
/// repaints on <c>ui.theme</c> and the reactive theme controller follows its own two keys, so nothing here needs a
/// reference to the window.
/// </summary>
public sealed partial class AppearanceSettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settings;
    private bool _seeding;

    /// <summary>The three choices in the order the page lists them.</summary>
    public static readonly IReadOnlyList<ThemePreference> Themes = [ThemePreference.System, ThemePreference.Light, ThemePreference.Dark];

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    /// <summary><c>ui.reactiveTheming</c>.</summary>
    [ObservableProperty]
    public partial bool ReactiveTheming { get; set; }

    /// <summary><c>ui.reactiveSmoothing</c>, 0 to 1.</summary>
    [ObservableProperty]
    public partial float ReactiveSmoothing { get; set; }

    public AppearanceSettingsViewModel(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _seeding = true;
        try
        {
            ThemeIndex = Math.Max(0, IndexOf(ThemePolicy.Read(settings)));
            ReactiveTheming = settings.GetValue(SettingsKeys.UiReactiveTheming, SettingsKeys.Defaults.UiReactiveTheming);
            ReactiveSmoothing = settings.GetValue(SettingsKeys.UiReactiveSmoothing, SettingsKeys.Defaults.UiReactiveSmoothing);
        }
        finally
        {
            _seeding = false;
        }
    }

    /// <summary>What a theme is called on the page.</summary>
    public static string ThemeName(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => "Light",
        ThemePreference.Dark => "Dark",
        _ => "Use Windows setting",
    };

    /// <summary>What the smoothing setting means in seconds (see <see cref="VisualizationSettingsViewModel"/>'s history, T-151).</summary>
    public string SmoothingDescription => string.Format(
        CultureInfo.CurrentCulture,
        "{0:0.0} s to move 63% of the way to the colour the music is asking for (0.5 s at the left, 4.0 s at the right).",
        new ReactiveThemeOptions(true, ReactiveSmoothing).TimeConstant.TotalSeconds);

    private static int IndexOf(ThemePreference preference)
    {
        for (int i = 0; i < Themes.Count; i++)
        {
            if (Themes[i] == preference)
            {
                return i;
            }
        }

        return -1;
    }

    partial void OnThemeIndexChanged(int value)
    {
        if (_seeding || value < 0 || value >= Themes.Count)
        {
            return;
        }

        ThemePolicy.Write(_settings, Themes[value]);
        _settings.FlushAsync().Forget("Save theme");
    }

    partial void OnReactiveThemingChanged(bool value)
    {
        if (_seeding)
        {
            return;
        }

        _settings.SetValue(SettingsKeys.UiReactiveTheming, value);
        _settings.FlushAsync().Forget("Save reactive theming");
    }

    partial void OnReactiveSmoothingChanged(float value)
    {
        OnPropertyChanged(nameof(SmoothingDescription));
        if (_seeding)
        {
            return;
        }

        _settings.SetValue(SettingsKeys.UiReactiveSmoothing, value);
        _settings.FlushAsync().Forget("Save reactive smoothing");
    }
}
