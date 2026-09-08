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
}
