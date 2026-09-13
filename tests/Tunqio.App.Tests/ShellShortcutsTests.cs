using Tunqio.App.Shell;
using Windows.System;

namespace Tunqio.App.Tests;

/// <summary>
/// E2-S6: the shell's shortcut table. What each key does, and — the part that is not decoration — how each one has
/// to reach the shell, since a bare key registered as a <c>KeyboardAccelerator</c> never fires in the views where
/// the user actually is, and an arrow key taken at the root takes the library's keyboard navigation with it.
/// </summary>
public class ShellShortcutsTests
{
    private static ShellShortcut? Find(VirtualKey key, VirtualKeyModifiers modifiers = VirtualKeyModifiers.None) =>
        ShellShortcuts.Find(key, modifiers, typing: false);

    [Fact]
    public void Every_key_appears_once()
    {
        ShellShortcuts.All.Select(s => (s.Key, s.Modifiers)).Should().OnlyHaveUniqueItems(
            "two shortcuts on one chord means one of them silently never happens");
    }

    /// <summary>
    /// The table against docs/ui-screens-and-flows.md, "Keyboard shortcuts" — the rows E2 and E5-S1 own. The rest of
    /// that table is deliberately absent: the media keys are SMTC (E7), and the mini player, playlists, tag editing
    /// and presets have nothing yet to do.
    /// </summary>
    [Fact]
    public void The_table_is_the_documented_one()
    {
        Find(VirtualKey.Space)!.Value.Command.Should().Be(ShellCommand.PlayPause);
        Find(VirtualKey.Right, VirtualKeyModifiers.Control)!.Value.Command.Should().Be(ShellCommand.Next);
        Find(VirtualKey.Left, VirtualKeyModifiers.Control)!.Value.Command.Should().Be(ShellCommand.Previous);
        Find(VirtualKey.M)!.Value.Command.Should().Be(ShellCommand.Mute);
        Find(VirtualKey.S)!.Value.Command.Should().Be(ShellCommand.Shuffle);
        Find(VirtualKey.R)!.Value.Command.Should().Be(ShellCommand.Repeat);
        Find(VirtualKey.Q)!.Value.Command.Should().Be(ShellCommand.Queue);

        Find(VirtualKey.Right)!.Value.Amount.Should().Be(5);
        Find(VirtualKey.Left)!.Value.Amount.Should().Be(-5);
        Find(VirtualKey.Right, VirtualKeyModifiers.Shift)!.Value.Amount.Should().Be(30);
        Find(VirtualKey.Left, VirtualKeyModifiers.Shift)!.Value.Amount.Should().Be(-30);
        Find(VirtualKey.Up)!.Value.Amount.Should().Be(0.05);
        Find(VirtualKey.Down)!.Value.Amount.Should().Be(-0.05);

        Find(VirtualKey.Number1, VirtualKeyModifiers.Control)!.Value.Command.Should().Be(ShellCommand.Discovery);
        Find(VirtualKey.Number2, VirtualKeyModifiers.Control)!.Value.Command.Should().Be(ShellCommand.Focus);
        Find(VirtualKey.Number3, VirtualKeyModifiers.Control)!.Value.Command.Should().Be(ShellCommand.Curation);
        Find(VirtualKey.F11)!.Value.Command.Should().Be(ShellCommand.ToggleFocus);
        Find(VirtualKey.Escape)!.Value.Command.Should().Be(ShellCommand.LeaveFocus);
        Find(VirtualKey.M, VirtualKeyModifiers.Control)!.Value.Command.Should().Be(ShellCommand.MiniPlayer);
        Find(VirtualKey.M)!.Value.Command.Should().Be(ShellCommand.Mute, "Ctrl+M is the mini player and M on its own is still Mute");
        Find(VirtualKey.Z, VirtualKeyModifiers.Control)!.Value.Command.Should().Be(ShellCommand.Undo);
        Find(VirtualKey.Y, VirtualKeyModifiers.Control)!.Value.Command.Should().Be(ShellCommand.Redo);
    }

    /// <summary>E5-S4: a text box has an undo of its own, so Curation's only fires on a Ctrl+Z nothing else took.</summary>
    [Fact]
    public void Undo_and_redo_leave_a_text_box_its_own()
    {
        Find(VirtualKey.Z, VirtualKeyModifiers.Control)!.Value.Delivery.Should().Be(ShortcutDelivery.Accelerator);
        Find(VirtualKey.Y, VirtualKeyModifiers.Control)!.Value.Delivery.Should().Be(ShortcutDelivery.Accelerator);
    }

    // ---- E5-S1: the mode keys ---------------------------------------------------------------------------------------

