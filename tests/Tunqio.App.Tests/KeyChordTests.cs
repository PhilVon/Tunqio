using Tunqio.App.Shell;
using Windows.System;

namespace Tunqio.App.Tests;

/// <summary>
/// E6-S4: the one string form a binding has in <c>settings.json</c>. Every default in the table survives the round
/// trip, a hand-typed spelling reads back, and text that names no key is refused rather than guessed.
/// </summary>
public class KeyChordTests
{
    [Theory]
    [InlineData(VirtualKey.Space, VirtualKeyModifiers.None, "Space")]
    [InlineData(VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, "Ctrl+Alt+P")]
    [InlineData(VirtualKey.D, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, "Ctrl+Shift+D")]
    [InlineData(VirtualKey.Right, VirtualKeyModifiers.Shift, "Shift+Right")]
    [InlineData(VirtualKey.Number1, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, "Ctrl+Alt+1")]
    [InlineData(VirtualKey.F11, VirtualKeyModifiers.None, "F11")]
    [InlineData(VirtualKey.Escape, VirtualKeyModifiers.None, "Esc")]
    [InlineData((VirtualKey)188, VirtualKeyModifiers.Control, "Ctrl+,")]
    [InlineData(VirtualKey.Add, VirtualKeyModifiers.Control, "Ctrl+Numpad+")]
    [InlineData(VirtualKey.NumberPad5, VirtualKeyModifiers.Windows, "Win+Numpad5")]
    public void A_chord_is_written_the_way_the_design_document_writes_it(VirtualKey key, VirtualKeyModifiers modifiers, string expected)
    {
        new KeyChord(key, modifiers).ToString().Should().Be(expected);
    }

    [Fact]
    public void Modifiers_are_always_written_in_the_same_order()
    {
        new KeyChord(VirtualKey.K, VirtualKeyModifiers.Windows | VirtualKeyModifiers.Menu | VirtualKeyModifiers.Shift | VirtualKeyModifiers.Control)
            .ToString().Should().Be("Ctrl+Shift+Alt+Win+K");
    }

    [Fact]
    public void Every_default_in_the_table_round_trips_through_its_string()
    {
        foreach (ShellShortcut shortcut in ShellShortcuts.All)
        {
            KeyChord chord = ShellShortcuts.DefaultChord(shortcut);
            KeyChord.TryParse(chord.ToString(), out KeyChord back).Should().BeTrue("'{0}' must read back", chord);
            back.Should().Be(chord);
        }
    }

    [Theory]
    [InlineData("ctrl+alt+p", VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu)]
    [InlineData("Control + Shift + d", VirtualKey.D, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift)]
    [InlineData(" Space ", VirtualKey.Space, VirtualKeyModifiers.None)]
    [InlineData("Escape", VirtualKey.Escape, VirtualKeyModifiers.None)]
    [InlineData("Ctrl+Comma", (VirtualKey)188, VirtualKeyModifiers.Control)]
    [InlineData("Ctrl+Numpad+", VirtualKey.Add, VirtualKeyModifiers.Control)]
    [InlineData("VK255", (VirtualKey)255, VirtualKeyModifiers.None)]
    [InlineData("Win+CapitalLock", VirtualKey.CapitalLock, VirtualKeyModifiers.Windows)]
    public void A_hand_typed_spelling_reads_back(string text, VirtualKey key, VirtualKeyModifiers modifiers)
    {
        KeyChord.TryParse(text, out KeyChord chord).Should().BeTrue();
        chord.Should().Be(new KeyChord(key, modifiers));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl")]
    [InlineData("Shift+Ctrl")]
    [InlineData("Hyper+P")]
    [InlineData("Nonsense")]
    [InlineData("VK999")]
    public void Text_that_names_no_key_is_refused(string? text)
    {
        KeyChord.TryParse(text, out _).Should().BeFalse("a value that cannot be read must leave the action on its default, not on a guess");
    }

    [Fact]
    public void A_modifier_pressed_on_its_own_is_not_a_chord()
    {
        foreach (VirtualKey key in new[] { VirtualKey.Control, VirtualKey.LeftControl, VirtualKey.Shift, VirtualKey.RightShift, VirtualKey.Menu, VirtualKey.LeftWindows })
        {
            KeyChord.FromKeyDown(key, VirtualKeyModifiers.Control).Should().BeNull("{0} is on the way to a chord, not one", key);
        }

        KeyChord.FromKeyDown(VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu)
            .Should().Be(new KeyChord(VirtualKey.P, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu));
    }

    [Fact]
    public void A_key_the_table_does_not_name_still_has_a_name()
    {
        KeyChord.KeyName((VirtualKey)255).Should().Be("VK255");
        KeyChord.KeyName(VirtualKey.Application).Should().Be("Application");
    }

    /// <summary>
    /// E7-S2: the media and volume keys are Windows's, delivered to the app through SMTC even while the window has focus.
    /// A shortcut on one would handle the press a second time, so none can be captured, read from the file, or be a default.
    /// </summary>
    [Theory]
    [InlineData(173)] // volume mute
    [InlineData(174)] // volume down
    [InlineData(175)] // volume up
    [InlineData(176)] // next track
    [InlineData(177)] // previous track
    [InlineData(178)] // stop
    [InlineData(179)] // play/pause
    [InlineData(181)] // select media
    public void A_media_key_is_never_a_shortcut(int code)
    {
        var key = (VirtualKey)code;
        KeyChord.IsSystemMediaKey(key).Should().BeTrue();
        KeyChord.FromKeyDown(key, VirtualKeyModifiers.None).Should().BeNull("a capture waits past a media key rather than binding it");
        KeyChord.FromKeyDown(key, VirtualKeyModifiers.Control).Should().BeNull();
        KeyChord.TryParse(KeyChord.KeyName(key), out _).Should().BeFalse("a hand-edited binding to a media key leaves the action on its default");
        KeyChord.TryParse("Ctrl+" + KeyChord.KeyName(key), out _).Should().BeFalse();
        ShellShortcuts.All.Should().NotContain(shortcut => KeyChord.IsSystemMediaKey(shortcut.Key));
    }

    [Fact]
    public void The_keys_either_side_of_the_media_keys_still_bind()
    {
        KeyChord.FromKeyDown((VirtualKey)172, VirtualKeyModifiers.None).Should().NotBeNull();
        KeyChord.FromKeyDown((VirtualKey)184, VirtualKeyModifiers.None).Should().NotBeNull();
        KeyChord.TryParse("VK186", out _).Should().BeTrue();
    }
}
