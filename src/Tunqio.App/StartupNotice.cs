namespace Tunqio.App;

public enum StartupNoticeSeverity
{
    Informational,
    Warning,
    Error,
}

/// <summary>Something the shell must tell the user as it appears (a database recovery, a missing library).</summary>
public sealed record StartupNotice(string Title, string Message, StartupNoticeSeverity Severity);
