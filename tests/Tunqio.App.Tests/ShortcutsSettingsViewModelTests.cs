using Tunqio.App.Shell;
using Tunqio.Core;
using Windows.System;

namespace Tunqio.App.Tests;

/// <summary>
/// E6-S4: Settings › Shortcuts. Every row of the table is on the page with its key; a chord is bound by pressing it;
/// a chord another action holds is named and not taken until the person says so, and then the other row reads Not
/// bound; Clear and Reset do what they say. What cannot be tested here — that the keystroke reaches the capture
/// button past the shell root — is <c>ShortcutCaptureButton</c> and <c>MainWindow.IsCapturingShortcut</c>, and
/// Phil's hands (AC-440).
/// </summary>
public class ShortcutsSettingsViewModelTests
{
    private static readonly KeyChord CtrlAltP = new(VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu);
    private static readonly KeyChord CtrlM = new(VirtualKey.M, VirtualKeyModifiers.Control);

    private readonly FakeSettings _settings = new();
    private readonly ShortcutsSettingsViewModel _vm;

    public ShortcutsSettingsViewModelTests()
    {
        _vm = new ShortcutsSettingsViewModel(new ShortcutBindings(_settings));
    }

    private ShortcutRow Row(string actionId) => _vm.Rows.Single(r => r.ActionId == actionId);

    // ---- AC-435: every action, with its binding -------------------------------------------------------------------

    [Fact]
    public void Every_row_of_the_table_is_on_the_page_in_table_order_with_its_default_key()
    {
        _vm.Rows.Select(r => r.ActionId).Should().Equal(ShellShortcuts.All.Select(ShellShortcuts.ActionId));
        foreach (ShortcutRow row in _vm.Rows)
        {
            row.Display.Should().Be(ShellShortcuts.DefaultChord(row.Action).ToString());
            row.IsBound.Should().BeTrue();
            row.IsDefault.Should().BeTrue();
            row.DefaultNote.Should().BeEmpty();
            row.ChangeAutomationName.Should().Be("Change " + row.Name);
            row.ClearAutomationName.Should().Be("Clear " + row.Name);
            row.BindingAutomationId.Should().Be("Binding." + row.ActionId);
        }

        Row("playPause").Name.Should().Be("Play / Pause");
        Row("playPause").Display.Should().Be("Space");
        Row("seekForward30").Display.Should().Be("Shift+Right");
        Row("openSettings").Display.Should().Be("Ctrl+,");
        _vm.HasChanges.Should().BeFalse();
    }

    // ---- AC-436: the table shows the change at once and the store carries it ---------------------------------------

    [Fact]
    public void Pressing_a_free_chord_binds_the_row_and_the_row_shows_it_at_once()
    {
        ShortcutRow row = Row("playPause");

        _vm.Bind(row, CtrlAltP).Should().Be(BindOutcome.Bound);

        row.Display.Should().Be("Ctrl+Alt+P");
        row.IsDefault.Should().BeFalse();
        row.DefaultNote.Should().Be("Space by default");
        _settings.GetValue<string?>(SettingsKeys.Shortcut("playPause"), null).Should().Be("Ctrl+Alt+P");
        _vm.HasChanges.Should().BeTrue();
        _vm.Conflict.Should().BeNull();
    }

    [Fact]
    public void Pressing_the_chord_a_row_already_has_writes_nothing()
    {
        ShortcutRow row = Row("mute");
        var changed = new List<string>();
        _settings.Changed += (_, key) => changed.Add(key);

        _vm.Bind(row, new KeyChord(VirtualKey.M, VirtualKeyModifiers.None)).Should().Be(BindOutcome.Unchanged);

        changed.Should().BeEmpty();
    }

    // ---- AC-437: conflicts are named, and never silent ---------------------------------------------------------------

    [Fact]
    public void A_chord_another_action_holds_is_named_and_not_taken()
    {
        ShortcutRow asker = Row("playPause");
        ShortcutRow holder = Row("miniPlayer");

        _vm.Bind(asker, CtrlM).Should().Be(BindOutcome.Conflict);

        _vm.Conflict.Should().NotBeNull();
        _vm.Conflict!.Row.Should().BeSameAs(asker);
        _vm.Conflict.Holder.Should().BeSameAs(holder);
        _vm.Conflict.Title.Should().Be("Ctrl+M is already Mini player");
        _vm.Conflict.Consequence.Should().Contain("Mini player will be left with no shortcut");
        asker.Display.Should().Be("Space", "nothing is written until the person answers");
        holder.Display.Should().Be("Ctrl+M");
        _settings.Contains(SettingsKeys.Shortcut("playPause")).Should().BeFalse();
    }

