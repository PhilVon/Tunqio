namespace Tunqio.Interop;

/// <summary>
/// <c>mp_debug_crash</c> (ABI 0.20, T-84): a genuine access violation on a thread inside mpcore.dll, for the crash reporter's
/// harness (tools/check-crash-report.ps1). Nothing else can crash inside the core on purpose, because every export runs under
/// the SEH guard. The shell calls this only for <c>--crash-test native</c>, which needs an environment variable and a scratch
/// data root.
/// </summary>
public static class NativeCrashTest
{
    /// <summary>Ends the process. Throws <see cref="NativeException"/> only if the process is somehow still alive 10 s later.</summary>
    public static void CrashOnCoreThread() =>
        NativeException.ThrowIfFailed(NativeMethods.DebugCrash(), "mp_debug_crash");
}
