using System.Globalization;
using System.Text.Json;

namespace Tunqio.App.Crash;

/// <summary>What a crash report records about the crash, as <c>report.json</c> holds it.</summary>
/// <param name="Source">Which handler caught it: <c>XAML</c>, <c>AppDomain</c> or <c>Native</c>.</param>
/// <param name="ExceptionType">The managed exception's type, or "Native exception 0xC0000005 (access violation)".</param>
/// <param name="Message">The exception's message, or where a native exception happened.</param>
/// <param name="StackTrace">The managed stack, when there is one.</param>
/// <param name="ExceptionCode">The native exception code, when it was one.</param>
/// <param name="TimeUtc">When it was captured.</param>
/// <param name="SessionId">The session that crashed, as the log names it.</param>
/// <param name="AppVersion">The build that crashed.</param>
/// <param name="DumpBytes">The minidump's size; 0 when it could not be written.</param>
/// <param name="DumpProblem">Why there is no dump, when there is none.</param>
public sealed record CrashReportInfo(
    string Source,
    string ExceptionType,
    string Message,
    string? StackTrace,
    uint? ExceptionCode,
    DateTimeOffset TimeUtc,
    string SessionId,
    string AppVersion,
    long DumpBytes,
    string? DumpProblem);

/// <summary>A crash report on disk.</summary>
/// <param name="Id">The folder's name, which sorts by time.</param>
/// <param name="Folder">The report's folder under <c>crashes</c>.</param>
/// <param name="Info">What <c>report.json</c> says; null when the crashing process did not get as far as writing it.</param>
/// <param name="LogLines">The log lines captured with it.</param>
/// <param name="DumpPath">Where the minidump is (or would be).</param>
/// <param name="DumpBytes">The minidump's size on disk; 0 when there is none.</param>
/// <param name="Kept">Whether the user chose to keep it, which also means it is not offered again.</param>
public sealed record CrashReport(
    string Id,
    string Folder,
    CrashReportInfo? Info,
    IReadOnlyList<string> LogLines,
    string DumpPath,
    long DumpBytes,
    bool Kept);

/// <summary>
/// The crash folder under the data root (E8-S5): one folder per report, holding <c>tunqio.dmp</c>, <c>log.txt</c> (the last
/// 200 log lines) and <c>report.json</c>, plus a <c>kept</c> marker once the user has chosen Keep. A report without the
/// marker is pending: the next launch offers it once, and the user's answer either marks it or deletes the folder.
/// </summary>
/// <remarks>
/// A crashing process writes the dump first and <c>report.json</c> last, so a folder with a dump and no
/// <c>report.json</c> is a crash that ended the process mid-report. It is still listed, with no <see cref="CrashReport.Info"/>,
/// because the user should still see it and be able to delete it.
/// </remarks>
public sealed class CrashReportStore
{
    /// <summary>The crash folder's name under the data root.</summary>
    public const string FolderName = "crashes";

    public const string DumpFileName = "tunqio.dmp";
    public const string LogFileName = "log.txt";
    public const string InfoFileName = "report.json";
    public const string KeptMarkerName = "kept";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <param name="dataRoot">The data root; reports go in its <see cref="FolderName"/> folder.</param>
    public CrashReportStore(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Root = Path.Combine(dataRoot, FolderName);
    }

    /// <summary>The crash folder.</summary>
    public string Root { get; }

    /// <summary>Creates and returns a new report's folder, named by <paramref name="now"/> and the process id.</summary>
    public string CreateReportFolder(DateTimeOffset now, int processId)
    {
        string name = string.Create(CultureInfo.InvariantCulture, $"{now.UtcDateTime:yyyyMMdd-HHmmss-fff}-{processId}");
        string folder = Path.Combine(Root, name);
        for (int i = 2; Directory.Exists(folder); i++)
        {
            folder = Path.Combine(Root, string.Create(CultureInfo.InvariantCulture, $"{name}-{i}"));
        }

        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Writes <c>log.txt</c>.</summary>
    public static void WriteLog(string folder, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        File.WriteAllLines(Path.Combine(folder, LogFileName), lines);
    }

    /// <summary>Writes <c>report.json</c>.</summary>
    public static void WriteInfo(string folder, CrashReportInfo info) =>
        File.WriteAllText(Path.Combine(folder, InfoFileName), JsonSerializer.Serialize(info, Json));

    /// <summary>Every report, newest first.</summary>
    public IReadOnlyList<CrashReport> List()
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        var reports = new List<CrashReport>();
        foreach (string folder in Directory.EnumerateDirectories(Root).OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal))
        {
            if (Load(folder) is { } report)
            {
                reports.Add(report);
            }
        }

        return reports;
    }

    /// <summary>The reports nobody has answered yet, newest first.</summary>
    public IReadOnlyList<CrashReport> Pending() => [.. List().Where(r => !r.Kept)];

    /// <summary>The reports the user chose to keep, newest first: what a diagnostics export carries.</summary>
    public IReadOnlyList<CrashReport> KeptReports() => [.. List().Where(r => r.Kept)];

    /// <summary>Marks <paramref name="report"/> as kept, so it is exported and never offered again.</summary>
    public static void Keep(CrashReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        File.WriteAllText(Path.Combine(report.Folder, KeptMarkerName), string.Empty);
    }

    /// <summary>Deletes <paramref name="report"/>'s folder; true when it is gone afterwards.</summary>
    public static bool Delete(CrashReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (Directory.Exists(report.Folder))
        {
            Directory.Delete(report.Folder, recursive: true);
        }

        return !Directory.Exists(report.Folder);
    }

    /// <summary>The report in <paramref name="folder"/>, or null when the folder holds none of a report's files.</summary>
    public static CrashReport? Load(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        string dump = Path.Combine(folder, DumpFileName);
        string log = Path.Combine(folder, LogFileName);
        string info = Path.Combine(folder, InfoFileName);
        if (!File.Exists(dump) && !File.Exists(log) && !File.Exists(info))
        {
            return null;
        }

        CrashReportInfo? parsed = null;
        if (File.Exists(info))
        {
            try
            {
                parsed = JsonSerializer.Deserialize<CrashReportInfo>(File.ReadAllText(info), Json);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Warning(e, "Crash report {Folder}: report.json could not be read", folder);
            }
        }

        IReadOnlyList<string> lines = [];
        if (File.Exists(log))
        {
            try
            {
                lines = File.ReadAllLines(log);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Warning(e, "Crash report {Folder}: log.txt could not be read", folder);
            }
        }

        long bytes = File.Exists(dump) ? new FileInfo(dump).Length : 0;
        return new CrashReport(Path.GetFileName(folder), folder, parsed, lines, dump, bytes, File.Exists(Path.Combine(folder, KeptMarkerName)));
    }
}
