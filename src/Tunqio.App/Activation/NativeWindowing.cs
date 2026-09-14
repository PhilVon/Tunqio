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
