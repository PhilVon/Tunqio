using Windows.System;

namespace Tunqio.App.Shell;

/// <summary>What a shell shortcut asks the transport for (E2-S6).</summary>
public enum ShellCommand
{
    PlayPause,
    Next,
    Previous,

    /// <summary>Nudge the position by the shortcut's amount, in seconds.</summary>
    Seek,

    /// <summary>Move the output level by the shortcut's amount, as a fraction of full.</summary>
    Volume,

    Mute,
    Shuffle,
    Repeat,

    /// <summary>Open the queue panel.</summary>
    Queue,

    /// <summary>Show or hide the diagnostics overlay (E2-S8).</summary>
    Diagnostics,

    /// <summary>Switch to Discovery (E5-S1).</summary>
    Discovery,

    /// <summary>Switch to Focus.</summary>
    Focus,

    /// <summary>Switch to Curation.</summary>
    Curation,

    /// <summary>Into Focus, or back to the mode it was entered from.</summary>
    ToggleFocus,

    /// <summary>Leave Focus for the mode it was entered from (flow 7); nothing outside Focus.</summary>
    LeaveFocus,

    /// <summary>Open the mini player (E5-S6).</summary>
    MiniPlayer,

    /// <summary>Open the settings overlay (E6-S3).</summary>
    OpenSettings,

    /// <summary>Undo the Curation editor's last edit (E5-S4); nothing outside Curation.</summary>
    Undo,

    /// <summary>Redo the Curation editor's last undone edit.</summary>
    Redo,

    /// <summary>Rate the playing track the shortcut's amount of stars, 1..5; 0 clears the rating (E6-S7).</summary>
    Rate,
}

/// <summary>
/// How a shortcut reaches the shell, which is the whole of why this is a table and not a list of
/// <c>KeyboardAccelerator</c>s in XAML.
/// </summary>
public enum ShortcutDelivery
{
    /// <summary>
    /// Taken at the shell root on the way down, before the focused control sees it. What a bare key needs when
    /// some ordinary control would otherwise eat it: a focused <c>Button</c> takes Space, and a list takes a
    /// letter as type-ahead.
    /// </summary>
    PreEmpt,

    /// <summary>
    /// A <c>KeyboardAccelerator</c>, which fires only if nothing else handled the key. What the arrow keys need:
    /// they are how a grid or a list is navigated, and taking them at the root would leave the library
    /// unreachable by keyboard to buy a five-second seek.
    /// </summary>
    Accelerator,
}

/// <summary>One row of the shell's shortcut table.</summary>
/// <param name="Key">The key, with <paramref name="Modifiers"/> matched exactly — Ctrl+Space is not Space.</param>
/// <param name="Modifiers">The modifiers that must be down, and no others.</param>
/// <param name="Command">What it asks for.</param>
/// <param name="Amount">Seconds for <see cref="ShellCommand.Seek"/>, a fraction for <see cref="ShellCommand.Volume"/>, stars for <see cref="ShellCommand.Rate"/>, zero otherwise.</param>
/// <param name="Delivery">How it reaches the shell.</param>
/// <param name="WhileTyping">
/// True for the few chords that mean nothing to a text box and so are not the typist's to keep. The default is
/// false, which is the rule: Ctrl+Left is how a caret moves by word, and Space is a space.
/// </param>
public readonly record struct ShellShortcut(
    VirtualKey Key,
    VirtualKeyModifiers Modifiers,
    ShellCommand Command,
    double Amount,
    ShortcutDelivery Delivery,
    bool WhileTyping = false);

