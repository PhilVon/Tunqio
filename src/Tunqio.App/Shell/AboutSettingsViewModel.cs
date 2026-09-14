using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.App.Playback;
using Tunqio.Core;
using Tunqio.Core.Audio;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Shell;

/// <summary>One row of the About page's licence list.</summary>
/// <param name="Name">The component.</param>
/// <param name="Version">Its version, or empty.</param>
/// <param name="Licence">The licence, as the notices name it.</param>
/// <param name="TextPath">The licence text on disk, or null when none is shipped for it.</param>
/// <param name="Note">What to show instead of a text when there is none.</param>
public sealed record LicenceRow(string Name, string Version, string Licence, string? TextPath, string? Note)
{
    /// <summary>"bass 2.4.18 · Free for non-commercial use (Un4seen Developments Ltd.)": the row, and its automation name.</summary>
    public string Display => Version.Length == 0 ? Name + " · " + Licence : Name + " " + Version + " · " + Licence;

    public override string ToString() => Display;
}

/// <summary>
/// Settings › About &amp; Diagnostics (E6-S5, docs/ui-screens-and-flows.md): the version, the licences, the logs
/// folder, the diagnostics export, the crash-reporting opt-in and a performance readout. What the page shows is
/// here; the page holds the picker and the folder launch, which cannot run in a test host.
/// </summary>
/// <remarks>
/// <para>
/// The licences come from the shipped <c>licenses/THIRD-PARTY-NOTICES.md</c> through <see cref="ThirdPartyNotices"/>,
/// joined to the texts in <c>licenses/</c>, so the page and the package cannot disagree about what is shipped
/// (T-128 checks the package carries both). The BASS sentence is <see cref="ThirdPartyAttribution.Bass"/>, which
/// the BASS licence requires and <c>Tunqio.Core.Tests</c> holds the notices file to.
/// </para>
/// <para>
/// The readout refreshes at the overlay's rate through <see cref="DiagnosticsReaders"/>, the same guarded reads
/// the Ctrl+Shift+D overlay uses, and only while <see cref="IsReadoutActive"/>: the page sets it when it is on
/// screen and clears it when it leaves or the overlay closes, so a hidden page costs no native calls.
/// </para>
/// </remarks>
public sealed partial class AboutSettingsViewModel : ObservableObject, IDisposable
{
    /// <summary>How often the readout is re-read while the page is on screen; the overlay's rate, for the same reason.</summary>
    public static readonly TimeSpan RefreshInterval = DiagnosticsViewModel.RefreshInterval;

    private const string Surface = "About page";

    private readonly AboutEnvironment _environment;
    private readonly IPlaybackSessionSource _source;
    private readonly ISettingsStore _settings;
    private readonly IAppPaths _paths;
    private readonly ILibraryFolderRepository _folders;
    private readonly Func<RenderStats?> _renderer;
    private readonly Func<long> _workingSet;
    private readonly string? _userProfile;
    private readonly SynchronizationContext? _ui;
    private readonly TimeProvider _time;
    private readonly Crash.CrashReportStore _crashReports;
    private ITimer? _timer;
    private bool _seeding;
    private bool _disposed;

    /// <summary>The licence rows, once <see cref="LoadLicences"/> has run.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LicenceRow> Licences { get; set; } = [];

    /// <summary>The row whose text is showing.</summary>
    [ObservableProperty]
    public partial LicenceRow? SelectedLicence { get; set; }

    /// <summary>The selected row's licence text, or its note when no text is shipped.</summary>
    [ObservableProperty]
    public partial string LicenceText { get; set; } = string.Empty;

    /// <summary><c>diagnostics.crashReporting</c>: whether a crash saves a report on this PC (E8-S5, <see cref="Crash.CrashReporter"/>).</summary>
    [ObservableProperty]
    public partial bool CrashReporting { get; set; }

    /// <summary>Whether the export replaces the profile and library folder paths. On by default: a support zip should not carry a user name unasked.</summary>
    [ObservableProperty]
    public partial bool RedactPaths { get; set; } = true;

    /// <summary>Whether the readout timer runs. The page sets this from its own visibility.</summary>
    [ObservableProperty]
    public partial bool IsReadoutActive { get; set; }

