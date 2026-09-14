using Windows.System;

namespace Tunqio.App.Shell;

/// <summary>What the keyboard is doing right now, for the handlers that get a key without its modifiers.</summary>
internal static class ShellKeyboard
{
    /// <summary>
    /// Which modifiers are down. <c>PreviewKeyDown</c> reports the key but not the modifiers, and the shortcut table
    /// matches them exactly, because Ctrl+Space is not Space and Shift+S is a capital S.
    /// </summary>
    public static VirtualKeyModifiers CurrentModifiers()
    {
        var modifiers = VirtualKeyModifiers.None;
        if (Controls.Modifiers.Control)
        {
            modifiers |= VirtualKeyModifiers.Control;
        }

        if (Controls.Modifiers.Shift)
        {
            modifiers |= VirtualKeyModifiers.Shift;
        }

        if (Controls.Modifiers.IsDown(VirtualKey.Menu))
        {
            modifiers |= VirtualKeyModifiers.Menu;
        }

        if (Controls.Modifiers.IsDown(VirtualKey.LeftWindows) || Controls.Modifiers.IsDown(VirtualKey.RightWindows))
        {
            modifiers |= VirtualKeyModifiers.Windows;
        }

        return modifiers;
    }
}