/// <summary>
/// The shell's keyboard shortcuts (E2-S6, docs/ui-screens-and-flows.md, "Keyboard shortcuts"), as a table rather
/// than as accelerators declared in XAML — for the same reason <see cref="ShellLayout"/> is a table: the criteria
/// are about the rules, and a rule that exists only as markup can be checked only by pressing keys at a running
/// window.
/// </summary>
/// <remarks>
/// <para>
/// Two things here are not obvious and are the substance of the story. The first is that <em>a shortcut on a bare
/// key cannot be a <c>KeyboardAccelerator</c></em>. An accelerator fires only when the key reached the end of the
/// routed event unhandled, and the controls the user is most likely to be focused on handle exactly these keys
/// first: a library tile is a <c>Button</c> and takes Space as a press, and a list takes S, R, M and Q as
/// type-ahead. AC-77 says Space toggles playback unless a text box has focus, so Space has to be taken on the way
/// down, before the button sees it — and so must the letters, or the shortcut works everywhere except the view the
/// user spends their time in.
/// </para>
/// <para>
/// The second is the opposite, and is why this is a per-shortcut property rather than a policy: <em>the arrow keys
/// must not be pre-empted.</em> They are how a grid and a list are navigated, and a shell that took them at the
/// root would trade the library's keyboard navigation for a five-second seek. As accelerators they do exactly the
/// right thing without anything having to know which control is focused — the grid handles them and they never
/// fire, the Now Playing panel does not and they seek.
/// </para>
/// <para>
/// Ctrl+Left and Ctrl+Right are the one deliberate loss. A grid moves focus on them without changing the
/// selection, and they are pre-empted anyway: previous and next track are the transport actions someone reaches
/// for while browsing the library, and moving focus without selecting is not.
/// </para>
/// <para>
/// Nothing in the table fires while focus is in something that takes typed text. That is broader than the
/// single-letter opt-out the design document describes, and deliberately: Ctrl+Left is how a caret moves by word
/// and Space is a space, so a search box that skipped a track on Ctrl+Left would be as broken as one that shuffled
/// on S. Escape leaves the search box, which is how someone typing reaches the transport again.
/// </para>
/// <para>
/// Media keys are not here: they are <c>SystemMediaTransportControls</c> (ADR-006) and belong to E7. Neither are
/// the shortcuts whose features do not exist yet — playlists, tag editing and visualization presets — because a key
/// that is registered and does nothing is worse than one that is not registered at all. Ctrl+M (E5-S6) is taken on
/// the way down like the mode keys: it is a chord no control wants, and M on its own is still Mute.
/// </para>
/// <para>
/// The mode keys (E5-S1) split the same way. Ctrl+1/2/3 and F11 are taken on the way down, since nothing a control
/// does with them is worth more than the mode. Esc is the opposite: a search box clears on it, a flyout closes on it
/// and a dialog cancels on it, so it is an accelerator, and it is handled only while there is a Focus to leave.
/// </para>
/// </remarks>
public static class ShellShortcuts
{
    /// <summary>How far the bare arrows seek (docs/ui-screens-and-flows.md).</summary>
    public static readonly TimeSpan SmallSeek = TimeSpan.FromSeconds(5);

    /// <summary>How far Shift and the arrows seek.</summary>
    public static readonly TimeSpan LargeSeek = TimeSpan.FromSeconds(30);

    /// <summary>How much one press of Up or Down moves the output level.</summary>
    public const double VolumeStep = 0.05;

