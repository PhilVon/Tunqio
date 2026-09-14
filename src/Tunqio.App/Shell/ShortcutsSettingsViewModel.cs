using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Tunqio.App.Shell;

/// <summary>One row of the Shortcuts page: an action and the key it is on.</summary>
public sealed partial class ShortcutRow : ObservableObject
{
    /// <summary>What an unbound row shows.</summary>
    public const string NotBound = "Not bound";

    public ShortcutRow(ShellShortcut action)
    {
        Action = action;
        ActionId = ShellShortcuts.ActionId(action);
        Name = ShellShortcuts.ActionName(action);
        DefaultDisplay = ShellShortcuts.DefaultChord(action).ToString();
        Display = DefaultDisplay;
        IsBound = true;
        IsDefault = true;
    }

    /// <summary>The table row this is over — the default key, and the delivery and amount that stay with the action.</summary>
    public ShellShortcut Action { get; }

    public string ActionId { get; }

    public string Name { get; }

    /// <summary>The default chord, for the "Space by default" note on a changed row.</summary>
    public string DefaultDisplay { get; }

    /// <summary>The chord in words, or <see cref="NotBound"/>.</summary>
    [ObservableProperty]
    public partial string Display { get; set; }

    [ObservableProperty]
    public partial bool IsBound { get; set; }

    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    /// <summary>"Space by default" on a changed row; empty on a default one.</summary>
    public string DefaultNote => IsDefault ? string.Empty : DefaultDisplay + " by default";

    public string ChangeAutomationName => "Change " + Name;

    public string ClearAutomationName => "Clear " + Name;

    /// <summary>A UIA id a harness can find the row's key by without knowing the key (<c>Binding.playPause</c>).</summary>
    public string BindingAutomationId => "Binding." + ActionId;

    partial void OnIsDefaultChanged(bool value) => OnPropertyChanged(nameof(DefaultNote));
}

/// <summary>What <see cref="ShortcutsSettingsViewModel.Bind"/> did with a captured chord.</summary>
public enum BindOutcome
{
    /// <summary>The row was already on that chord.</summary>
    Unchanged,

    /// <summary>Bound and stored.</summary>
    Bound,

    /// <summary>Another action holds the chord; nothing was written and <see cref="ShortcutsSettingsViewModel.Conflict"/> says which.</summary>
    Conflict,
}

/// <summary>A chord that was asked for while another action held it.</summary>
/// <param name="Row">The row that asked.</param>
/// <param name="Chord">What it asked for.</param>
/// <param name="Holder">The row that has it.</param>
public sealed record ShortcutConflict(ShortcutRow Row, KeyChord Chord, ShortcutRow Holder)
{
    /// <summary>"Ctrl+M is already Mini player."</summary>
    public string Title => $"{Chord} is already {Holder.Name}";

    /// <summary>What taking it would mean, said before it is done.</summary>
    public string Consequence => $"Use it for {Row.Name} anyway? {Holder.Name} will be left with no shortcut until you give it one.";
}

/// <summary>
/// Settings › Shortcuts (E6-S4): every row of <see cref="ShellShortcuts"/> with the key it is on, over
/// <see cref="ShortcutBindings"/>. A row is changed by pressing the new keys, cleared, or — all at once — reset.
/// </summary>
/// <remarks>
/// A chord another action holds is never taken silently (AC-437). The choice here is to <em>offer</em> the key
/// rather than refuse it outright: refusing would make swapping two keys impossible without a third to park one on,
/// and the one thing worse than a shared key is a rule nobody can work around. So the page names the holder, and
/// only on "use it anyway" unbinds the holder — which then shows "Not bound" in its row, where a silently
/// overwritten binding shows nothing — and binds the asker. The two never share a key at any point, because the
/// holder is released before the asker is bound.
/// </remarks>
public sealed partial class ShortcutsSettingsViewModel : ObservableObject
{
    private readonly ShortcutBindings _bindings;

    public ShortcutsSettingsViewModel(ShortcutBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        _bindings = bindings;
        Rows = new ObservableCollection<ShortcutRow>(ShellShortcuts.All.Select(action => new ShortcutRow(action)));
        Refresh();
        _bindings.Changed += (_, _) => Refresh();
    }

    /// <summary>One row per table row, in table order.</summary>
    public ObservableCollection<ShortcutRow> Rows { get; }

    /// <summary>The chord waiting on the person's answer, or null.</summary>
    [ObservableProperty]
    public partial ShortcutConflict? Conflict { get; private set; }

    /// <summary>True while any row is off its default, which is when Reset has something to do.</summary>
    [ObservableProperty]
    public partial bool HasChanges { get; private set; }

    /// <summary>
    /// Puts <paramref name="row"/> on <paramref name="chord"/>, unless another row holds it — then nothing is written
    /// and <see cref="Conflict"/> waits for <see cref="TakeConflictingKey"/> or <see cref="KeepConflictingKey"/>.
    /// </summary>
    public BindOutcome Bind(ShortcutRow row, KeyChord chord)
    {
        ArgumentNullException.ThrowIfNull(row);
        Conflict = null;
        if (_bindings.ChordOf(row.ActionId) == chord)
        {
            return BindOutcome.Unchanged;
        }

        ShellShortcut? holder = _bindings.Holder(chord);
        if (holder is { } held && ShellShortcuts.ActionId(held) != row.ActionId)
        {
            Conflict = new ShortcutConflict(row, chord, Rows.First(r => r.ActionId == ShellShortcuts.ActionId(held)));
            Serilog.Log.Information("Shortcut {Action} asked for {Chord}, which {Holder} holds", row.Name, chord, Conflict.Holder.Name);
            return BindOutcome.Conflict;
        }

        _bindings.Set(row.Action, chord);
        return BindOutcome.Bound;
    }

    /// <summary>Answers the conflict with yes: the holder is unbound first, then the asker is bound.</summary>
    public void TakeConflictingKey()
    {
        if (Conflict is not { } conflict)
        {
            return;
        }

        Conflict = null;
        _bindings.Move(conflict.Holder.Action, conflict.Row.Action, conflict.Chord);
    }

    /// <summary>Answers the conflict with no: nothing changes.</summary>
    public void KeepConflictingKey() => Conflict = null;

    /// <summary>Leaves the row with no key.</summary>
    public void Unbind(ShortcutRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Conflict = null;
        if (row.IsBound)
        {
            _bindings.Set(row.Action, null);
        }
    }

    /// <summary>Every row back to its default, and every <c>shortcuts.*</c> key gone from the file.</summary>
    public void RestoreDefaults()
    {
        Conflict = null;
        _bindings.Reset();
    }

    private void Refresh()
    {
        bool changes = false;
        foreach (ShortcutRow row in Rows)
        {
            KeyChord? chord = _bindings.ChordOf(row.ActionId);
            row.Display = chord?.ToString() ?? ShortcutRow.NotBound;
            row.IsBound = chord is not null;
            row.IsDefault = _bindings.IsDefault(row.ActionId);
            changes |= !row.IsDefault;
        }

        HasChanges = changes;
    }
}
