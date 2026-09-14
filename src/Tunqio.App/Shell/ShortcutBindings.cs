using Tunqio.App.Controls;
using Tunqio.Core;
using Windows.System;

namespace Tunqio.App.Shell;

/// <summary>
/// The shell's shortcut table as it is right now (E6-S4): <see cref="ShellShortcuts.All"/> with the bindings in the
/// settings store applied, kept current as the store changes. The window asks this what a key means and registers
/// its accelerators from it; the Shortcuts page writes through it. Both sit over the one store, which is the source
/// of truth — the window and the page each hold their own instance and agree because the store tells both.
/// </summary>
public sealed class ShortcutBindings
{
    private readonly ISettingsStore? _settings;
    private IReadOnlyList<ShellShortcut> _table;
    private Task _saving = Task.CompletedTask;

    /// <param name="settings">Null (the spike modes) leaves the table on its defaults for good.</param>
    public ShortcutBindings(ISettingsStore? settings)
    {
        _settings = settings;
        _table = Resolve();
        if (settings is not null)
        {
            settings.Changed += OnSettingChanged;
        }
    }

    /// <summary>
    /// Raised after a <c>shortcuts.*</c> key changed and the table was rebuilt, on the thread that wrote the setting.
    /// The window posts its re-registration to its own thread, as it does for <c>ui.theme</c>.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>Every bound shortcut, in table order, with the stored keys applied; unbound actions are absent.</summary>
    public IReadOnlyList<ShellShortcut> Table => _table;

    /// <summary>The bound shortcuts taken at the root on the way down.</summary>
    public IEnumerable<ShellShortcut> PreEmpting => _table.Where(s => s.Delivery == ShortcutDelivery.PreEmpt);

    /// <summary>The bound shortcuts registered as accelerators.</summary>
    public IEnumerable<ShellShortcut> Accelerated => _table.Where(s => s.Delivery == ShortcutDelivery.Accelerator);

    /// <summary><see cref="ShellShortcuts.Find(VirtualKey, VirtualKeyModifiers, bool)"/> over the current table.</summary>
    public ShellShortcut? Find(VirtualKey key, VirtualKeyModifiers modifiers, bool typing) =>
        ShellShortcuts.Find(_table, key, modifiers, typing);

    /// <summary>The chord an action is on now, or null when it is unbound.</summary>
    public KeyChord? ChordOf(string actionId)
    {
        foreach (ShellShortcut shortcut in _table)
        {
            if (ShellShortcuts.ActionId(shortcut) == actionId)
            {
                return new KeyChord(shortcut.Key, shortcut.Modifiers);
            }
        }

        return null;
    }

    /// <summary>The action that holds <paramref name="chord"/> now, or null when nothing does.</summary>
    public ShellShortcut? Holder(KeyChord chord)
    {
        foreach (ShellShortcut shortcut in _table)
        {
            if (shortcut.Key == chord.Key && shortcut.Modifiers == chord.Modifiers)
            {
                return shortcut;
            }
        }

        return null;
    }

    /// <summary>True when the store holds no binding for the action, so it is on its default.</summary>
    public bool IsDefault(string actionId) => Stored(actionId) is null;

    /// <summary>
    /// Binds an action to <paramref name="chord"/>, or to nothing when it is null. A chord equal to the action's
    /// default removes the key instead of storing it, so <c>settings.json</c> only ever holds what differs from the
    /// code. The store's <c>Changed</c> rebuilds the table before this returns; the save follows.
    /// </summary>
    public void Set(ShellShortcut action, KeyChord? chord)
    {
        if (_settings is null)
        {
            return;
        }

        Write(action, chord);
        Save();
    }

    /// <summary>
    /// Takes <paramref name="chord"/> from <paramref name="holder"/> for <paramref name="asker"/>: the holder is
    /// released first, so no table ever names two actions on the chord, and the two writes are saved together.
    /// </summary>
    public void Move(ShellShortcut holder, ShellShortcut asker, KeyChord chord)
    {
        if (_settings is null)
        {
            return;
        }

        Write(holder, null);
        Write(asker, chord);
        Save();
    }

    /// <summary>Removes every action's key, which puts the whole table back on its defaults.</summary>
    public void Reset()
    {
        if (_settings is null)
        {
            return;
        }

        foreach (ShellShortcut shortcut in ShellShortcuts.All)
        {
            string key = SettingsKeys.Shortcut(ShellShortcuts.ActionId(shortcut));
            if (_settings.Contains(key))
            {
                _settings.SetValue<string?>(key, null);
            }
        }

        Serilog.Log.Information("Shortcuts reset to their defaults");
        Save();
    }

    /// <summary>Completes when every save asked for so far has finished, well or badly; for a test that relaunches.</summary>
    public Task WaitForSavesAsync() =>
        _saving.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private void Write(ShellShortcut action, KeyChord? chord)
    {
        string key = SettingsKeys.Shortcut(ShellShortcuts.ActionId(action));
        string? value = chord is null ? string.Empty : chord == ShellShortcuts.DefaultChord(action) ? null : chord.ToString();
        _settings!.SetValue(key, value);
        Serilog.Log.Information("Shortcut {Action} is now {Chord}", ShellShortcuts.ActionName(action), chord?.ToString() ?? "unbound");
    }

    /// <summary>
    /// Saves now. The store serialises its own flushes and a queued flush writes the newest state (T-157), so this no
    /// longer chains one save behind the last; the latest save is kept only for <see cref="WaitForSavesAsync"/>, which
    /// completes once it has written everything set before it was asked for.
    /// </summary>
    private void Save()
    {
        _saving = _settings!.FlushAsync();
        _saving.Forget("Save shortcuts");
    }

    private string? Stored(string actionId) => _settings?.GetValue<string?>(SettingsKeys.Shortcut(actionId), null);

    private IReadOnlyList<ShellShortcut> Resolve() => ShellShortcuts.Resolve(ShellShortcuts.All, Stored);

    private void OnSettingChanged(object? sender, string key)
    {
        if (!key.StartsWith(SettingsKeys.ShortcutsPrefix, StringComparison.Ordinal))
        {
            return;
        }

        _table = Resolve();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
