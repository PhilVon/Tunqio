using System.IO.Compression;
using System.Text;

namespace Tunqio.App.Shell;

/// <summary>A path the export replaces, and the placeholder that stands in for it.</summary>
public sealed record PathPlaceholder(string Path, string Placeholder);

/// <summary>What goes into a diagnostics zip (E6-S5).</summary>
/// <param name="LogsDirectory">Where the <c>tunqio-yyyyMMdd.log</c> files are; a missing directory contributes no logs.</param>
/// <param name="SettingsPath"><c>settings.json</c>; null or a missing file contributes no settings entry.</param>
/// <param name="SystemInfo">The text of <c>system-info.txt</c>, already composed by the caller.</param>
/// <param name="Redact">The paths to replace in the copied logs, settings and system info; empty leaves them as they are.</param>
/// <param name="CrashReportFolders">
/// The crash reports the user chose to keep (E8-S5), each copied under <c>crashes/&lt;report&gt;/</c>. Their text files are
/// redacted like the logs; a minidump is binary and goes in as it is.
/// </param>
public sealed record DiagnosticsExportRequest(
    string LogsDirectory,
    string? SettingsPath,
    string SystemInfo,
    IReadOnlyList<PathPlaceholder> Redact,
    IReadOnlyList<string>? CrashReportFolders = null);

/// <summary>What was written: the zip, its entries in order, and how many path occurrences were replaced.</summary>
public sealed record DiagnosticsExportResult(string ZipPath, IReadOnlyList<string> Entries, int Redactions);

/// <summary>
/// Writes the diagnostics zip the About page exports (E6-S5, docs/ui-screens-and-flows.md "About &amp; Diagnostics"):
/// every log file, <c>settings.json</c> and a <c>system-info.txt</c>, with the user's paths optionally replaced by
/// placeholders. A class of its own rather than code in the page, so a test can write one into a scratch
/// directory and open it, and so the <c>--export-diagnostics</c> switch can write one with no picker on screen.
/// </summary>
/// <remarks>
/// <para>
/// Redaction is textual, over the copies only: the originals are never touched. A path is replaced in the form it
/// appears on disk and in the form <c>settings.json</c> stores it, with every backslash doubled, because a
/// redaction that missed the JSON-escaped spelling would leave the profile path in the one file that always
/// carries it. Longer paths are replaced first, so a library folder under the profile becomes its own placeholder
/// rather than "%USERPROFILE%\Music".
/// </para>
/// <para>
/// The day's log is open for writing by this process, with sharing, so it is read with
/// <see cref="FileShare.ReadWrite"/>; what has reached the disk is what goes in, and the file sink's buffered tail
/// is not.
/// </para>
/// </remarks>
public static class DiagnosticsExport
{
    /// <summary>The entry name of the system information text.</summary>
    public const string SystemInfoEntry = "system-info.txt";

    /// <summary>The entry name of the copied settings.</summary>
    public const string SettingsEntry = "settings.json";

    /// <summary>The folder inside the zip that holds the copied logs.</summary>
    public const string LogsFolder = "logs/";

    /// <summary>The folder inside the zip that holds the kept crash reports (E8-S5).</summary>
    public const string CrashesFolder = "crashes/";

    /// <summary>What stands in for the user profile directory when paths are redacted.</summary>
    public const string ProfilePlaceholder = "[user profile]";