    [ObservableProperty]
    public partial string Dropouts { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FrameTime { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Memory { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNotice { get; set; }

    [ObservableProperty]
    public partial bool NoticeIsError { get; set; }

    /// <param name="environment">The build: versions and where the executable is.</param>
    /// <param name="source">The session, for the output the engine opened and the readout's engine statistics.</param>
    /// <param name="settings">Where <c>diagnostics.crashReporting</c> and <c>output.*</c> are read and written.</param>
    /// <param name="paths">The logs directory and the settings file the export copies.</param>
    /// <param name="folders">The library folders, whose paths the export can redact.</param>
    /// <param name="renderer">Reads the renderer's statistics; null when there is no renderer.</param>
    /// <param name="crashReports">
    /// The crash folder (E8-S5), whose kept reports the export includes. Required, not optional: an export that silently
    /// left kept reports out would be the T-156 shape CompositionRootTests guards against.
    /// </param>
    /// <param name="ui">The XAML thread's context; null runs the refresh inline, which is what the tests want.</param>
    /// <param name="clock">Drives the readout; a fake clock is how a test advances it.</param>
    /// <param name="workingSet">The process's working set in bytes; the real one by default.</param>
    /// <param name="userProfile">The profile directory the export redacts; the real one by default, empty for none.</param>
    public AboutSettingsViewModel(
        AboutEnvironment environment,
        IPlaybackSessionSource source,
        ISettingsStore settings,
        IAppPaths paths,
        ILibraryFolderRepository folders,
        Func<RenderStats?> renderer,
        Crash.CrashReportStore crashReports,
        SynchronizationContext? ui = null,
        TimeProvider? clock = null,
        Func<long>? workingSet = null,
        string? userProfile = null)
    {
        ArgumentNullException.ThrowIfNull(crashReports);
        _crashReports = crashReports;
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(renderer);
        _environment = environment;
        _source = source;
        _settings = settings;
        _paths = paths;
        _folders = folders;
        _renderer = renderer;
        _workingSet = workingSet ?? (() => Environment.WorkingSet);
        _userProfile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _ui = ui;
        _time = clock ?? TimeProvider.System;
        _seeding = true;
        try
        {
            CrashReporting = settings.GetValue(SettingsKeys.DiagnosticsCrashReporting, SettingsKeys.Defaults.DiagnosticsCrashReporting);
        }
        finally
        {
            _seeding = false;
        }

        RefreshReadout();
    }

    /// <summary>"Tunqio 0.1.0".</summary>
    public string VersionLine => _environment.ProductName + " " + _environment.AppVersion;

    /// <summary>"mpcore 0.1.0 · ABI 1.0".</summary>
    public string CoreLine => "mpcore " + _environment.CoreVersion + " · ABI " + _environment.AbiVersion;

    /// <summary>The OS, as the runtime describes it.</summary>
    public string OsLine => _environment.OsVersion;

    /// <summary>The sentence the BASS licence requires.</summary>
    public static string BassAttribution => ThirdPartyAttribution.Bass;

    /// <summary>Where the logs are; Open logs folder opens it.</summary>
    public string LogsDirectory => _paths.LogsDirectory;

    /// <summary>Where the licence texts are; Open licences folder opens it.</summary>
    public string LicencesDirectory => Path.Combine(_environment.BaseDirectory, ThirdPartyAttribution.LicensesFolderName);

    /// <summary>The name the save picker suggests.</summary>
    public string SuggestedExportName => DiagnosticsExport.SuggestedFileName(_time.GetLocalNow());

    /// <summary>The switch's header and automation name.</summary>
    public const string CrashReportingHeader = "Save crash reports on this PC";

    /// <summary>The text under the crash-reporting switch (E8-S5): exactly what is captured, where it goes, and that it stays here.</summary>
    public static string CrashReportingHint =>
        "Off (the default): nothing is captured beyond what Windows itself records. On: if Tunqio crashes, it saves a report in the crashes folder of its data folder: a crash dump (a copy of part of Tunqio's memory, which can contain file paths and track names) and the last 200 lines of its log. The next time Tunqio starts, it shows you what was saved and lets you keep or delete it. Nothing leaves this PC unless you send it yourself, for example in a diagnostics export.";

    // ---- licences ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads the shipped notices and the <c>licenses/</c> folder into <see cref="Licences"/>: the notices file
    /// itself first, then every shipped component the notices list, in file order (the BASS packages, the NuGet
    /// packages and the runtime, the vendored sources compiled into mpcore.dll), with its text where one is shipped,
    /// then any text in the folder the notices do not mention. A row the notices mark as not shipped (Catch2,
    /// test-only) is left out (T-198).
    /// </summary>
    public void LoadLicences()
    {
        var rows = new List<LicenceRow>();
        string noticesPath = Path.Combine(LicencesDirectory, ThirdPartyNotices.FileName);
        IReadOnlyList<ThirdPartyComponent> components = [];
        if (File.Exists(noticesPath))
        {
            rows.Add(new LicenceRow("Third-party notices", string.Empty, "every component and its licence", noticesPath, null));
            try
            {
                components = ThirdPartyNotices.Parse(File.ReadAllText(noticesPath));
            }
            catch (IOException e)
            {
                Serilog.Log.Warning(e, "The third-party notices at {Path} could not be read", noticesPath);
            }
        }
        else
        {
            rows.Add(new LicenceRow("Third-party notices", string.Empty, "not shipped with this build", null, "THIRD-PARTY-NOTICES.md is not next to the executable. The build places it under licenses/ (T-128); a package without it is not licensed to be distributed."));
        }

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ThirdPartyComponent component in components)
        {
            if (!component.Shipped)
            {
                continue; // kept in the notices for completeness, not in the package
            }

            string? text = null;
            if (component.LicenceFile is { } file)
            {
                string candidate = Path.GetFullPath(Path.Combine(_environment.BaseDirectory, file));
                referenced.Add(candidate);
                text = File.Exists(candidate) ? candidate : null;
            }

            rows.Add(new LicenceRow(
                component.Name,
                component.Version,
                component.Licence,
                text,
                text is null ? MissingTextNote(component) : null));
        }

        if (Directory.Exists(LicencesDirectory))
        {
            foreach (string extra in Directory.EnumerateFiles(LicencesDirectory, "*.txt").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (!referenced.Contains(Path.GetFullPath(extra)))
                {
                    rows.Add(new LicenceRow(Path.GetFileNameWithoutExtension(extra), string.Empty, "see the text", extra, null));
                }
            }
        }

        Licences = rows;
        SelectedLicence = rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>What the text box says for a component whose licence text is not beside the executable.</summary>
    private static string MissingTextNote(ThirdPartyComponent component)
    {
        if (component.LicenceFile is { } file)
        {
            return "The licence text " + file + " is missing from this build.";
        }

        return component.Section.StartsWith("Vendored", StringComparison.OrdinalIgnoreCase)
            ? "Compiled into mpcore.dll from source. The licence text is in the repository beside the vendored source."
            : "Licensed under " + component.Licence + ", as the third-party notices record. Its licence text is collected by the release pipeline (E8-S1) and is not shipped beside the executable yet.";
    }

    partial void OnSelectedLicenceChanged(LicenceRow? value)
    {
        if (value is null)
        {
            LicenceText = string.Empty;
            return;
        }

        if (value.TextPath is null)
        {
            LicenceText = value.Note ?? string.Empty;
            return;
        }

        try
        {
            LicenceText = File.ReadAllText(value.TextPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LicenceText = "The licence text at " + value.TextPath + " could not be read: " + e.Message;
        }
    }

    // ---- crash reporting ----------------------------------------------------------------------------------------------

    partial void OnCrashReportingChanged(bool value)
    {
        if (_seeding)
        {
            return;
        }

        _settings.SetValue(SettingsKeys.DiagnosticsCrashReporting, value);
        _settings.FlushAsync().Forget("Save crash reporting");
    }

    // ---- export -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The text of <c>system-info.txt</c>: the build, the OS, the GPU the renderer reports, and the output the
    /// engine opened. Every line is a label and a value, so it reads in a text editor.
    /// </summary>
    public string SystemInfo()
    {
        PlaybackSession? session = _source.Session;
        EngineStats? engine = DiagnosticsReaders.EngineStats(session, Surface);
        RenderStats? renderer = DiagnosticsReaders.Renderer(_renderer, Surface);
        OutputPreference preference = OutputPolicy.Read(_settings);
        string device = preference.DeviceId is null
            ? "system default"
            : preference.DeviceId + " (" + (session?.OutputDevices().FirstOrDefault(d => string.Equals(d.Id, preference.DeviceId, StringComparison.OrdinalIgnoreCase))?.Name ?? "not connected") + ")";

        var text = new StringBuilder();
        text.Append("Product: ").AppendLine(_environment.ProductName);
        text.Append("App version: ").AppendLine(_environment.AppVersion);
        text.Append("Core version: ").AppendLine(_environment.CoreVersion);
        text.Append("ABI version: ").AppendLine(_environment.AbiVersion);
        text.Append("OS: ").AppendLine(_environment.OsVersion);
        text.Append("Exported: ").AppendLine(_time.GetUtcNow().ToString("u", CultureInfo.InvariantCulture));
        text.Append("GPU adapter: ").AppendLine(renderer is null ? "unknown (the renderer is not running)" : renderer.Adapter + (renderer.Warp ? " (WARP)" : string.Empty));
        text.Append("Output device: ").AppendLine(session is null ? "no audio engine" : device);
        text.Append("Output format: ").AppendLine(engine is null
            ? "unknown"
            : Inv($"{engine.OutputSampleRate} Hz · {engine.OutputChannels} ch · {engine.OutputFormat} · {(engine.Exclusive ? "exclusive" : "shared")} · {engine.OutputBuffer.TotalMilliseconds:F0} ms buffer · started {(engine.OutputStarted ? "yes" : "no")}"));
        text.Append("Data root: ").AppendLine(_paths.DataRoot);
        text.Append("Process memory: ").AppendLine(Inv($"{_workingSet() / (1024.0 * 1024.0):F0} MB working set"));
        text.Append("Paths redacted: ").AppendLine(RedactPaths ? "yes" : "no");
        return text.ToString();
    }

    /// <summary>
    /// Writes the zip to <paramref name="zipPath"/> and says so in the notice. The library folders are read first
    /// because the redaction needs their paths; the write itself is the exporter's.
    /// </summary>
    public async Task<DiagnosticsExportResult?> ExportAsync(string zipPath, CancellationToken ct = default)
    {
        try
        {
            IReadOnlyList<PathPlaceholder> redact = [];
            if (RedactPaths)
            {
                IReadOnlyList<LibraryFolderDto> folders = await _folders.ListAsync(ct);
                redact = DiagnosticsExport.Placeholders(_userProfile, [.. folders.Select(f => f.Path)]);
            }

            IReadOnlyList<string> kept = [.. _crashReports.KeptReports().Select(r => r.Folder)];
            var request = new DiagnosticsExportRequest(_paths.LogsDirectory, _paths.SettingsPath, SystemInfo(), redact, kept);
            DiagnosticsExportResult result = await DiagnosticsExport.WriteAsync(zipPath, request, ct);
            Serilog.Log.Information("Diagnostics exported to {Path}: {Entries} entries, {Redactions} path redactions", result.ZipPath, result.Entries.Count, result.Redactions);
            SetNotice(Inv($"Diagnostics written to {result.ZipPath} ({result.Entries.Count} files{(RedactPaths ? Inv($", {result.Redactions} paths redacted") : string.Empty)})."), error: false);
            return result;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Error(e, "Diagnostics could not be exported to {Path}", zipPath);
            SetNotice("The diagnostics zip could not be written: " + e.Message, error: true);
            return null;
        }
    }

    /// <summary>Clears the notice; the page's InfoBar calls it when closed.</summary>
    public void ClearNotice()
    {
        Notice = string.Empty;
        HasNotice = false;
    }

    /// <summary>Puts a message in the notice bar; the page's folder launches use it when a folder will not open.</summary>
    public void SetNotice(string text, bool error)
    {
        HasNotice = false;
        NoticeIsError = error;
        Notice = text;
        HasNotice = text.Length > 0;
    }

    // ---- readout ------------------------------------------------------------------------------------------------------

    /// <summary>Re-reads the three numbers. Called by the timer, and once at construction so the page is never blank.</summary>
    public void RefreshReadout()
    {
        PlaybackSession? session = _source.Session;
        PerformanceReadout readout = Diagnostics.Performance(
            DiagnosticsReaders.EngineStats(session, Surface),
            DiagnosticsReaders.Renderer(_renderer, Surface),
            _workingSet());
        Dropouts = readout.Dropouts;
        FrameTime = readout.FrameTime;
        Memory = readout.Memory;
    }

    partial void OnIsReadoutActiveChanged(bool value)
    {
        _timer?.Dispose();
        _timer = null;
        if (!value || _disposed)
        {
            return;
        }

        RefreshReadout();
        _timer = _time.CreateTimer(_ => Post(RefreshReadout), null, RefreshInterval, RefreshInterval);
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
            return;
        }

        // No JoinableTaskFactory in this app; the context is the XAML thread's DispatcherQueue one and Post never blocks the caller.
#pragma warning disable VSTHRD001
        _ui.Post(_ => action(), null);
#pragma warning restore VSTHRD001
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer?.Dispose();
        _timer = null;
    }

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
