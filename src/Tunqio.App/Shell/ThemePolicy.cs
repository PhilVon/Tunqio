using Tunqio.Core;

namespace Tunqio.App.Shell;

/// <summary>What <c>ui.theme</c> can say. <see cref="System"/> is the default and means "whatever Windows is".</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Reads and writes <c>ui.theme</c> (E2-S1, AC-69). Pure, so "follows the system unless overridden" is a table
/// rather than something you can only find out by changing Windows' theme and watching.
/// </summary>
/// <remarks>
/// Anything unrecognised in the settings file is <see cref="ThemePreference.System"/>, not an error: a theme is not
/// worth refusing to start over, and the documented default is the safe answer.
/// </remarks>
public static class ThemePolicy
{
    /// <summary>Parses <c>ui.theme</c>; anything unrecognised is the documented default.</summary>
    public static ThemePreference Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemePreference.Light,
        "dark" => ThemePreference.Dark,
        "system" => ThemePreference.System,
        _ => ThemePreference.System,
    };

    /// <summary>The value <c>ui.theme</c> stores for <paramref name="preference"/>.</summary>
    public static string Name(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => "light",
        ThemePreference.Dark => "dark",
        _ => "system",
    };

    /// <summary>Reads the stored preference.</summary>
    public static ThemePreference Read(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Parse(settings.GetValue<string?>(SettingsKeys.UiTheme, null));
    }

    /// <summary>Writes the preference back (the caller flushes).</summary>
    public static void Write(ISettingsStore settings, ThemePreference preference)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SetValue(SettingsKeys.UiTheme, Name(preference));
    }
}