    /// <summary>The name the picker suggests, dated so two exports do not overwrite each other.</summary>
    public static string SuggestedFileName(DateTimeOffset now) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"tunqio-diagnostics-{now:yyyyMMdd-HHmm}.zip");

    /// <summary>
    /// The placeholders for a profile directory and the library folders: each folder as
    /// <c>[library folder N]</c>, then the profile, longest path first so the more specific one wins.
    /// </summary>
    public static IReadOnlyList<PathPlaceholder> Placeholders(string? userProfile, IReadOnlyList<string> libraryFolders)
    {
        ArgumentNullException.ThrowIfNull(libraryFolders);
        var placeholders = new List<PathPlaceholder>();
        for (int i = 0; i < libraryFolders.Count; i++)
        {
            string folder = libraryFolders[i].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (folder.Length > 0)
            {
                placeholders.Add(new PathPlaceholder(folder, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"[library folder {i + 1}]")));
            }
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            placeholders.Add(new PathPlaceholder(userProfile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), ProfilePlaceholder));
        }

        return [.. placeholders.OrderByDescending(p => p.Path.Length)];
    }

    /// <summary>
    /// <paramref name="text"/> with every placeholder's path replaced, in both its plain and its JSON-escaped
    /// spelling, case-insensitively (Windows paths are). <paramref name="count"/> is how many occurrences went.
    /// </summary>
    public static string Redact(string text, IReadOnlyList<PathPlaceholder> placeholders, out int count)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(placeholders);
        count = 0;
        foreach (PathPlaceholder placeholder in placeholders)
        {
            if (placeholder.Path.Length == 0)
            {
                continue;
            }

            string escaped = placeholder.Path.Replace("\\", "\\\\", StringComparison.Ordinal);
            if (!string.Equals(escaped, placeholder.Path, StringComparison.Ordinal))
            {
                text = Replace(text, escaped, placeholder.Placeholder, ref count);
            }

            text = Replace(text, placeholder.Path, placeholder.Placeholder, ref count);
        }

        return text;
    }

    /// <summary>Writes the zip at <paramref name="zipPath"/>, replacing one already there.</summary>
    public static DiagnosticsExportResult Write(string zipPath, DiagnosticsExportRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(request);
        var entries = new List<string>();
        int redactions = 0;
        string? directory = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Written beside the target and moved over it, so a reader never sees a half-written zip at the picked path.
        string temporary = zipPath + ".partial";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                Add(zip, SystemInfoEntry, request.SystemInfo, request.Redact, entries, ref redactions);
                if (request.SettingsPath is { Length: > 0 } settings && File.Exists(settings))
                {
                    Add(zip, SettingsEntry, ReadShared(settings), request.Redact, entries, ref redactions);
                }

                foreach (string log in LogFiles(request.LogsDirectory))
                {
                    Add(zip, LogsFolder + Path.GetFileName(log), ReadShared(log), request.Redact, entries, ref redactions);
                }

                foreach (string report in request.CrashReportFolders ?? [])
                {
                    AddCrashReport(zip, report, request.Redact, entries, ref redactions);
                }
            }

            File.Move(temporary, zipPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return new DiagnosticsExportResult(zipPath, entries, redactions);
    }

    /// <summary>
    /// <see cref="Write"/> on the thread pool: the zip is a handful of files, but a week of logs is megabytes, and
    /// the caller is the XAML thread. The <c>Task.Run</c> is here rather than in the view model so the convention
    /// (docs/solution-structure.md: no <c>Task.Run</c> in view models) holds where it is meant to.
    /// </summary>
    public static Task<DiagnosticsExportResult> WriteAsync(string zipPath, DiagnosticsExportRequest request, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Write(zipPath, request), ct);
    }

    /// <summary>The log files in <paramref name="logsDirectory"/>, oldest first by name; none for a directory that is not there.</summary>
    public static IReadOnlyList<string> LogFiles(string logsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        if (!Directory.Exists(logsDirectory))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(logsDirectory, "*.log").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>One kept crash report's files under <c>crashes/&lt;report&gt;/</c>; the kept marker is bookkeeping and stays out.</summary>
    private static void AddCrashReport(ZipArchive zip, string folder, IReadOnlyList<PathPlaceholder> redact, List<string> entries, ref int redactions)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        string prefix = CrashesFolder + Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "/";
        foreach (string file in Directory.EnumerateFiles(folder).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(file);
            if (string.Equals(name, Crash.CrashReportStore.KeptMarkerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(Path.GetExtension(name), ".dmp", StringComparison.OrdinalIgnoreCase))
            {
                zip.CreateEntryFromFile(file, prefix + name, CompressionLevel.Optimal);
                entries.Add(prefix + name);
            }
            else
            {
                Add(zip, prefix + name, ReadShared(file), redact, entries, ref redactions);
            }
        }
    }

    private static void Add(ZipArchive zip, string name, string content, IReadOnlyList<PathPlaceholder> redact, List<string> entries, ref int redactions)
    {
        int count = 0;
        string text = redact.Count == 0 ? content : Redact(content, redact, out count);
        redactions += count;
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(text);
        entries.Add(name);
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Replaces whole-path occurrences only: a match has to end at a separator, a quote, whitespace or the end of
    /// the text, so <c>C:\Users\phil</c> does not eat the front of <c>C:\Users\philw\...</c> (the test over a
    /// scratch directory under the real profile found exactly that).
    /// </summary>
    private static string Replace(string text, string oldValue, string newValue, ref int count)
    {
        var result = new StringBuilder(text.Length);
        int from = 0;
        int at = text.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            int end = at + oldValue.Length;
            if (end >= text.Length || (!char.IsLetterOrDigit(text[end]) && text[end] != '_' && text[end] != '-'))
            {
                result.Append(text, from, at - from).Append(newValue);
                from = end;
                count++;
            }

            at = text.IndexOf(oldValue, end, StringComparison.OrdinalIgnoreCase);
        }

        return from == 0 ? text : result.Append(text, from, text.Length - from).ToString();
    }
}