    [Fact]
    public void Keeping_the_key_where_it_was_changes_nothing()
    {
        _vm.Bind(Row("playPause"), CtrlM);

        _vm.KeepConflictingKey();

        _vm.Conflict.Should().BeNull();
        Row("playPause").Display.Should().Be("Space");
        Row("miniPlayer").Display.Should().Be("Ctrl+M");
        _vm.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void Taking_the_key_leaves_the_other_row_visibly_unbound_and_the_two_never_share_it()
    {
        var holdersAtEachChange = new List<int>();
        var bindings = new ShortcutBindings(_settings);
        bindings.Changed += (_, _) => holdersAtEachChange.Add(bindings.Table.Count(s => s.Key == VirtualKey.M && s.Modifiers == VirtualKeyModifiers.Control));
        _vm.Bind(Row("playPause"), CtrlM);

        _vm.TakeConflictingKey();

        _vm.Conflict.Should().BeNull();
        Row("playPause").Display.Should().Be("Ctrl+M");
        Row("miniPlayer").Display.Should().Be(ShortcutRow.NotBound);
        Row("miniPlayer").IsBound.Should().BeFalse();
        Row("miniPlayer").IsDefault.Should().BeFalse();
        _settings.GetValue<string?>(SettingsKeys.Shortcut("miniPlayer"), null).Should().BeEmpty();
        _settings.GetValue<string?>(SettingsKeys.Shortcut("playPause"), null).Should().Be("Ctrl+M");
        holdersAtEachChange.Should().Equal([0, 1], "the holder is released before the asker is bound, so no table ever had both on the key");
    }

    [Fact]
    public void Two_keys_can_be_swapped_by_taking_and_then_rebinding_the_freed_row()
    {
        _vm.Bind(Row("shuffle"), new KeyChord(VirtualKey.R, VirtualKeyModifiers.None)).Should().Be(BindOutcome.Conflict);
        _vm.TakeConflictingKey();
        _vm.Bind(Row("repeat"), new KeyChord(VirtualKey.S, VirtualKeyModifiers.None)).Should().Be(BindOutcome.Bound, "S is free once Shuffle has moved to R");

        Row("shuffle").Display.Should().Be("R");
        Row("repeat").Display.Should().Be("S");
        new ShortcutBindings(_settings).Table.Select(s => (s.Key, s.Modifiers)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_new_capture_forgets_an_unanswered_conflict()
    {
        _vm.Bind(Row("playPause"), CtrlM);

        _vm.Bind(Row("playPause"), CtrlAltP).Should().Be(BindOutcome.Bound);

        _vm.Conflict.Should().BeNull();
        Row("miniPlayer").Display.Should().Be("Ctrl+M");
    }

    // ---- clear and reset (AC-147, AC-438) ---------------------------------------------------------------------------

    [Fact]
    public void Clear_leaves_a_row_with_no_key_and_frees_it_for_another_action()
    {
        ShortcutRow mute = Row("mute");

        _vm.Unbind(mute);

        mute.Display.Should().Be(ShortcutRow.NotBound);
        mute.IsBound.Should().BeFalse();
        _vm.Bind(Row("repeat"), new KeyChord(VirtualKey.M, VirtualKeyModifiers.None)).Should().Be(BindOutcome.Bound);
    }

    [Fact]
    public void Reset_restores_every_default_and_removes_every_stored_key()
    {
        _vm.Bind(Row("playPause"), CtrlAltP);
        _vm.Unbind(Row("mute"));
        _vm.Bind(Row("shuffle"), new KeyChord(VirtualKey.R, VirtualKeyModifiers.None));
        _vm.TakeConflictingKey();

        _vm.RestoreDefaults();

        foreach (ShortcutRow row in _vm.Rows)
        {
            row.Display.Should().Be(ShellShortcuts.DefaultChord(row.Action).ToString());
            row.IsDefault.Should().BeTrue();
            _settings.GetValue<string?>(SettingsKeys.Shortcut(row.ActionId), null).Should().BeNull();
        }

        _vm.HasChanges.Should().BeFalse();
    }

    /// <summary>The page follows the store, so a change made anywhere — another page, the file — shows on it.</summary>
    [Fact]
    public void A_binding_written_to_the_store_by_someone_else_shows_on_the_page()
    {
        _settings.SetValue(SettingsKeys.Shortcut("queue"), "Ctrl+Alt+Q");

        Row("queue").Display.Should().Be("Ctrl+Alt+Q");
        _vm.HasChanges.Should().BeTrue();
    }
}
