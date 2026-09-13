using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.Core;

namespace Tunqio.App.Shell;

/// <summary>The three modes (docs/ui-screens-and-flows.md, "Modes, defined precisely").</summary>
public enum ShellMode
{
    /// <summary>Browse and sample. The default, and where Esc from Focus goes when Focus was where the app started.</summary>
    Discovery,

    /// <summary>Listen: Now Playing takes the whole width and the sidebar is hidden.</summary>
    Focus,

    /// <summary>Organise: the library side takes the larger share in a column shape (Q-67), for E5-S4's dual pane.</summary>
    Curation,
}

/// <summary>Reads and writes <c>ui.mode</c>. Anything unrecognised is Discovery, the documented default.</summary>
public static class ShellModePolicy
{
    /// <summary>Parses <c>ui.mode</c>; anything unrecognised is the documented default.</summary>
    public static ShellMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "focus" => ShellMode.Focus,
        "curation" => ShellMode.Curation,
        _ => ShellMode.Discovery,
    };

    /// <summary>The value <c>ui.mode</c> stores for <paramref name="mode"/>.</summary>
    public static string Name(ShellMode mode) => mode switch
    {
        ShellMode.Focus => "focus",
        ShellMode.Curation => "curation",
        _ => "discovery",
    };
}

/// <summary>
/// The shell's mode (E5-S1, ADR-007: "UI mode is a property on <c>ShellState</c>"). One per process, owned by the
/// container, so the window, the switcher and the shortcuts all move the same value.
/// </summary>
/// <remarks>
/// <para>
/// It knows nothing about playback, and that is the design rather than an omission: AC-134 says switching modes never
/// interrupts playback, and the surest way to keep that true is for the object that switches them to have no way of
/// reaching the session at all.
/// </para>
/// <para>
/// <see cref="ReturnMode"/> is what makes Esc mean "back", not "Discovery" (flow 7): it is the mode Focus was
/// entered from. A launch that opens in Focus has nowhere to go back to, so it returns to the documented default.
/// </para>
/// </remarks>
public sealed class ShellState : ObservableObject
{
    private readonly ISettingsStore? _settings;
    private ShellMode _mode;

    /// <param name="settings">Where <c>ui.mode</c> is read from and written back to; null keeps the mode in memory.</param>
    public ShellState(ISettingsStore? settings)
    {
        _settings = settings;
        _mode = settings is null ? ShellMode.Discovery : ShellModePolicy.Parse(settings.GetValue<string?>(SettingsKeys.UiMode, null));
        ReturnMode = _mode == ShellMode.Focus ? ShellMode.Discovery : _mode;
    }

    /// <summary>The mode the shell is in.</summary>
    public ShellMode Mode
    {
        get => _mode;
        private set => SetProperty(ref _mode, value);
    }

    /// <summary>Where leaving Focus goes: the mode Focus was entered from.</summary>
    public ShellMode ReturnMode { get; private set; }

    /// <summary>
    /// Switches to <paramref name="mode"/> and remembers it. False when the shell is already there, so a shortcut
    /// that changed nothing leaves its key unhandled.
    /// </summary>
    public bool Select(ShellMode mode)
    {
        if (mode == _mode)
        {
            return false;
        }

        if (mode == ShellMode.Focus)
        {
            ReturnMode = _mode;
        }

        Mode = mode;
        if (_settings is not null)
        {
            _settings.SetValue(SettingsKeys.UiMode, ShellModePolicy.Name(mode));
            _settings.Flush();
        }

        return true;
    }

    /// <summary>F11: into Focus, or back out of it to where it was entered from.</summary>
    public bool ToggleFocus() => Select(_mode == ShellMode.Focus ? ReturnMode : ShellMode.Focus);

    /// <summary>
    /// Esc (flow 7). Only Focus has anything to leave; anywhere else this is false, so the key goes on to whatever
    /// else wanted it - a search box clearing, a flyout closing.
    /// </summary>
    public bool LeaveFocus() => _mode == ShellMode.Focus && Select(ReturnMode);
}
