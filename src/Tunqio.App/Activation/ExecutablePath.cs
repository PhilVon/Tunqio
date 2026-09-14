using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tunqio.App.Activation;

/// <summary>
/// A file path spelled the way the file system stores it (E7-S4). COM starts Tunqio for a toast press from the lowercase path the
/// Windows App SDK registered, and a process started from a differently spelled path is not found by the running instance's
/// single-instance key; the press trampoline in <see cref="Program"/> starts Tunqio again from the true spelling.
/// </summary>
public static class ExecutablePath
{
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

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
