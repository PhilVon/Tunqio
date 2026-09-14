using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Tunqio.App.Shell;

/// <summary>
/// The "press keys" control of the Shortcuts page (E6-S4). A button that, once pressed, takes focus and turns the
/// next key-down into a <see cref="KeyChord"/>: a modifier on its own is waited past, Esc cancels, and losing focus
/// cancels. While it is recording it owns the keyboard — the shell root's tunnelling handler steps aside for it
/// (<c>MainWindow.IsCapturingShortcut</c>), it marks the key handled before its own button behaviour or anything
/// below sees it, and it answers the accelerator pass as handled, so pressing Space to bind Space neither presses
/// this button nor toggles playback.
/// </summary>
public sealed partial class ShortcutCaptureButton : Button
{
    /// <summary>What the button says while it waits for keys.</summary>
    public const string Prompt = "Press keys…";

    private object? _restingContent;

    public ShortcutCaptureButton()
    {
        Click += (_, _) => Begin();
        LostFocus += (_, _) => End(cancelled: true);
        ProcessKeyboardAccelerators += (_, args) => args.Handled = args.Handled || IsCapturing;
    }

    /// <summary>A chord was pressed.</summary>
    public event EventHandler<KeyChord>? Captured;

    /// <summary>True from the press until a chord, Esc or lost focus.</summary>
    public bool IsCapturing { get; private set; }

    /// <summary>Starts recording; the next chord is raised on <see cref="Captured"/>.</summary>
    public void Begin()
    {
        if (IsCapturing)
        {
            return;
        }

        IsCapturing = true;
        _restingContent = Content;
        Content = Prompt;
        Focus(FocusState.Programmatic);
    }

    protected override void OnPreviewKeyDown(KeyRoutedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!IsCapturing)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            End(cancelled: true);
            return;
        }

        if (KeyChord.FromKeyDown(e.Key, ShellKeyboard.CurrentModifiers()) is { } chord)
        {
            End(cancelled: false);
            Captured?.Invoke(this, chord);
        }
    }

    /// <summary>The key-up of the chord, and of Space in particular, must not press the button the chord was typed into.</summary>
    protected override void OnPreviewKeyUp(KeyRoutedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (IsCapturing)
        {
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyUp(e);
    }

    private void End(bool cancelled)
    {
        if (!IsCapturing)
        {
            return;
        }

        IsCapturing = false;
        Content = _restingContent;
        if (cancelled)
        {
            Serilog.Log.Debug("Shortcut capture cancelled");
        }
    }
}
