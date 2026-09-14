namespace Tunqio.App.Shell;

/// <summary>
/// <c>Tunqio.exe --export-diagnostics FILE [--redact-paths]</c> (E6-S5): once audio is up, the shell writes the
/// diagnostics zip the About page's Export button would, to FILE, with no picker, and carries on running. It
/// exists for <c>tools/check-about.ps1</c>, which drives the page by UIA and cannot drive a file picker, and it is
/// the same exporter the button calls, so what the harness opens is what a person would get.
/// </summary>
public static class DiagnosticsExportSwitch
{
    /// <summary>The switch itself.</summary>
    public const string Name = "--export-diagnostics";

    /// <summary>The flag that turns path redaction on for the switch; the page's own default is on.</summary>
    public const string RedactFlag = "--redact-paths";

    /// <summary>The zip path after the switch, or null when the switch is absent or has no path.</summary>
    public static string? Path(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        int i = Array.IndexOf(args, Name);
        return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
    }

    /// <summary>Whether the paths are to be redacted.</summary>
    public static bool Redact(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Contains(RedactFlag, StringComparer.Ordinal);
    }
}
