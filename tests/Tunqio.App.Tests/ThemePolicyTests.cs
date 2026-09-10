using Tunqio.App.Shell;
using Tunqio.Core;

namespace Tunqio.App.Tests;

/// <summary>
/// E2-S1, AC-69: the half of "follows the system and can be overridden" that is a rule rather than a repaint. That
/// the repaint does not flash is a property of where it is applied — the shell's root, so brushes already in the
/// tree re-evaluate and nothing is unloaded — and is checked by driving the window, not from here.
/// </summary>
public class ThemePolicyTests
{
    private readonly FakeSettings _settings = new();

    [Fact]
    public void An_unset_theme_follows_the_system() =>
        ThemePolicy.Read(_settings).Should().Be(ThemePreference.System);

    [Theory]
    [InlineData("light", ThemePreference.Light)]
    [InlineData("dark", ThemePreference.Dark)]
    [InlineData("system", ThemePreference.System)]
    [InlineData("Dark", ThemePreference.Dark)]
    [InlineData("  LIGHT  ", ThemePreference.Light)]
    public void The_stored_value_is_read_case_and_space_insensitively(string stored, ThemePreference expected) =>
        ThemePolicy.Parse(stored).Should().Be(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sepia")]
    [InlineData(null)]
    public void Anything_unrecognised_follows_the_system_rather_than_failing(string? stored) =>
        ThemePolicy.Parse(stored).Should().Be(
            ThemePreference.System, "a theme nobody can parse is not worth refusing to start over");

    [Theory]
    [InlineData(ThemePreference.System)]
    [InlineData(ThemePreference.Light)]
    [InlineData(ThemePreference.Dark)]
    public void A_written_preference_reads_back_as_itself(ThemePreference preference)
    {
        ThemePolicy.Write(_settings, preference);

        ThemePolicy.Read(_settings).Should().Be(preference);
    }

    [Fact]
    public void The_name_written_is_the_one_the_settings_table_documents()
    {
        ThemePolicy.Write(_settings, ThemePreference.Dark);

        _settings.GetValue<string?>(SettingsKeys.UiTheme, null).Should().Be("dark");
    }

    [Fact]
    public void The_documented_default_is_what_an_absent_setting_means() =>
        ThemePolicy.Parse(SettingsKeys.Defaults.UiTheme).Should().Be(ThemePreference.System);
}
