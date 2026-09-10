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
/// <param name="Amount">Seconds for <see cref="ShellCommand.Seek"/>, a fraction for <see cref="ShellCommand.Volume"/>, zero otherwise.</param>
/// <param name="Delivery">How it reaches the shell.</param>
public readonly record struct ShellShortcut(
    VirtualKey Key,
    VirtualKeyModifiers Modifiers,
    ShellCommand Command,
    double Amount,
    ShortcutDelivery Delivery);

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
/// the shortcuts whose features do not exist yet — the mode switches, the mini player, playlists, tag editing and
/// visualization presets — because a key that is registered and does nothing is worse than one that is not
/// registered at all.
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

        new(VirtualKey.Right, VirtualKeyModifiers.None, ShellCommand.Seek, 5, ShortcutDelivery.Accelerator),
        new(VirtualKey.Left, VirtualKeyModifiers.None, ShellCommand.Seek, -5, ShortcutDelivery.Accelerator),
        new(VirtualKey.Right, VirtualKeyModifiers.Shift, ShellCommand.Seek, 30, ShortcutDelivery.Accelerator),
        new(VirtualKey.Left, VirtualKeyModifiers.Shift, ShellCommand.Seek, -30, ShortcutDelivery.Accelerator),
        new(VirtualKey.Up, VirtualKeyModifiers.None, ShellCommand.Volume, VolumeStep, ShortcutDelivery.Accelerator),
        new(VirtualKey.Down, VirtualKeyModifiers.None, ShellCommand.Volume, -VolumeStep, ShortcutDelivery.Accelerator),
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
    /// for every shortcut while <paramref name="typing"/>, since focus is then in something the keystroke belongs
    /// to more than the transport.
    /// </summary>
    public static ShellShortcut? Find(VirtualKey key, VirtualKeyModifiers modifiers, bool typing)
    {
        if (typing)
        {
            return null;
        }

        foreach (ShellShortcut shortcut in Table)
        {
            if (shortcut.Key == key && shortcut.Modifiers == modifiers)
            {
                return shortcut;
            }
        }

        return null;
    }
}
