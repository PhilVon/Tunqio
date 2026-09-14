using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Library;
using Windows.System;

namespace Tunqio.App.Tests;

/// <summary>
/// E6-S4: the table the shell listens to is the defaults with <c>shortcuts.&lt;action&gt;</c> applied. The shell
/// resolves a rebound key to the same action with the same delivery, the store's change is heard without a restart,
/// and the file only ever holds what differs from the code.
/// </summary>
public class ShortcutBindingsTests
{
    private static readonly KeyChord CtrlAltP = new(VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu);

    private static ShellShortcut Action(ShellCommand command, double amount = 0) =>
        ShellShortcuts.All.Single(s => s.Command == command && s.Amount == amount);

    [Fact]
    public void Every_row_has_its_own_action_id_and_a_name()
    {
        IEnumerable<string> ids = ShellShortcuts.All.Select(ShellShortcuts.ActionId);

        ids.Should().OnlyHaveUniqueItems("two rows on one setting key would fight over it");
        ids.Should().OnlyContain(id => id.All(char.IsAsciiLetterOrDigit) && char.IsLower(id[0]), "the id is a JSON key that is read by eye too");
        ShellShortcuts.All.Select(ShellShortcuts.ActionName).Should().OnlyContain(name => name.Length > 0).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_rows_that_carry_an_amount_are_named_apart()
    {
        ShellShortcuts.ActionId(Action(ShellCommand.Seek, 5)).Should().Be("seekForward5");
        ShellShortcuts.ActionId(Action(ShellCommand.Seek, -30)).Should().Be("seekBack30");
        ShellShortcuts.ActionId(Action(ShellCommand.Volume, ShellShortcuts.VolumeStep)).Should().Be("volumeUp");
        ShellShortcuts.ActionId(Action(ShellCommand.Volume, -ShellShortcuts.VolumeStep)).Should().Be("volumeDown");
        ShellShortcuts.ActionId(Action(ShellCommand.PlayPause)).Should().Be("playPause");
        ShellShortcuts.ActionName(Action(ShellCommand.Seek, -30)).Should().Be("Seek back 30 s");
        ShellShortcuts.ActionName(Action(ShellCommand.Volume, -ShellShortcuts.VolumeStep)).Should().Be("Volume down");
    }

    /// <summary>A command added to the table (T-73's rating keys, say) gets a row without anyone naming it here.</summary>
    [Fact]
    public void A_command_with_no_name_of_its_own_is_still_named()
    {
        var unnamed = new ShellShortcut((VirtualKey)0xFF, VirtualKeyModifiers.None, (ShellCommand)9999, 0, ShortcutDelivery.PreEmpt);

        ShellShortcuts.ActionName(unnamed).Should().Be("9999");
        ShellShortcuts.ActionId(unnamed).Should().Be("9999");
    }

    [Fact]
    public void With_nothing_stored_the_resolved_table_is_the_default_one()
    {
        var bindings = new ShortcutBindings(new FakeSettings());

        bindings.Table.Should().Equal(ShellShortcuts.All);
        ShellShortcuts.All.Select(ShellShortcuts.ActionId).Should().OnlyContain(id => bindings.IsDefault(id));
    }

    [Fact]
    public void Without_a_store_the_table_stays_on_its_defaults()
    {
        var bindings = new ShortcutBindings(null);

        bindings.Table.Should().Equal(ShellShortcuts.All);
        bindings.Set(Action(ShellCommand.PlayPause), CtrlAltP);
        bindings.Find(VirtualKey.Space, VirtualKeyModifiers.None, typing: false)!.Value.Command.Should().Be(ShellCommand.PlayPause);
    }

    // ---- AC-436: the shell honours a change without a restart -------------------------------------------------------

    [Fact]
    public void A_stored_binding_moves_the_action_and_keeps_its_delivery_and_amount()
    {
        var settings = new FakeSettings();
        settings.SetValue(SettingsKeys.Shortcut("seekBack30"), "Ctrl+Alt+J");
        var bindings = new ShortcutBindings(settings);

        ShellShortcut? found = bindings.Find(VirtualKey.J, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, typing: false);

        found.Should().NotBeNull();
        found!.Value.Command.Should().Be(ShellCommand.Seek);
        found.Value.Amount.Should().Be(-30);
        found.Value.Delivery.Should().Be(ShortcutDelivery.Accelerator, "delivery belongs to the action, not to the key");
        bindings.Find(VirtualKey.Left, VirtualKeyModifiers.Shift, typing: false).Should().BeNull("the default key is no longer bound");
        bindings.Accelerated.Should().Contain(found.Value);
    }

    [Fact]
    public void A_change_in_the_store_is_heard_while_the_shell_runs()
    {
        var settings = new FakeSettings();
        var bindings = new ShortcutBindings(settings);
        int changes = 0;
        bindings.Changed += (_, _) => changes++;

        bindings.Set(Action(ShellCommand.PlayPause), CtrlAltP);

        changes.Should().Be(1);
        bindings.Find(VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, typing: false)!.Value.Command.Should().Be(ShellCommand.PlayPause);
        bindings.Find(VirtualKey.Space, VirtualKeyModifiers.None, typing: false).Should().BeNull();
        bindings.ChordOf("playPause").Should().Be(CtrlAltP);
        bindings.Holder(CtrlAltP)!.Value.Command.Should().Be(ShellCommand.PlayPause);
        bindings.IsDefault("playPause").Should().BeFalse();
    }

    /// <summary>
    /// Two writes saved together, not as two flushes in flight: the second of those fails on the temp file the first
    /// holds, and the store has already marked its snapshot clean, so the asker's binding would never reach the file.
    /// </summary>
    [Fact]
    public async Task Taking_a_key_from_another_action_is_one_save_Async()
    {
        string path = Path.Combine(Path.GetTempPath(), "tunqio-shortcuts-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            using (var store = new JsonSettingsStore(path))
            {
                var bindings = new ShortcutBindings(store);
                bindings.Move(Action(ShellCommand.MiniPlayer), Action(ShellCommand.PlayPause), new KeyChord(VirtualKey.M, VirtualKeyModifiers.Control));
                await bindings.WaitForSavesAsync();
            }

            using var relaunched = new JsonSettingsStore(path);
            relaunched.GetValue<string?>(SettingsKeys.Shortcut("playPause"), null).Should().Be("Ctrl+M");
            relaunched.GetValue<string?>(SettingsKeys.Shortcut("miniPlayer"), null).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void A_key_that_is_not_a_shortcut_setting_changes_nothing()
    {
        var settings = new FakeSettings();
        var bindings = new ShortcutBindings(settings);
        int changes = 0;
        bindings.Changed += (_, _) => changes++;

        settings.SetValue(SettingsKeys.UiTheme, "dark");

        changes.Should().Be(0);
    }

    [Fact]
    public void The_typing_rule_stays_with_the_action_on_its_new_key()
    {
        var settings = new FakeSettings();
        settings.SetValue(SettingsKeys.Shortcut("diagnostics"), "Ctrl+Alt+X");
        settings.SetValue(SettingsKeys.Shortcut("mute"), "Ctrl+Alt+Y");
        var bindings = new ShortcutBindings(settings);

        bindings.Find(VirtualKey.X, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, typing: true)!.Value.Command.Should().Be(ShellCommand.Diagnostics);
        bindings.Find(VirtualKey.Y, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, typing: true).Should().BeNull();
    }

    // ---- what the file holds ----------------------------------------------------------------------------------------

    [Fact]
    public void An_empty_value_leaves_the_action_unbound()
    {
        var settings = new FakeSettings();
        settings.SetValue(SettingsKeys.Shortcut("mute"), "");
        var bindings = new ShortcutBindings(settings);

        bindings.Find(VirtualKey.M, VirtualKeyModifiers.None, typing: false).Should().BeNull();
        bindings.ChordOf("mute").Should().BeNull();
        bindings.Table.Should().HaveCount(ShellShortcuts.All.Count - 1);
    }

    [Fact]
    public void A_value_that_names_no_key_leaves_the_default_in_force()
    {
        var settings = new FakeSettings();
        settings.SetValue(SettingsKeys.Shortcut("mute"), "Hyper+Q");
        var bindings = new ShortcutBindings(settings);

        bindings.Find(VirtualKey.M, VirtualKeyModifiers.None, typing: false)!.Value.Command.Should().Be(ShellCommand.Mute);
    }

    [Fact]
    public void A_hand_edited_file_that_puts_two_actions_on_one_key_gives_it_to_the_first()
    {
        var settings = new FakeSettings();
        settings.SetValue(SettingsKeys.Shortcut("repeat"), "M"); // Mute is earlier in the table and has M by default
        var bindings = new ShortcutBindings(settings);

        bindings.Find(VirtualKey.M, VirtualKeyModifiers.None, typing: false)!.Value.Command.Should().Be(ShellCommand.Mute);
        bindings.ChordOf("repeat").Should().BeNull("a chord that names two actions means one silently never happens");
        bindings.Table.Select(s => (s.Key, s.Modifiers)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Binding_an_action_to_its_own_default_removes_the_key_rather_than_storing_it()
    {
        var settings = new FakeSettings();
        settings.SetValue(SettingsKeys.Shortcut("playPause"), "Ctrl+Alt+P");
        var bindings = new ShortcutBindings(settings);

        bindings.Set(Action(ShellCommand.PlayPause), new KeyChord(VirtualKey.Space, VirtualKeyModifiers.None));

        settings.GetValue<string?>(SettingsKeys.Shortcut("playPause"), null).Should().BeNull();
        bindings.IsDefault("playPause").Should().BeTrue();
    }

    // ---- AC-438: persistence and reset ------------------------------------------------------------------------------

    [Fact]
    public async Task Bindings_survive_a_relaunch_and_reset_removes_every_key_Async()
    {
        string path = Path.Combine(Path.GetTempPath(), "tunqio-shortcuts-" + Guid.NewGuid().ToString("N"), "settings.json");
        try
        {
            using (var first = new JsonSettingsStore(path))
            {
                var bindings = new ShortcutBindings(first);
                bindings.Set(Action(ShellCommand.PlayPause), CtrlAltP);
                bindings.Set(Action(ShellCommand.Mute), null);
                await bindings.WaitForSavesAsync(); // the two saves run one after the other, and the file is theirs until then
            }

            // Parsed rather than matched as text: System.Text.Json escapes '+' as + in the file, and it reads back as '+'.
            System.Text.Json.Nodes.JsonObject json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            json["shortcuts.playPause"]!.GetValue<string>().Should().Be("Ctrl+Alt+P");
            json["shortcuts.mute"]!.GetValue<string>().Should().BeEmpty();

            using (var second = new JsonSettingsStore(path))
            {
                var relaunched = new ShortcutBindings(second);
                relaunched.Find(VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, typing: false)!.Value.Command.Should().Be(ShellCommand.PlayPause);
                relaunched.Find(VirtualKey.M, VirtualKeyModifiers.None, typing: false).Should().BeNull();

                relaunched.Reset();
                await relaunched.WaitForSavesAsync();

                relaunched.Table.Should().Equal(ShellShortcuts.All);
            }

            (await File.ReadAllTextAsync(path)).Should().NotContain("shortcuts.", "reset removes the keys rather than writing the defaults into the file");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
