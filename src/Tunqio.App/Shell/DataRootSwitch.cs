namespace Tunqio.App.Shell;

/// <summary>
/// <c>Tunqio.exe --data-root PATH</c> (E6-S6): the app keeps settings.json, library.db, logs, art and presets under PATH
/// instead of <c>%LocalAppData%\Tunqio</c>. It exists so a harness can run the app against a scratch profile, which is the
/// only isolation that cannot touch the real one: parking the real library.db aside for a run has already failed once
/// (T-183), and under the Claude desktop app <c>%LocalAppData%</c> may be a package-redirected copy anyway.
/// </summary>
public static class DataRootSwitch
{
    /// <summary>The switch itself.</summary>
    public const string Name = "--data-root";

    /// <summary>The data root after the switch, or null when the switch is absent or has no path.</summary>
    public static string? Path(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        int i = Array.IndexOf(args, Name);
        return i >= 0 && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
            ? args[i + 1]
            : null;
    }
}
