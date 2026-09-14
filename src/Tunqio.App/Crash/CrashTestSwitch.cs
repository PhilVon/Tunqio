using Tunqio.App.Shell;

namespace Tunqio.App.Crash;

/// <summary>Which crash <c>--crash-test</c> forces.</summary>
public enum CrashTestKind
{
    /// <summary>An exception thrown on a background thread: the AppDomain handler.</summary>
    Managed,

    /// <summary>An exception thrown on the XAML thread: the XAML handler, then the stowed-exception fail-fast.</summary>
    Xaml,

    /// <summary>An access violation on a thread inside mpcore.dll (<c>mp_debug_crash</c>): the native filter.</summary>
    Native,
}

/// <summary>
/// <c>Tunqio.exe --data-root PATH --crash-test managed|xaml|native</c> with <c>TUNQIO_CRASH_TEST=1</c> in the environment (E8-S5):
/// once audio is up, the shell crashes the way it asks, for tools/check-crash-report.ps1. All three conditions, so nobody meets
/// it by accident: a normal launch has no such variable and no scratch data root, and the switch alone does nothing.
/// </summary>
public static class CrashTestSwitch
{
    /// <summary>The switch itself.</summary>
    public const string Name = "--crash-test";

    /// <summary>The variable that must be <c>1</c> for the switch to count.</summary>
    public const string EnvironmentVariable = "TUNQIO_CRASH_TEST";

    /// <summary>The crash asked for, or null when any of the three conditions is missing or the kind is not one of the three.</summary>
    public static CrashTestKind? Requested(string[] args, string? environmentValue)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!string.Equals(environmentValue, "1", StringComparison.Ordinal) || DataRootSwitch.Path(args) is null)
        {
            return null;
        }

        int i = Array.IndexOf(args, Name);
        if (i < 0 || i + 1 >= args.Length)
        {
            return null;
        }

        return args[i + 1] switch
        {
            "managed" => CrashTestKind.Managed,
            "xaml" => CrashTestKind.Xaml,
            "native" => CrashTestKind.Native,
            _ => null,
        };
    }

    /// <summary>The crash this process was asked for, reading the real environment.</summary>
    public static CrashTestKind? Requested(string[] args) => Requested(args, Environment.GetEnvironmentVariable(EnvironmentVariable));

    /// <summary>
    /// <c>SetErrorMode</c> for THIS process only: no "Tunqio has stopped working" box for a crash that was asked for. Nothing
    /// machine-wide and nothing in the registry; Windows still records its Application Error event.
    /// </summary>
    public static void SuppressCrashDialogsForThisProcess()
    {
        const uint FailCriticalErrors = 0x0001;
        const uint NoGpFaultErrorBox = 0x0002;
        _ = SetErrorMode(FailCriticalErrors | NoGpFaultErrorBox);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
