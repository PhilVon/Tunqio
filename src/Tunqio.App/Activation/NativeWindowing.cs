using System.Runtime.InteropServices;

namespace Tunqio.App.Activation;

/// <summary>
/// The Win32 calls single instance needs. Windows gives the foreground to the process the user acted on, which is the
/// second instance, not the running one; so the second instance passes that right on with
/// <see cref="AllowFor"/> before it redirects, and the running one restores and raises its window.
/// </summary>
internal static class NativeWindowing
{
    private const int SwRestore = 9;

    /// <summary>Restores <paramref name="hwnd"/> if minimised and asks for the foreground; false when Windows refused.</summary>
    public static bool BringToForeground(nint hwnd)
    {
        if (hwnd == nint.Zero)
        {
            return false;
        }

        if (IsIconic(hwnd))
        {
            _ = ShowWindow(hwnd, SwRestore);
        }

        return SetForegroundWindow(hwnd);
    }

    /// <summary>Lets <paramref name="processId"/> take the foreground this process was given.</summary>
    public static bool AllowFor(uint processId) => AllowSetForegroundWindow(processId);

    /// <summary>
    /// True when the foreground window is one of this process's, visible and not minimised (E7-S4). A window hidden to the tray
    /// is not visible, a minimised one is iconic, and one behind another window is not the foreground window.
    /// </summary>
    public static bool ThisProcessIsInForeground() => Foreground().IsThisProcess;

    /// <summary>
    /// What the foreground window is, for the rule above and for the log line that records the decision. The foreground window of
    /// a WinUI 3 window is not always the visible top-level window itself: check-toasts measured a hidden helper window of the
    /// process in the foreground while its main window was plainly in front. So visible and minimised are read from its root
    /// owner, the window a person sees.
    /// </summary>
    public static ForegroundWindow Foreground()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == nint.Zero)
        {
            return new ForegroundWindow(nint.Zero, nint.Zero, 0, false, false);
        }

        _ = GetWindowThreadProcessId(foreground, out uint processId);
        nint root = GetAncestor(foreground, GaRootOwner);
        if (root == nint.Zero)
        {
            root = foreground;
        }

        return new ForegroundWindow(foreground, root, processId, IsWindowVisible(root) || IsWindowVisible(foreground), IsIconic(root));
    }

    /// <summary>The foreground window and its root owner: the process, and whether the owner is visible and minimised.</summary>
    public readonly record struct ForegroundWindow(nint Handle, nint RootOwner, uint ProcessId, bool Visible, bool Minimised)
    {
        /// <summary>True when it is this process's, visible and not minimised.</summary>
        public bool IsThisProcess => Handle != nint.Zero && ProcessId == (uint)Environment.ProcessId && Visible && !Minimised;

        public override string ToString() => FormattableString.Invariant(
            $"hwnd 0x{Handle:X} (root owner 0x{RootOwner:X}), pid {ProcessId} (this {Environment.ProcessId}), visible {Visible}, minimised {Minimised}");
    }

    private const uint GaRootOwner = 3;

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
