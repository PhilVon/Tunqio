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

    /// <summary>
    /// Switch the visualizer to the next preset in the catalogue, wrapping at the end (T-185); nothing while no
    /// renderer is attached.
    /// </summary>
    NextPreset,
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
/// the shortcuts whose features do not exist yet — playlists and tag editing — because a key
/// that is registered and does nothing is worse than one that is not registered at all. Ctrl+M (E5-S6) is taken on
/// the way down like the mode keys: it is a chord no control wants, and M on its own is still Mute.
/// </para>
/// <para>
/// The mode keys (E5-S1) split the same way. Ctrl+1/2/3 and F11 are taken on the way down, since nothing a control
/// does with them is worth more than the mode. Esc is the opposite: a search box clears on it, a flyout closes on it
/// and a dialog cancels on it, so it is an accelerator, and it is handled only while there is a Focus to leave.
/// </para>
/// <para>
/// The table is the <em>defaults</em> (E6-S4). What the shell actually listens to is <see cref="Resolve"/>'s copy of
/// it with <c>shortcuts.&lt;action&gt;</c> from <c>settings.json</c> applied — a stored chord replaces the key, an
/// empty string unbinds the action, an absent key means the default — and <see cref="ShortcutBindings"/> keeps that
/// copy current as the store changes. The delivery, the typing rule and the amount stay with the action whatever key
/// it is on: Space rebound to J is still taken on the way down, because the reason it is taken is that a focused
/// button would eat it, and a button eats J as readily as Space. The action's name in the file is derived from its
/// row (<see cref="ActionId"/>), so a command added to the table gets its setting, its page row and its name without
/// a second list to keep in step.
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

        // Next preset (T-185). Ctrl+V is paste, so it is an accelerator like undo and redo: a text box takes the key
        // before an accelerator sees it, and the typing rule keeps it out of anything that does not.
        new(VirtualKey.V, VirtualKeyModifiers.Control, ShellCommand.NextPreset, 0, ShortcutDelivery.Accelerator),

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

    /// <summary>Every default shortcut, in the order the design document lists them (and the Shortcuts page shows them).</summary>
    public static IReadOnlyList<ShellShortcut> All => Table;

    /// <summary>The default shortcuts taken at the root on the way down.</summary>
    public static IEnumerable<ShellShortcut> PreEmpting =>
        Table.Where(shortcut => shortcut.Delivery == ShortcutDelivery.PreEmpt);

    /// <summary>The default shortcuts registered as accelerators, which fire only on a key nothing else wanted.</summary>
    public static IEnumerable<ShellShortcut> Accelerated =>
        Table.Where(shortcut => shortcut.Delivery == ShortcutDelivery.Accelerator);

    /// <summary>
    /// The default shortcut for <paramref name="key"/> with exactly <paramref name="modifiers"/> down, or null —
    /// including for all but the <see cref="ShellShortcut.WhileTyping"/> ones while <paramref name="typing"/>, since
    /// focus is then in something the keystroke belongs to more than the transport. The live shell asks
    /// <see cref="ShortcutBindings"/>, which asks the same question of the resolved table.
    /// </summary>
    public static ShellShortcut? Find(VirtualKey key, VirtualKeyModifiers modifiers, bool typing) =>
        Find(Table, key, modifiers, typing);

    /// <summary><see cref="Find(VirtualKey, VirtualKeyModifiers, bool)"/> over any table, such as a resolved one.</summary>
    public static ShellShortcut? Find(IReadOnlyList<ShellShortcut> table, VirtualKey key, VirtualKeyModifiers modifiers, bool typing)
    {
        ArgumentNullException.ThrowIfNull(table);
        foreach (ShellShortcut shortcut in table)
        {
            if (shortcut.Key == key && shortcut.Modifiers == modifiers)
            {
                return typing && !shortcut.WhileTyping ? null : shortcut;
            }
        }

        return null;
    }

    // ---- E6-S4: the action behind a row, and the table with the stored bindings applied -----------------------------

    /// <summary>
    /// The name of a row's action in <c>settings.json</c> (<c>shortcuts.playPause</c>, <c>shortcuts.seekBack30</c>,
    /// <c>shortcuts.volumeUp</c>): the command in camel case, with the amount folded in for the commands that carry
    /// one, since Seek forward 5 s and Seek back 30 s are four rows on one command.
    /// </summary>
    public static string ActionId(ShellShortcut shortcut)
    {
        string command = shortcut.Command.ToString();
        string id = char.ToLowerInvariant(command[0]) + command[1..];
        return shortcut.Command switch
        {
            ShellCommand.Seek => id + (shortcut.Amount < 0 ? "Back" : "Forward") + Math.Abs(shortcut.Amount).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ShellCommand.Volume => id + (shortcut.Amount < 0 ? "Down" : "Up"),
            // Six rows on one command (E6-S7): rate1..rate5, and rateClear for the zero that clears. Without the amount
            // all six shared shortcuts.rate, so rebinding one star moved every star and the page showed six "Rate" rows.
            ShellCommand.Rate => shortcut.Amount == 0
                ? id + "Clear"
                : id + ((int)shortcut.Amount).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => id,
        };
    }

    /// <summary>
    /// What the Shortcuts page calls the action. Named commands read as a person would say them; a command not named
    /// here is its enum name with spaces put back (<c>ToggleFocus</c> would be "Toggle focus"), so a row is never blank.
    /// </summary>
    public static string ActionName(ShellShortcut shortcut) => shortcut.Command switch
    {
        ShellCommand.Rate => shortcut.Amount switch
        {
            0 => "Clear rating",
            1 => "Rate 1 star",
            _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Rate {(int)shortcut.Amount} stars"),
        },
        ShellCommand.PlayPause => "Play / Pause",
        ShellCommand.Next => "Next track",
        ShellCommand.Previous => "Previous track",
        ShellCommand.Seek => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Seek {(shortcut.Amount < 0 ? "back" : "forward")} {Math.Abs(shortcut.Amount)} s"),
        ShellCommand.Volume => shortcut.Amount < 0 ? "Volume down" : "Volume up",
        ShellCommand.Mute => "Mute",
        ShellCommand.Shuffle => "Shuffle",
        ShellCommand.Repeat => "Repeat",
        ShellCommand.Queue => "Queue",
        ShellCommand.Diagnostics => "Diagnostics overlay",
        ShellCommand.Discovery => "Discovery mode",
        ShellCommand.Focus => "Focus mode",
        ShellCommand.Curation => "Curation mode",
        ShellCommand.ToggleFocus => "Toggle Focus",
        ShellCommand.LeaveFocus => "Leave Focus",
        ShellCommand.MiniPlayer => "Mini player",
        ShellCommand.OpenSettings => "Settings",
        ShellCommand.Undo => "Undo (Curation)",
        ShellCommand.Redo => "Redo (Curation)",
        ShellCommand.NextPreset => "Next preset",
        _ => Humanise(shortcut.Command.ToString()),
    };

    /// <summary>The default chord of a row, in the form the store and the page use.</summary>
    public static KeyChord DefaultChord(ShellShortcut shortcut) => new(shortcut.Key, shortcut.Modifiers);

    /// <summary>
    /// The table the shell listens to: <paramref name="defaults"/> with each action's stored binding applied.
    /// <paramref name="stored"/> answers for an action id with the chord string, an empty string for "unbound", or
    /// null for "no setting". A value that cannot be read leaves the default (and is logged), so a typo in the file
    /// costs nothing. If two actions end up on one chord — which the page never writes, but a hand-edited file can —
    /// the first in table order keeps it and the other is left unbound, because a table in which one chord names
    /// two actions is a table in which one of them silently never happens (<c>Every_key_appears_once</c>).
    /// </summary>
    public static IReadOnlyList<ShellShortcut> Resolve(IReadOnlyList<ShellShortcut> defaults, Func<string, string?> stored)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentNullException.ThrowIfNull(stored);
        var resolved = new List<ShellShortcut>(defaults.Count);
        var taken = new HashSet<KeyChord>();
        foreach (ShellShortcut shortcut in defaults)
        {
            string id = ActionId(shortcut);
            string? value = stored(id);
            KeyChord chord;
            if (value is null)
            {
                chord = DefaultChord(shortcut);
            }
            else if (value.Length == 0)
            {
                continue; // unbound on purpose
            }
            else if (!KeyChord.TryParse(value, out chord))
            {
                Serilog.Log.Warning("shortcuts.{Action} is '{Value}', which names no key; using the default", id, value);
                chord = DefaultChord(shortcut);
            }

            if (!taken.Add(chord))
            {
                Serilog.Log.Warning("shortcuts.{Action} would put a second action on {Chord}; leaving it unbound", id, chord);
                continue;
            }

            resolved.Add(shortcut with { Key = chord.Key, Modifiers = chord.Modifiers });
        }

        return resolved;
    }

    private static string Humanise(string pascal)
    {
        var text = new System.Text.StringBuilder(pascal.Length + 4);
        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];
            if (i > 0 && (char.IsUpper(c) || (char.IsDigit(c) && !char.IsDigit(pascal[i - 1]))))
            {
                text.Append(' ').Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }
}
