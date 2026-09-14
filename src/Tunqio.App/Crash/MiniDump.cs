using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Tunqio.App.Crash;

/// <summary>
/// <c>MiniDumpWriteDump</c> on this process, from inside it (E8-S5): no WER LocalDumps keys and no second process, so nothing
/// about the machine changes when crash reporting is turned on. Best effort by nature: a process that is crashing may not
/// manage it, and <see cref="Write"/> never throws.
/// </summary>
internal static class MiniDump
{
    /// <summary>
    /// A modest dump: every thread's stack and context, the module list (loaded and unloaded) and thread and process data
    /// (the TEB and PEB), and no heap. Enough to see where each thread was and which module faulted; a few megabytes for
    /// the shell rather than the hundreds a full-memory dump costs.
    /// </summary>
    public const uint DumpType = MiniDumpNormal | MiniDumpWithUnloadedModules | MiniDumpWithProcessThreadData | MiniDumpWithThreadInfo;

    private const uint MiniDumpNormal = 0x00000000;
    private const uint MiniDumpWithUnloadedModules = 0x00000020;
    private const uint MiniDumpWithProcessThreadData = 0x00000100;
    private const uint MiniDumpWithThreadInfo = 0x00001000;

    /// <summary>
    /// Binds the P/Invokes now, while nothing is crashing: resolving dbghelp.dll for the first time inside a crash means
    /// taking the loader lock in a process that may already hold it.
    /// </summary>
    public static void Prepare()
    {
        Marshal.Prelink(typeof(MiniDump).GetMethod(nameof(MiniDumpWriteDump), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
        Marshal.Prelink(typeof(MiniDump).GetMethod(nameof(MiniDumpWriteDumpWithException), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
        Marshal.Prelink(typeof(MiniDump).GetMethod(nameof(GetCurrentThreadId), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
    }

    /// <summary>
    /// Writes the dump to <paramref name="path"/>, with the faulting thread's exception record when
    /// <paramref name="exceptionPointers"/> is not zero. Returns its size, or 0 with <paramref name="problem"/> saying why.
    /// </summary>
    public static long Write(string path, nint exceptionPointers, out string? problem)
    {
        problem = null;
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            bool written;
            if (exceptionPointers == 0)
            {
                written = MiniDumpWriteDump(GetCurrentProcess(), (uint)Environment.ProcessId, file.SafeFileHandle, DumpType, 0, 0, 0);
            }
            else
            {
                var info = new MinidumpExceptionInformation { ThreadId = GetCurrentThreadId(), ExceptionPointers = exceptionPointers, ClientPointers = 0 };
                written = MiniDumpWriteDumpWithException(GetCurrentProcess(), (uint)Environment.ProcessId, file.SafeFileHandle, DumpType, ref info, 0, 0);
            }

            if (!written)
            {
                problem = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"MiniDumpWriteDump failed with error {Marshal.GetLastPInvokeError()}");
                return 0;
            }

            file.Flush(flushToDisk: true);
            return file.Length;
        }
#pragma warning disable CA1031 // Inside a crash: whatever went wrong is reported in the text, never thrown.
        catch (Exception e)
#pragma warning restore CA1031
        {
            problem = e.GetType().Name + ": " + e.Message;
            return 0;
        }
    }

    /// <summary>MINIDUMP_EXCEPTION_INFORMATION, which dbghelp.h declares under pshpack4.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct MinidumpExceptionInformation
    {
        public uint ThreadId;
        public nint ExceptionPointers;
        public int ClientPointers;
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(nint process, uint processId, SafeFileHandle file, uint dumpType, nint exceptionParam, nint userStreamParam, nint callbackParam);

    [DllImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDumpWithException(nint process, uint processId, SafeFileHandle file, uint dumpType, ref MinidumpExceptionInformation exceptionParam, nint userStreamParam, nint callbackParam);
}
