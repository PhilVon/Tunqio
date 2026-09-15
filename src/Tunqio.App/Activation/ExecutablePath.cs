using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tunqio.App.Activation;

/// <summary>
/// A file path spelled the way the file system stores it, and the one relaunch that makes a differently spelled launch of
/// Tunqio.exe find the running instance (E7-S4, T-192).
/// </summary>
/// <remarks>
/// <para>
/// Why a spelling matters. The Windows App SDK's <c>AppInstance</c> scopes every instance key by an app id it computes from
/// the process's own module path (<c>ComputeAppId</c> in dev/AppLifecycle/Association.cpp hashes
/// <c>GetModuleFileNameW(nullptr)</c>; <c>AppInstance.cpp</c> names the key's mutex <c>App.&lt;hash&gt;_&lt;key&gt;_Mutex</c> and
/// the instance list <c>App.&lt;hash&gt;_Module</c>). An unpackaged process's module path is spelled exactly as whoever started
/// it spelled it. So <c>tunqio.exe</c> started from a lowercase path (COM's LocalServer32 for a toast press, a shortcut, a
/// script) hashes to a different app, looks for <see cref="InstanceKey"/> in a different instance list, finds nothing, and
/// becomes a second Tunqio on the same data root, however carefully the key itself is normalised. The SDK's GitHub source
/// folds the path to lowercase before hashing (PR #5696), but the runtime Tunqio shipped when this was measured (1.8.260804001; 2.4 since T-87, and the relaunch stays because it costs nothing) did not:
/// see T-192 on the board.
/// </para>
/// <para>
/// The fix is to make the module path canonical rather than to change the key: an unpackaged launch whose own path is not
/// the file system's spelling of it starts Tunqio.exe again from that spelling with the same arguments and exits
/// (<see cref="RelaunchTarget"/>, called from <c>Program.Main</c>). The SDK offers no way to seed the app id from managed
/// code, so this is the one place every launch goes through. A packaged process is left alone: Windows starts it from the
/// manifest's path, and its file and protocol activations could not be carried over a relaunch.
/// </para>
/// </remarks>
public static class ExecutablePath
{
    /// <summary>
    /// Set on a relaunched process. A process that finds it set never relaunches again, so a path the file system reports
    /// in a form the new process still does not match cannot start a loop.
    /// </summary>
    public const string RelaunchedVariable = "TUNQIO_CANONICAL_RELAUNCH";

    private const uint FileNameNormalized = 0;

    /// <summary><paramref name="path"/> with the case the file system has for it, or <paramref name="path"/> when that cannot be read.</summary>
    public static string WithTrueCase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            char[] buffer = new char[1024];
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, FileNameNormalized);
            if (length == 0 || length >= buffer.Length)
            {
                return path;
            }

            string final = new(buffer, 0, (int)length);
            if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                return @"\\" + final[8..];
            }

            return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    /// <summary>
    /// The path this process must be started again from so AppInstance finds the running instance, or null to carry on here:
    /// null when the process has package identity, was itself relaunched, has no path, or already runs from
    /// <paramref name="truePath"/> spelled exactly (ordinal, because the SDK's hash is).
    /// </summary>
    /// <param name="processPath">This process's module path, as it was started (<see cref="Environment.ProcessPath"/>).</param>
    /// <param name="truePath">The same file as the file system spells it (<see cref="WithTrueCase"/>).</param>
    /// <param name="packaged">Whether the process has package identity.</param>
    /// <param name="relaunched">Whether <see cref="RelaunchedVariable"/> was set on this process.</param>
    public static string? RelaunchTarget(string? processPath, string? truePath, bool packaged, bool relaunched)
    {
        if (packaged || relaunched || string.IsNullOrWhiteSpace(processPath) || string.IsNullOrWhiteSpace(truePath))
        {
            return null;
        }

        return string.Equals(processPath, truePath, StringComparison.Ordinal) ? null : truePath;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