    private static readonly ShellShortcut[] Table =
    [
        new(VirtualKey.Space, VirtualKeyModifiers.None, ShellCommand.PlayPause, 0, ShortcutDelivery.PreEmpt),
        new(VirtualKey.Right, VirtualKeyModifiers.Control, ShellCommand.Next, 0, ShortcutDelivery.PreEmpt),
        new(VirtualKey.Left, VirtualKeyModifiers.Control, ShellCommand.Previous, 0, ShortcutDelivery.PreEmpt),
        new(VirtualKey.M, VirtualKeyModifiers.None, ShellCommand.Mute, 0, ShortcutDelivery.PreEmpt),
        new(VirtualKey.S, VirtualKeyModifiers.None, ShellCommand.Shuffle, 0, ShortcutDelivery.PreEmpt),
        new(VirtualKey.R, VirtualKeyModifiers.None, ShellCommand.Repeat, 0, ShortcutDelivery.PreEmpt),
        new(VirtualKey.Q, VirtualKeyModifiers.None, ShellCommand.Queue, 0, ShortcutDelivery.PreEmpt),
        new(VirtualKey.Number1, VirtualKeyModifiers.Control, ShellCommand.Discovery, 0, ShortcutDelivery.PreEmpt),
        new(VirtualKey.Number2, VirtualKeyModifiers.Control, ShellCommand.Focus, 0, ShortcutDelivery.PreEmpt),
        new(VirtualKey.Number3, VirtualKeyModifiers.Control, ShellCommand.Curation, 0, ShortcutDelivery.PreEmpt),
        new(VirtualKey.F11, VirtualKeyModifiers.None, ShellCommand.ToggleFocus, 0, ShortcutDelivery.PreEmpt),
        new(VirtualKey.M, VirtualKeyModifiers.Control, ShellCommand.MiniPlayer, 0, ShortcutDelivery.PreEmpt),
        // Ctrl+, (VK_OEM_COMMA, no VirtualKey name). At the shell rather than on the sidebar, where E3-S12 put it: the
        // sidebar is collapsed in Focus, and Settings is an overlay over the whole shell now (E6-S3).
        new((VirtualKey)188, VirtualKeyModifiers.Control, ShellCommand.OpenSettings, 0, ShortcutDelivery.PreEmpt),
        new(VirtualKey.Escape, VirtualKeyModifiers.None, ShellCommand.LeaveFocus, 0, ShortcutDelivery.Accelerator),

        // Curation's undo and redo (E5-S4). Accelerators, like Esc: a text box has an undo of its own and keeps it.
        new(VirtualKey.Z, VirtualKeyModifiers.Control, ShellCommand.Undo, 0, ShortcutDelivery.Accelerator),
        new(VirtualKey.Y, VirtualKeyModifiers.Control, ShellCommand.Redo, 0, ShortcutDelivery.Accelerator),

        // Rate the playing track (E6-S7): Ctrl+Alt+1..5 for the stars, Ctrl+Alt+0 to clear. One command with the
        // stars as the amount, the way the seeks carry their seconds. Taken on the way down like the mode keys: no
        // control wants the chord, and on a layout where Ctrl+Alt is AltGr the typing check already keeps it out of
        // a text box.
        new(VirtualKey.Number1, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, ShellCommand.Rate, 1, ShortcutDelivery.PreEmpt),
        new(VirtualKey.Number2, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, ShellCommand.Rate, 2, ShortcutDelivery.PreEmpt),
        new(VirtualKey.Number3, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, ShellCommand.Rate, 3, ShortcutDelivery.PreEmpt),
        new(VirtualKey.Number4, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, ShellCommand.Rate, 4, ShortcutDelivery.PreEmpt),
        new(VirtualKey.Number5, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, ShellCommand.Rate, 5, ShortcutDelivery.PreEmpt),
        new(VirtualKey.Number0, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu, ShellCommand.Rate, 0, ShortcutDelivery.PreEmpt),

        new(VirtualKey.Right, VirtualKeyModifiers.None, ShellCommand.Seek, 5, ShortcutDelivery.Accelerator),
        new(VirtualKey.Left, VirtualKeyModifiers.None, ShellCommand.Seek, -5, ShortcutDelivery.Accelerator),
        new(VirtualKey.Right, VirtualKeyModifiers.Shift, ShellCommand.Seek, 30, ShortcutDelivery.Accelerator),
        new(VirtualKey.Left, VirtualKeyModifiers.Shift, ShellCommand.Seek, -30, ShortcutDelivery.Accelerator),
        new(VirtualKey.Up, VirtualKeyModifiers.None, ShellCommand.Volume, VolumeStep, ShortcutDelivery.Accelerator),
        new(VirtualKey.Down, VirtualKeyModifiers.None, ShellCommand.Volume, -VolumeStep, ShortcutDelivery.Accelerator),

        // The one chord a text box has no opinion about, so it is the one shortcut that still works while typing.
        new(
            VirtualKey.D,
            VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift,
            ShellCommand.Diagnostics,
            0,
            ShortcutDelivery.Accelerator,
            WhileTyping: true),
    ];

    /// <summary>Every shortcut the shell registers, in the order the design document lists them.</summary>
    public static IReadOnlyList<ShellShortcut> All => Table;

    /// <summary>The shortcuts taken at the root on the way down.</summary>
    public static IEnumerable<ShellShortcut> PreEmpting =>
        Table.Where(shortcut => shortcut.Delivery == ShortcutDelivery.PreEmpt);

    /// <summary>The shortcuts registered as accelerators, which fire only on a key nothing else wanted.</summary>
    public static IEnumerable<ShellShortcut> Accelerated =>
        Table.Where(shortcut => shortcut.Delivery == ShortcutDelivery.Accelerator);

    /// <summary>
    /// The shortcut for <paramref name="key"/> with exactly <paramref name="modifiers"/> down, or null — including
    /// for all but the <see cref="ShellShortcut.WhileTyping"/> ones while <paramref name="typing"/>, since focus is
    /// then in something the keystroke belongs to more than the transport.
    /// </summary>
    public static ShellShortcut? Find(VirtualKey key, VirtualKeyModifiers modifiers, bool typing)
    {
        foreach (ShellShortcut shortcut in Table)
        {
            if (shortcut.Key == key && shortcut.Modifiers == modifiers)
            {
                return typing && !shortcut.WhileTyping ? null : shortcut;
            }
        }

        return null;
    }
}
