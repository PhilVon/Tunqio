using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Tunqio.App.Shell;

/// <summary>
/// What a theme switch did (AC-69). <paramref name="ContentUnchanged"/> is the "no white flash" half stated as
/// something checkable: the shell's content is the same object it was before the switch, so what happened was a
/// repaint of brushes already in the tree and not a reload with a frame of nothing in between.
/// </summary>
internal sealed record ThemeSwitch(
    string From,
    string Requested,
    string To,
    bool ContentUnchanged,
    bool AppliedSynchronously,
    string BackdropBefore,
    bool Pass,
    string Note);

/// <summary>
/// What the three panels actually measured at one client width, after the layout pass settled. The fractions are
/// Now Playing's and the sidebar's shares of the width they divide; the controls panel is a bar, not a share (T-182).
/// </summary>
internal sealed record ShellMeasurement(
    int ClientWidth,
    string Mode,
    bool Stacked,
    double NowPlayingWidth,
    double SidebarWidth,
    double ControlsWidth,
    double NowPlayingFraction,
    double SidebarFraction,
    string Theme,
    bool Pass,
    string Note);

/// <summary>
/// E2-S1 measurement mode: <c>Tunqio.exe --shell-spike [--out FILE]</c>. Sizes the window through the documented
/// widths, reads what the three panels came out at, and writes the table as JSON.
/// </summary>
/// <remarks>
/// <see cref="ShellLayoutTests"/> proves the table; this proves the table reached the visual tree. They are
/// different claims, and the gap between them is the whole class of bug where a policy is right and nothing is
/// bound to it. At 1600 px the shares must be 75 / 25 with the controls bar spanning Now Playing; at 700 px the
/// panels must have stacked, which shows as all three spanning the full client width instead of dividing it.
/// </remarks>
internal sealed class ShellSpikeRunner
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    /// <summary>Widths the run measures: either side of both breakpoints, and the two the criterion names.</summary>
    private static readonly int[] Widths = [700, 900, 1100, 1300, 1600];

    /// <summary>How far a measured share may sit from the documented one; a hair for the border strokes.</summary>
    private const double Tolerance = 0.02;

    private readonly MainWindow _window;
    private readonly ILogger _logger;
    private readonly string _outputPath;
    private readonly List<ShellMeasurement> _measurements = [];
    private readonly List<ThemeSwitch> _themeSwitches = [];
    private int _index;

    public ShellSpikeRunner(MainWindow window, ILogger logger, string[] args, string defaultOutput)
    {
        _window = window;
        _logger = logger;
        _outputPath = StringArg(args, "--out") ?? defaultOutput;
    }

    public static bool IsRequested(string[] args) => args.Contains("--shell-spike", StringComparer.Ordinal);

    public void Start()
    {
        _logger.LogInformation("shell spike: measuring the panels at {Widths}", string.Join(", ", Widths));
        Next();
    }

    /// <summary>
    /// One width per tick rather than one loop: a resize is only real after XAML has run a layout pass, and the
    /// pass happens between ticks of the dispatcher, not inside a method that asked for it.
    /// </summary>
    private void Next()
    {
        if (_index >= Widths.Length)
        {
            Finish();
            return;
        }

        int width = Widths[_index++];
        _window.AppWindow.ResizeClient(new SizeInt32(width, 900));
        DispatcherQueueTimer settle = _window.DispatcherQueue.CreateTimer();
        settle.Interval = TimeSpan.FromMilliseconds(400);
        settle.IsRepeating = false;
        settle.Tick += (_, _) =>
        {
            Measure(width);
            Next();
        };
        settle.Start();
    }

    private void Measure(int requested)
    {
        ShellMeasurement measurement = _window.MeasurePanels(requested);
        _measurements.Add(measurement);
        _logger.LogInformation(
            "shell spike at {Width} px: {Mode}, Now Playing {NowPlaying:F0} px, sidebar {Sidebar:F0} px, controls bar {Controls:F0} px ({Note})",
            measurement.ClientWidth, measurement.Mode, measurement.NowPlayingWidth, measurement.SidebarWidth,
            measurement.ControlsWidth, measurement.Note);
    }

    /// <summary>
    /// The theme half (AC-69). Every preference is applied and read back on the spot: a switch that had to wait for
    /// a frame, or that rebuilt the tree, would show here as one that did not take synchronously or that changed
    /// the content object.
    /// </summary>
    /// <remarks>
    /// Switching persists the choice, because that is what switching means for the Appearance page that will call
    /// it. A diagnostic run is not the user choosing, so whatever was in <c>ui.theme</c> before goes back after.
    /// </remarks>
    private void SwitchThemes()
    {
        ThemePreference original = _window.ThemePreference;
        foreach (ThemePreference preference in new[] { ThemePreference.Light, ThemePreference.Dark, ThemePreference.System })
        {
            _themeSwitches.Add(_window.SwitchThemeAndReport(preference));
        }

        _window.SetTheme(original);
    }

    private void Finish()
    {
        SwitchThemes();
        bool layoutPass = _measurements.Count == Widths.Length && _measurements.TrueForAll(m => m.Pass);
        bool themePass = _themeSwitches.Count == 3 && _themeSwitches.TrueForAll(t => t.Pass);
        bool pass = layoutPass && themePass;
        var report = new
        {
            Machine = Environment.MachineName,
            Verdict = pass
                ? "PASS: 75/25 at 1600 px with the controls bar under Now Playing, panels stacked at 700 px, and every theme switch applied in place without rebuilding the tree"
                : "FAIL: see the Note on each measurement and each theme switch",
            Tolerance,
            LayoutPass = layoutPass,
            ThemePass = themePass,
            Measurements = _measurements,
            ThemeSwitches = _themeSwitches,
            Pass = pass,
        };
        string json = JsonSerializer.Serialize(report, ReportOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        File.WriteAllText(_outputPath, json);
        foreach (ThemeSwitch t in _themeSwitches)
        {
            _logger.LogInformation("shell spike theme {Requested}: {From} -> {To} ({Note})", t.Requested, t.From, t.To, t.Note);
        }

        _logger.LogInformation("shell spike {Result}; report {Path}", pass ? "PASS" : "FAIL", _outputPath);
        Environment.ExitCode = pass ? 0 : 1;
        _window.Close();
    }

    /// <summary>
    /// Whether one measurement is what the width called for: in a column shape, the documented shares and a controls
    /// bar as wide as the Now Playing column above it; in a stacked one, three panels each spanning the client.
    /// </summary>
    internal static (bool Pass, string Note) Judge(ShellLayoutState expected, double clientWidth, double nowPlaying, double sidebar, double controls)
    {
        if (expected.Stacked)
        {
            bool spanned = Near(nowPlaying, clientWidth) && Near(sidebar, clientWidth) && Near(controls, clientWidth);
            return spanned
                ? (true, "stacked: all three panels span the client width")
                : (false, string.Create(
                    CultureInfo.InvariantCulture,
                    $"stacked, but the panels measured {nowPlaying:F0} / {sidebar:F0} / {controls:F0} px against a client of {clientWidth:F0}"));
        }

        double total = nowPlaying + sidebar;
        if (total <= 0)
        {
            return (false, "the panels measured nothing; the window had not laid out");
        }

        if (nowPlaying < ShellLayout.NowPlayingMinWidth - 1 || sidebar < ShellLayout.SidebarMinWidth - 1)
        {
            return (false, string.Create(
                CultureInfo.InvariantCulture,
                $"a panel is under its floor: {nowPlaying:F0} / {sidebar:F0} px against floors of " +
                $"{ShellLayout.NowPlayingMinWidth:F0} / {ShellLayout.SidebarMinWidth:F0}"));
        }

        // T-182: the controls are a bar under Now Playing. A bar narrower than the column above it has been put
        // somewhere else - back in a column of its own is the regression this exists to catch.
        if (!Near(controls, nowPlaying))
        {
            return (false, string.Create(
                CultureInfo.InvariantCulture,
                $"the controls panel measured {controls:F0} px under a Now Playing column of {nowPlaying:F0}; as a bar it should span it"));
        }

        double gotNowPlaying = nowPlaying / total;
        double gotSidebar = sidebar / total;
        if (ShellLayout.FloorsBind(clientWidth))
        {
            // The shares are not the documented ones here and are not meant to be: the floors won. What has to
            // hold is that they won without leaving a gap, and the floors themselves were checked above.
            return (true, string.Create(
                CultureInfo.InvariantCulture,
                $"floors bind at this width, so the shares are {gotNowPlaying:P0} / {gotSidebar:P0} rather than the documented ones; the controls bar spans Now Playing"));
        }

        (double wantNowPlaying, double wantSidebar) = expected.Fractions;
        bool ok = Math.Abs(gotNowPlaying - wantNowPlaying) <= Tolerance
            && Math.Abs(gotSidebar - wantSidebar) <= Tolerance;
        return (ok, string.Create(
            CultureInfo.InvariantCulture,
            $"wanted {wantNowPlaying:P0} / {wantSidebar:P0}, measured {gotNowPlaying:P0} / {gotSidebar:P0}; the controls bar spans Now Playing at {controls:F0} px"));
    }

    private static bool Near(double measured, double expected) => Math.Abs(measured - expected) <= 2;

    private static string? StringArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
