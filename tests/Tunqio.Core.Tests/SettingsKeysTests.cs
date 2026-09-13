using System.Reflection;

namespace Tunqio.Core.Tests;

/// <summary>SettingsKeys mirrors the "Settings keys" table in docs/solution-structure.md.</summary>
public class SettingsKeysTests
{
    [Fact]
    public void Every_documented_key_has_a_constant()
    {
        string doc = File.ReadAllText(RepoPaths.File("docs", "solution-structure.md"));
        string table = doc[doc.IndexOf("## Settings keys", StringComparison.Ordinal)..];
        table = table[..table.IndexOf("## Coding conventions", StringComparison.Ordinal)];

        IEnumerable<string> documented = table.Split('\n')
            .Where(l => l.StartsWith("| `", StringComparison.Ordinal))
            .Select(l => l.Split('`')[1])
            .Where(k => !k.Contains('<', StringComparison.Ordinal)) // templated keys (viz.params.<preset>.<name>, shortcuts.<action>)
            .Select(k => k.Split(" / ")[0]); // "ui.closeToTray / ui.minimizeToTray"

        HashSet<string> constants = typeof(SettingsKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (string key in documented)
        {
            constants.Should().Contain(key, "docs/solution-structure.md documents it");
        }

        constants.Should().Contain("ui.minimizeToTray");
    }

    /// <summary>E5-S5: a hover preview is unexpected sound (R-15), so it and its one-time offer both start off.</summary>
    [Fact]
    public void Hover_preview_starts_off_and_not_yet_offered()
    {
        SettingsKeys.Defaults.UiHoverPreview.Should().BeFalse();
        SettingsKeys.Defaults.UiHoverPreviewOffered.Should().BeFalse();
    }

    /// <summary>
    /// E5-S3, Q-72. The mode table asks for Ambient Glow behind the art in Discovery; Phil chose one preset for every
    /// mode, with Ambient Glow as what a first run draws, over a preset each mode would choose or remember.
    /// </summary>
    [Fact]
    public void A_first_run_draws_ambient_glow()
    {
        SettingsKeys.Defaults.VizPreset.Should().Be("ambient-glow");
        File.Exists(RepoPaths.File("presets", "ambient-glow", "preset.json")).Should().BeTrue(
            "the default has to name a preset that ships, or a first run falls back to whatever the core draws first");
    }
}
