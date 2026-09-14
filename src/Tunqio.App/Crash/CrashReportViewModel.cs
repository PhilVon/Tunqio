using System.Globalization;
using Tunqio.Core;

namespace Tunqio.App.Crash;

/// <summary>What the user chose for a report.</summary>
public enum CrashReportDecision
{
    /// <summary>Kept on this PC, and included in a diagnostics export.</summary>
    Kept,

    /// <summary>Deleted: the Delete button, which is how the dialog is declined.</summary>
    Deleted,
}

/// <summary>
/// The next launch's crash report dialog (E8-S5): with <c>diagnostics.crashReporting</c> on and a report nobody has answered, it
/// says in plain words what was captured (the exception, the log lines, the dump's size and where it is) and offers Keep or
/// Delete. Each report is offered once: Keep marks it and Delete removes it, so neither comes back. Closing the dialog without
/// choosing (Esc) keeps it, because a report should not be destroyed by a key press that did not say so; that is recorded
/// the same way as Keep, so it is not offered again either.
/// </summary>
/// <remarks>With the setting off, no report is offered, even one captured while it was on; it stays on disk until it is turned on again.</remarks>
public sealed class CrashReportViewModel
{
    /// <summary>The dialog's title, which is also its automation name.</summary>
    public const string Title = "Tunqio closed unexpectedly";

    /// <summary>At most this many reports are offered in one launch; a crash loop's backlog is not a wall of dialogs.</summary>
    public const int MaxOfferedPerLaunch = 3;

    private readonly CrashReportStore _store;
    private readonly ISettingsStore _settings;
    private readonly HashSet<string> _offered = new(StringComparer.Ordinal);

    public CrashReportViewModel(CrashReportStore store, ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        _store = store;
        _settings = settings;
    }

    /// <summary>The report being offered, once <see cref="Next"/> has found one.</summary>
    public CrashReport? Report { get; private set; }

    /// <summary>What was chosen for <see cref="Report"/>; null until a choice is made.</summary>
    public CrashReportDecision? Decision { get; private set; }

    /// <summary>
    /// Moves to the newest report nobody has answered and not yet offered in this launch, and returns it; null when the setting
    /// is off, there is none, or <see cref="MaxOfferedPerLaunch"/> have been offered.
    /// </summary>
    public CrashReport? Next()
    {
        Report = null;
        Decision = null;
        if (!CrashReporter.ReadEnabled(_settings) || _offered.Count >= MaxOfferedPerLaunch)
        {
            return null;
        }

        Report = _store.Pending().FirstOrDefault(r => !_offered.Contains(r.Id));
        if (Report is not null)
        {
            _offered.Add(Report.Id);
            Serilog.Log.Information("Crash report {Id}: offered ({Type}, dump {DumpBytes} bytes, {Lines} log lines)", Report.Id, Report.Info?.ExceptionType ?? "not recorded", Report.DumpBytes, Report.LogLines.Count);
        }

        return Report;
    }

    /// <summary>The first sentence: what happened and that nothing has left the PC.</summary>
    public string Summary => Report is null
        ? string.Empty
        : "Tunqio stopped because of an error" + When + ". Crash reporting is on, so Tunqio saved a report about it on this PC. Nothing has been sent anywhere.";

    /// <summary>The exception, as the report recorded it.</summary>
    public string ExceptionLine => Report?.Info is { } info
        ? info.ExceptionType + (info.Message.Length > 0 ? ": " + info.Message : string.Empty)
        : "Not recorded: Tunqio ended before it could finish writing the report.";

    /// <summary>The caption over the log lines.</summary>
    public string LogCaption => Report is null || Report.LogLines.Count == 0
        ? "No log lines were saved with this report."
        : string.Create(CultureInfo.InvariantCulture, $"The last {Report.LogLines.Count} lines of Tunqio's log before it stopped:");

    /// <summary>The log lines, one per line.</summary>
    public string LogText => Report is null ? string.Empty : string.Join(Environment.NewLine, Report.LogLines);

    /// <summary>The dump's size and location, and what a dump can hold.</summary>
    public string DumpLine => Report switch
    {
        null => string.Empty,
        { DumpBytes: > 0 } r => "A crash dump of " + CrashReporter.FormatSize(r.DumpBytes) + " is saved at " + r.DumpPath +
            ". A crash dump is a copy of part of Tunqio's memory when it stopped, so it can contain file paths and track names.",
        { Info.DumpProblem: { } problem } => "No crash dump could be saved (" + problem + ").",
        _ => "No crash dump was saved.",
    };

    /// <summary>What the two buttons do.</summary>
    public static string ChoiceLine =>
        "Nothing leaves this PC unless you send it yourself. Keep report leaves it in Tunqio's crashes folder, and Export diagnostics in Settings > About & Diagnostics includes it in the zip, which you can send if you choose. Delete report removes it now.";

    /// <summary>Keep: marks the report so it is exported and not offered again. False when there is nothing to decide.</summary>
    public bool Keep() => Decide(CrashReportDecision.Kept);

    /// <summary>Delete (declining): removes the report's folder. False when there is nothing to decide.</summary>
    public bool Delete() => Decide(CrashReportDecision.Deleted);

    /// <summary>The dialog closed without a button (Esc): kept, and not offered again. Does nothing after a choice.</summary>
    public void CloseWithoutChoice() => Decide(CrashReportDecision.Kept);

    private string When => Report?.Info is { } info
        ? " on " + info.TimeUtc.ToLocalTime().ToString("d MMMM yyyy 'at' HH:mm", CultureInfo.CurrentCulture)
        : string.Empty;

    private bool Decide(CrashReportDecision decision)
    {
        if (Report is not { } report || Decision is not null)
        {
            return false;
        }

        Decision = decision;
        try
        {
            if (decision == CrashReportDecision.Kept)
            {
                CrashReportStore.Keep(report);
                Serilog.Log.Information("Crash report {Id}: kept at {Folder}", report.Id, report.Folder);
            }
            else
            {
                bool gone = CrashReportStore.Delete(report);
                Serilog.Log.Information("Crash report {Id}: deleted (folder gone {Gone})", report.Id, gone);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Error(e, "Crash report {Id}: {Decision} could not be carried out", report.Id, decision);
        }

        return true;
    }
}
