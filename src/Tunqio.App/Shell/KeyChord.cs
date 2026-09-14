using System.Globalization;
using Windows.System;

namespace Tunqio.App.Shell;

/// <summary>
/// A key with the modifiers that must be down with it, and the one string form it has in <c>settings.json</c>
/// (E6-S4): <c>Ctrl+Shift+D</c>, <c>Shift+Right</c>, <c>Space</c>, <c>Ctrl+,</c>. Modifiers are always written
/// in the order Ctrl, Shift, Alt, Win, and the key is the name the design document uses for it, so a binding
/// someone typed into the file by hand reads back the same as one the page wrote.
/// </summary>
public readonly record struct KeyChord(VirtualKey Key, VirtualKeyModifiers Modifiers)
{
    private static readonly (VirtualKeyModifiers Flag, string Name)[] ModifierNames =
    [
        (VirtualKeyModifiers.Control, "Ctrl"),
        (VirtualKeyModifiers.Shift, "Shift"),
        (VirtualKeyModifiers.Menu, "Alt"),
        (VirtualKeyModifiers.Windows, "Win"),
    ];

    /// <summary>Other spellings a hand-edited file might use for a modifier.</summary>
    private static readonly Dictionary<string, VirtualKeyModifiers> ModifierAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl"] = VirtualKeyModifiers.Control,
        ["Control"] = VirtualKeyModifiers.Control,
        ["Shift"] = VirtualKeyModifiers.Shift,
        ["Alt"] = VirtualKeyModifiers.Menu,
        ["Menu"] = VirtualKeyModifiers.Menu,
        ["Win"] = VirtualKeyModifiers.Windows,
        ["Windows"] = VirtualKeyModifiers.Windows,
    };

    /// <summary>
    /// The keys with a name of their own. Everything else is the <see cref="VirtualKey"/> enum name when it has one
    /// and <c>VK</c> plus the code when it does not, so no key is unrepresentable — a binding is only ever refused
    /// for being a conflict, never for being unusual.
    /// </summary>
    private static readonly Dictionary<VirtualKey, string> KeyNames = new()
    {
        [VirtualKey.Space] = "Space",
        [VirtualKey.Escape] = "Esc",
        [VirtualKey.Enter] = "Enter",
        [VirtualKey.Tab] = "Tab",
        [VirtualKey.Back] = "Backspace",
        [VirtualKey.Delete] = "Delete",
        [VirtualKey.Insert] = "Insert",
        [VirtualKey.Home] = "Home",
        [VirtualKey.End] = "End",
        [VirtualKey.PageUp] = "PageUp",
        [VirtualKey.PageDown] = "PageDown",
        [VirtualKey.Left] = "Left",
        [VirtualKey.Right] = "Right",
        [VirtualKey.Up] = "Up",
        [VirtualKey.Down] = "Down",
        [VirtualKey.Add] = "Numpad+",
        [VirtualKey.Subtract] = "Numpad-",
        [VirtualKey.Multiply] = "Numpad*",
        [VirtualKey.Divide] = "Numpad/",
        [VirtualKey.Decimal] = "Numpad.",
        // The OEM keys have no VirtualKey name; these are the US layout's glyphs, which is what the design document
        // writes Ctrl+, with.
        [(VirtualKey)186] = ";",
        [(VirtualKey)187] = "=",
        [(VirtualKey)188] = ",",
        [(VirtualKey)189] = "-",
        [(VirtualKey)190] = ".",
        [(VirtualKey)191] = "/",
        [(VirtualKey)192] = "`",
        [(VirtualKey)219] = "[",
        [(VirtualKey)220] = "\\",
        [(VirtualKey)221] = "]",
        [(VirtualKey)222] = "'",
    };

    private static readonly Dictionary<string, VirtualKey> KeysByName = BuildKeysByName();

    /// <summary>The keys that are modifiers themselves, which a capture waits past rather than binds.</summary>
    private static readonly HashSet<VirtualKey> ModifierKeys =
    [
        VirtualKey.Control, VirtualKey.LeftControl, VirtualKey.RightControl,
        VirtualKey.Shift, VirtualKey.LeftShift, VirtualKey.RightShift,
        VirtualKey.Menu, VirtualKey.LeftMenu, VirtualKey.RightMenu,
        VirtualKey.LeftWindows, VirtualKey.RightWindows,
    ];

    /// <summary>The name of <paramref name="key"/> on its own: <c>A</c>, <c>5</c>, <c>F11</c>, <c>Space</c>, <c>,</c>.</summary>
    public static string KeyName(VirtualKey key)
    {
        if (KeyNames.TryGetValue(key, out string? named))
        {
            return named;
        }

        if (key is >= VirtualKey.A and <= VirtualKey.Z)
        {
            return ((char)('A' + (key - VirtualKey.A))).ToString();
        }

        if (key is >= VirtualKey.Number0 and <= VirtualKey.Number9)
        {
            return ((char)('0' + (key - VirtualKey.Number0))).ToString();
        }

        if (key is >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9)
        {
            return "Numpad" + (char)('0' + (key - VirtualKey.NumberPad0));
        }

        if (key is >= VirtualKey.F1 and <= VirtualKey.F24)
        {
            return "F" + (1 + (key - VirtualKey.F1)).ToString(CultureInfo.InvariantCulture);
        }

        return Enum.IsDefined(key)
            ? key.ToString()
            : "VK" + ((int)key).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The chord's string form, which is also what the settings page shows.</summary>
    public override string ToString()
    {
        var text = new System.Text.StringBuilder();
        foreach ((VirtualKeyModifiers flag, string name) in ModifierNames)
        {
            if (Modifiers.HasFlag(flag))
            {
                text.Append(name).Append('+');
            }
        }

        return text.Append(KeyName(Key)).ToString();
    }

    /// <summary>
    /// Reads a chord back from its string form; false for text that names no key, so a hand-edited value that
    /// cannot be read leaves the action on its default rather than on nothing.
    /// </summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = VirtualKeyModifiers.None;
        string rest = text.Trim();
        // Peel modifiers off the front one at a time rather than splitting on '+': the key itself may be '+' (Numpad+),
        // and "Ctrl+Numpad+" split on '+' has no key left at the end.
        while (true)
        {
            int plus = rest.IndexOf('+', StringComparison.Ordinal);
            if (plus <= 0 || plus == rest.Length - 1)
            {
                break;
            }

            if (!ModifierAliases.TryGetValue(rest[..plus].Trim(), out VirtualKeyModifiers modifier))
            {
                break;
            }

            modifiers |= modifier;
            rest = rest[(plus + 1)..].Trim();
        }

        if (!TryParseKey(rest, out VirtualKey key) || IsSystemMediaKey(key))
        {
            return false;
        }

        chord = new KeyChord(key, modifiers);
        return true;
    }

    /// <summary>
    /// The chord a key-down means while the page is recording one, or null for a modifier pressed on its own —
    /// Ctrl going down on the way to Ctrl+P is not a binding to Ctrl.
    /// </summary>
    public static KeyChord? FromKeyDown(VirtualKey key, VirtualKeyModifiers modifiers) =>
        ModifierKeys.Contains(key) || key == VirtualKey.None || IsSystemMediaKey(key) ? null : new KeyChord(key, modifiers);

    /// <summary>
    /// The volume, media and launch keys (VK 173..183). Windows owns them: the media keys reach the app through the
    /// system media transport controls whether or not the window has focus (E7-S2, ADR-006), and the volume keys move
    /// the system volume. A shortcut on one would be a second handling of the same press — Play/Pause toggled by SMTC
    /// and toggled back by the shell — so none can be captured or read back from the file.
    /// </summary>
    public static bool IsSystemMediaKey(VirtualKey key) => (int)key is >= 173 and <= 183;

    private static bool TryParseKey(string name, out VirtualKey key)
    {
        if (KeysByName.TryGetValue(name, out key))
        {
            return true;
        }

        if (name.StartsWith("VK", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(name.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out int code)
            && code is > 0 and < 256)
        {
            key = (VirtualKey)code;
            return true;
        }

        // Anything the enum names and the table above does not: CapitalLock, NumberKeyLock, Application ...
        return Enum.TryParse(name, ignoreCase: true, out key) && !ModifierKeys.Contains(key) && key != VirtualKey.None;
    }

    private static Dictionary<string, VirtualKey> BuildKeysByName()
    {
        var byName = new Dictionary<string, VirtualKey>(StringComparer.OrdinalIgnoreCase);
        for (VirtualKey key = VirtualKey.A; key <= VirtualKey.Z; key++)
        {
            byName[KeyName(key)] = key;
        }

        for (VirtualKey key = VirtualKey.Number0; key <= VirtualKey.Number9; key++)
        {
            byName[KeyName(key)] = key;
        }

        for (VirtualKey key = VirtualKey.NumberPad0; key <= VirtualKey.NumberPad9; key++)
        {
            byName[KeyName(key)] = key;
        }

        for (VirtualKey key = VirtualKey.F1; key <= VirtualKey.F24; key++)
        {
            byName[KeyName(key)] = key;
        }

        foreach ((VirtualKey key, string name) in KeyNames)
        {
            byName[name] = key;
        }

        // Spellings the enum uses, or a person might, for keys the table names differently.
        byName["Escape"] = VirtualKey.Escape;
        byName["Return"] = VirtualKey.Enter;
        byName["Back"] = VirtualKey.Back;
        byName["Del"] = VirtualKey.Delete;
        byName["Comma"] = (VirtualKey)188;
        byName["Period"] = (VirtualKey)190;
        byName["Plus"] = (VirtualKey)187;
        byName["Minus"] = (VirtualKey)189;
        return byName;
    }
}