    [Fact]
    public void The_mode_keys_are_taken_before_a_focused_control_sees_them()
    {
        foreach (VirtualKey key in new[] { VirtualKey.Number1, VirtualKey.Number2, VirtualKey.Number3 })
        {
            Find(key, VirtualKeyModifiers.Control)!.Value.Delivery.Should().Be(ShortcutDelivery.PreEmpt);
        }

        Find(VirtualKey.F11)!.Value.Delivery.Should().Be(ShortcutDelivery.PreEmpt);
    }

    /// <summary>
    /// Esc belongs to a search box, a flyout and a dialog before it belongs to the shell, so it must only ever fire
    /// on a key none of them wanted.
    /// </summary>
    [Fact]
    public void Esc_is_only_the_shells_when_nothing_else_wanted_it()
    {
        Find(VirtualKey.Escape)!.Value.Delivery.Should().Be(ShortcutDelivery.Accelerator);
    }

    [Fact]
    public void A_media_key_is_not_the_shells_to_take()
    {
        Find(VirtualKey.GamepadMenu).Should().BeNull();
        ShellShortcuts.All.Should().NotContain(s => s.Command == ShellCommand.PlayPause && s.Key != VirtualKey.Space);
    }

    // ---- AC-77: the text-box opt-out --------------------------------------------------------------------------------

    [Fact]
    public void Nothing_fires_while_the_focus_is_in_something_being_typed_into()
    {
        foreach (ShellShortcut shortcut in ShellShortcuts.All.Where(s => !s.WhileTyping))
        {
            ShellShortcuts.Find(shortcut.Key, shortcut.Modifiers, typing: true)
                .Should().BeNull("'{0}' is part of typing before it is part of the transport", shortcut.Key);
        }
    }

    /// <summary>
    /// The exception, and it has to stay one. A chord a text box has no opinion about is not the typist's to keep,
    /// and the moment a diagnostic overlay is most wanted is the moment something has gone wrong where the user was.
    /// </summary>
    [Fact]
    public void Only_the_diagnostics_overlay_is_the_typists_to_lose()
    {
        ShellShortcuts.All.Where(s => s.WhileTyping)
            .Should().ContainSingle().Which.Command.Should().Be(ShellCommand.Diagnostics);
    }

    [Fact]
    public void Modifiers_are_matched_exactly()
    {
        Find(VirtualKey.Space, VirtualKeyModifiers.Control).Should().BeNull("Ctrl+Space is not Space");
        Find(VirtualKey.S, VirtualKeyModifiers.Shift).Should().BeNull("Shift+S is a capital S");
        Find(VirtualKey.Right, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift).Should().BeNull();
    }

    // ---- AC-78: reaching every view ---------------------------------------------------------------------------------

    /// <summary>
    /// The bare keys have to be taken before the focused control sees them, or the shortcut works everywhere
    /// except the library: a tile is a <c>Button</c> and takes Space as a press, and a list takes a letter as
    /// type-ahead. An accelerator only ever fires on a key that reached the end unhandled, which those never do.
    /// </summary>
    [Fact]
    public void A_shortcut_on_a_bare_key_is_taken_before_the_control_that_has_focus()
    {
        VirtualKey[] bare = [VirtualKey.Space, VirtualKey.M, VirtualKey.S, VirtualKey.R, VirtualKey.Q];

        foreach (VirtualKey key in bare)
        {
            Find(key)!.Value.Delivery.Should().Be(ShortcutDelivery.PreEmpt, "{0} would otherwise never reach the shell", key);
        }
    }

    /// <summary>
    /// And the arrows must not be, which is the same argument run the other way: they are how a grid and a list
    /// are navigated, and a shell that took them at the root would trade that for a five-second seek.
    /// </summary>
    [Fact]
    public void The_arrow_keys_are_left_to_whatever_is_navigating_with_them()
    {
        ShellShortcuts.All
            .Where(s => s.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
            .Where(s => !s.Modifiers.HasFlag(VirtualKeyModifiers.Control))
            .Should().OnlyContain(s => s.Delivery == ShortcutDelivery.Accelerator)
            .And.HaveCount(6);
    }

    /// <summary>
    /// Ctrl+Left and Ctrl+Right are the exception, and the deliberate loss: a grid moves focus on them without
    /// changing the selection, and next and previous track are what someone reaches for while browsing.
    /// </summary>
    [Fact]
    public void Previous_and_next_track_win_over_moving_focus_in_a_grid()
    {
        Find(VirtualKey.Left, VirtualKeyModifiers.Control)!.Value.Delivery.Should().Be(ShortcutDelivery.PreEmpt);
        Find(VirtualKey.Right, VirtualKeyModifiers.Control)!.Value.Delivery.Should().Be(ShortcutDelivery.PreEmpt);
    }

    [Fact]
    public void The_two_halves_together_are_the_whole_table()
    {
        ShellShortcuts.PreEmpting.Concat(ShellShortcuts.Accelerated)
            .Should().BeEquivalentTo(ShellShortcuts.All);
    }
}
