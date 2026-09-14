using System.IO.Compression;
using System.Xml.Linq;
using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Core.Audio;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Tests;

/// <summary>
/// E6-S5: Settings › About &amp; Diagnostics over a scratch directory standing in for the executable's folder and the
/// data root. The version comes from the assembly the props stamp; the licences from a notices file and a licenses/
/// folder written here; the export is opened and read back; the readout ticks only while the page says it is on screen.
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class AboutSettingsTests : IAsyncLifetime
{
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeSettings _settings = new();
    private readonly FakeFolderRepository _folders = new();
    private readonly StubSessionSource _source = new();
    private readonly ManualClock _clock = new();
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "tunqio-about-" + Guid.NewGuid().ToString("N"));
    private PlaybackSession _session = null!;
    private ScratchPaths _paths = null!;
    private RenderStats? _renderer;
    private long _workingSet = 123 * 1024 * 1024;

    private const string Notices = """
        # Third-party notices

        Audio playback uses the BASS audio library and its add-ons.

        | Package | Version | Source | Licence | Licence text |
        |---------|---------|--------|---------|--------------|
        | `bass` | 2.4.18 | https://www.un4seen.com/files/bass24.zip | Free for non-commercial use (Un4seen Developments Ltd.) | `licenses/bass.txt` |
        | `bassmix` | 2.4.13 | https://www.un4seen.com/files/bassmix24.zip | Free to use with BASS | `licenses/bassmix.txt` |

        ## NuGet packages and the runtime

        | Component | Version | Files in the package | Licence |
        |-----------|---------|----------------------|---------|
        | TagLibSharp | 2.3.0 | `TagLibSharp.dll` | LGPL-2.1-only |
        | Serilog, Serilog.Sinks.File | 4.4.0, 7.0.0 | `Serilog*.dll` | Apache-2.0 |

        ## Vendored native sources (`native/third_party/`)

        | Component | Version | Source | Licence | Ships | Files | SHA-256 |
        |-----------|---------|--------|---------|-------|-------|---------|
        | Catch2 | v3.16.0 | https://github.com/catchorg/Catch2 | Boost Software License 1.0 (`catch2/LICENSE.txt`) | No: test-only | `catch2/catch_amalgamated.hpp` | `abc` |
        | | | | | | `catch2/catch_amalgamated.cpp` | `def` |
        | pffft | commit `0aec` | https://bitbucket.org/jpommier/pffft | FFTPACK licence (BSD-style) | Yes, compiled into `mpcore.dll` | `pffft/pffft.h` | `123` |

        ## Not shipped

        Build-time and test-only packages are not in the package: BenchmarkDotNet, xUnit.
        """;

    public Task InitializeAsync()
    {
        _paths = new ScratchPaths(Path.Combine(_scratch, "data"));
        _paths.EnsureCreated();
        Directory.CreateDirectory(Path.Combine(_scratch, "app", "licenses"));
        File.WriteAllText(Path.Combine(_scratch, "app", "licenses", "THIRD-PARTY-NOTICES.md"), Notices);
        File.WriteAllText(Path.Combine(_scratch, "app", "licenses", "bass.txt"), "BASS 2.4 licence text");
        File.WriteAllText(Path.Combine(_scratch, "app", "licenses", "extra.txt"), "An extra text the notices do not mention");
        _session = new PlaybackSession(_engine, _tracks, new FakePlayHistory(), new FakeQueueStore(), _settings, autoPoll: false);
        _source.Session = _session;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
        if (Directory.Exists(_scratch))
        {
            Directory.Delete(_scratch, recursive: true);
        }
    }

    private static AboutEnvironment Environment(string baseDirectory) =>
        new("Tunqio", "0.1.0", "0.1.0", "1.0", "Windows 10 (X64)", baseDirectory);

    private AboutSettingsViewModel About(string? userProfile = @"C:\Users\phil", StubSessionSource? source = null) =>
        new(Environment(Path.Combine(_scratch, "app")), source ?? _source, _settings, _paths, _folders, () => _renderer, new Crash.CrashReportStore(_paths.DataRoot), null, _clock, () => _workingSet, userProfile ?? string.Empty);

    private static RenderStats Frames() => new(
        Frames: 1200, Resizes: 2, Fps: 144.0, FrameLast: TimeSpan.FromMilliseconds(6.9), FrameMax: TimeSpan.FromMilliseconds(25.2),
        FrameAverage: TimeSpan.FromMilliseconds(6.97), FrameHistogram: [1143, 4, 0, 1, 0, 0], DxgiPresentCount: 1200, DxgiMissedRefreshes: 3,
        Width: 854, Height: 720, Warp: false, Headless: false, DeviceLost: false, Visible: true, Adapter: "NVIDIA GeForce RTX 4080 SUPER");

    // ---- version ------------------------------------------------------------------------------------------------------

    /// <summary>AC-441: the version on the page is TunqioVersion from Directory.Build.props, which stamps the assembly.</summary>
    [Fact]
    public void The_app_version_is_the_one_in_Directory_Build_props()
    {
        string props = File.ReadAllText(RepoFile("Directory.Build.props"));
        string expected = XDocument.Parse(props).Descendants("TunqioVersion").Single().Value;

        AboutEnvironment.ReadAppVersion().Should().Be(expected);
        AboutEnvironment.Current().ProductName.Should().Be(Identity.ProductName);
    }

    [Fact]
    public void The_version_lines_name_the_product_the_core_and_the_ABI()
    {
        AboutSettingsViewModel vm = About();

        vm.VersionLine.Should().Be("Tunqio 0.1.0");
        vm.CoreLine.Should().Be("mpcore 0.1.0 · ABI 1.0");
        vm.OsLine.Should().Be("Windows 10 (X64)");
        AboutSettingsViewModel.BassAttribution.Should().Be(ThirdPartyAttribution.Bass);
    }

    // ---- licences -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// AC-442, T-198: every shipped row of every notices table is a licence row, in file order (native, NuGet and runtime,
    /// vendored), the shipped texts are joined to them, and each is openable. Catch2 is test-only and is not listed, the
    /// build-only tools in the Not shipped prose are not rows, and nothing is listed that the notices do not name.
    /// </summary>
    [Fact]
    public void Licences_are_listed_from_the_shipped_notices_and_the_licenses_folder()
    {
        AboutSettingsViewModel vm = About();

        vm.LoadLicences();

        vm.Licences.Select(l => l.Name).Should().Equal("Third-party notices", "bass", "bassmix", "TagLibSharp", "Serilog, Serilog.Sinks.File", "pffft", "extra");
        vm.Licences.Single(l => l.Name == "bass").Display.Should().Be("bass 2.4.18 · Free for non-commercial use (Un4seen Developments Ltd.)");
        vm.Licences.Single(l => l.Name == "bass").TextPath.Should().Be(Path.Combine(_scratch, "app", "licenses", "bass.txt"));
        vm.Licences.Single(l => l.Name == "bassmix").TextPath.Should().BeNull("bassmix.txt was not written to the scratch folder");
        vm.Licences.Single(l => l.Name == "bassmix").Note.Should().Contain("licenses/bassmix.txt is missing");
        vm.Licences.Single(l => l.Name == "extra").TextPath.Should().EndWith("extra.txt");
        LicenceRow taglib = vm.Licences.Single(l => l.Name == "TagLibSharp");
        taglib.Display.Should().Be("TagLibSharp 2.3.0 · LGPL-2.1-only");
        taglib.TextPath.Should().BeNull();
        taglib.Note.Should().Contain("LGPL-2.1-only").And.Contain("release pipeline");
        vm.Licences.Single(l => l.Name.StartsWith("Serilog", StringComparison.Ordinal)).Licence.Should().Be("Apache-2.0");
        vm.Licences.Single(l => l.Name == "pffft").Note.Should().Contain("mpcore.dll").And.Contain("repository");
        vm.Licences.Should().NotContain(l => l.Name.Contains("Catch2", StringComparison.OrdinalIgnoreCase), "Catch2 is test-only");
        vm.Licences.Should().NotContain(l => l.Name.Contains("BenchmarkDotNet", StringComparison.OrdinalIgnoreCase), "build-only tools do not ship");
    }

    /// <summary>T-198: the page lists every shipped row of the repository's own notices file, which is the one the build ships.</summary>
    [Fact]
    public void The_real_notices_put_every_shipped_component_on_the_page_with_its_licence()
    {
        string real = File.ReadAllText(RepoFile("THIRD-PARTY-NOTICES.md"));
        File.WriteAllText(Path.Combine(_scratch, "app", "licenses", "THIRD-PARTY-NOTICES.md"), real);
        AboutSettingsViewModel vm = About();

        vm.LoadLicences();

        IReadOnlyList<ThirdPartyComponent> shipped = [.. ThirdPartyNotices.Parse(real).Where(c => c.Shipped)];
        shipped.Should().HaveCountGreaterThan(20);
        vm.Licences.Skip(1).Take(shipped.Count).Select(l => (l.Name, l.Licence)).Should().Equal(shipped.Select(c => (c.Name, c.Licence)));
        foreach (string name in new[] { "TagLibSharp", "Microsoft.Data.Sqlite", "System.Reactive", "CommunityToolkit.Mvvm" })
        {
            vm.Licences.Should().ContainSingle(l => l.Name == name && l.Licence.Length > 0, $"{name} ships in the package");
        }

        vm.Licences.Should().NotContain(l => l.Name == "Catch2");
    }

    [Fact]
    public void Choosing_a_licence_shows_its_text_or_says_why_there_is_none()
    {
        AboutSettingsViewModel vm = About();
        vm.LoadLicences();

        vm.SelectedLicence.Should().Be(vm.Licences[0], "the notices file is selected first so the page is never blank");
        vm.LicenceText.Should().Be(Notices);

        vm.SelectedLicence = vm.Licences.Single(l => l.Name == "bass");
        vm.LicenceText.Should().Be("BASS 2.4 licence text");

        vm.SelectedLicence = vm.Licences.Single(l => l.Name == "TagLibSharp");
        vm.LicenceText.Should().Contain("release pipeline");

        vm.SelectedLicence = null;
        vm.LicenceText.Should().BeEmpty();
    }

    [Fact]
    public void Without_the_notices_the_page_says_the_build_is_missing_them()
    {
        File.Delete(Path.Combine(_scratch, "app", "licenses", "THIRD-PARTY-NOTICES.md"));
        AboutSettingsViewModel vm = About();

        vm.LoadLicences();

        vm.Licences[0].Note.Should().Contain("not next to the executable");
        vm.LicenceText.Should().Contain("T-128");
    }

    // ---- crash reporting ----------------------------------------------------------------------------------------------

    /// <summary>AC-445: the switch writes diagnostics.crashReporting; seeding from the store does not write it back.</summary>
    [Fact]
    public void The_crash_reporting_switch_writes_its_key_and_seeding_does_not()
    {
        AboutSettingsViewModel seeded = About();
        seeded.CrashReporting.Should().Be(SettingsKeys.Defaults.DiagnosticsCrashReporting);
        _settings.Contains(SettingsKeys.DiagnosticsCrashReporting).Should().BeFalse();

        var changed = new List<string>();
        _settings.Changed += (_, key) => changed.Add(key);
        seeded.CrashReporting = true;

        _settings.GetValue(SettingsKeys.DiagnosticsCrashReporting, false).Should().BeTrue();
        changed.Should().Equal(SettingsKeys.DiagnosticsCrashReporting);
        About().CrashReporting.Should().BeTrue("a new view model reads the stored choice");
        // E8-S5 (AC-532): exactly what is captured, and that nothing leaves the machine unless the user sends it.
        AboutSettingsViewModel.CrashReportingHint.Should().StartWith("Off (the default): nothing is captured beyond what Windows itself records.")
            .And.Contain("crash dump").And.Contain("file paths and track names").And.Contain("last 200 lines of its log")
            .And.Contain("keep or delete it").And.Contain("Nothing leaves this PC unless you send it yourself");
    }

    // ---- export -------------------------------------------------------------------------------------------------------

    /// <summary>AC-444: the zip holds the logs, settings.json and system-info.txt, and the info names what it must.</summary>
    [Fact]
    public async Task The_export_zips_the_logs_the_settings_and_a_system_info_text_Async()
    {
        _renderer = Frames();
        File.WriteAllText(Path.Combine(_paths.LogsDirectory, "tunqio-20260913.log"), "yesterday\n");
        File.WriteAllText(Path.Combine(_paths.LogsDirectory, "tunqio-20260914.log"), "today\n");
        File.WriteAllText(_paths.SettingsPath, "{ \"ui.theme\": \"dark\" }");
        AboutSettingsViewModel vm = About();
        vm.RedactPaths = false;
        string zip = Path.Combine(_scratch, "out", "diag.zip");

        DiagnosticsExportResult? result = await vm.ExportAsync(zip);

        result.Should().NotBeNull();
        result!.Entries.Should().Equal("system-info.txt", "settings.json", "logs/tunqio-20260913.log", "logs/tunqio-20260914.log");
        result.Redactions.Should().Be(0);
        vm.HasNotice.Should().BeTrue();
        vm.NoticeIsError.Should().BeFalse();
        vm.Notice.Should().Contain("4 files");
        using ZipArchive archive = ZipFile.OpenRead(zip);
        Read(archive, "logs/tunqio-20260914.log").Should().Be("today\n");
        Read(archive, "settings.json").Should().Contain("ui.theme");
        string info = Read(archive, "system-info.txt");
        info.Should().Contain("App version: 0.1.0");
        info.Should().Contain("Core version: 0.1.0");
        info.Should().Contain("ABI version: 1.0");
        info.Should().Contain("OS: Windows 10 (X64)");
        info.Should().Contain("GPU adapter: NVIDIA GeForce RTX 4080 SUPER");
        info.Should().Contain("Output device: system default");
        info.Should().Contain("Output format: 48000 Hz · 2 ch · 48000/2/32 · shared · 0 ms buffer · started yes", "the fake engine's stats, as the overlay's Output section shows them");
        info.Should().Contain("Process memory: 123 MB");
        info.Should().Contain("Paths redacted: no");
    }

    [Fact]
    public async Task The_export_names_the_chosen_output_device_and_says_when_there_is_no_audio_or_renderer_Async()
    {
        _engine.Devices.Add(new OutputDevice(1, "USB DAC", "usb-dac", 96_000, 2, TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(10), IsDefault: false));
        _settings.SetValue(SettingsKeys.OutputDeviceId, "usb-dac");
        About().SystemInfo().Should().Contain("Output device: usb-dac (USB DAC)").And.Contain("GPU adapter: unknown (the renderer is not running)");

        _settings.SetValue(SettingsKeys.OutputDeviceId, "gone");
        About().SystemInfo().Should().Contain("Output device: gone (not connected)");

        AboutSettingsViewModel silent = About(source: new StubSessionSource());
        silent.SystemInfo().Should().Contain("Output device: no audio engine").And.Contain("Output format: unknown");
        DiagnosticsExportResult? result = await silent.ExportAsync(Path.Combine(_scratch, "silent.zip"));
        result!.Entries.Should().Contain("system-info.txt", "a machine with no sound still exports");
    }

    /// <summary>AC-444's switch: the profile and the library folders go from the copied settings and logs, in both spellings.</summary>
    [Fact]
    public async Task Redaction_replaces_the_profile_and_library_folder_paths_in_the_copies_only_Async()
    {
        _folders.Rows.Add(new LibraryFolderDto(1, @"C:\Users\phil\Music\", true, null, null));
        _folders.Rows.Add(new LibraryFolderDto(2, @"D:\Archive\", true, null, null));
        string log = Path.Combine(_paths.LogsDirectory, "tunqio-20260914.log");
        File.WriteAllText(log, @"Scanned c:\users\phil\music\Album\01.flac and D:\Archive\x.mp3 under C:\Users\phil");
        File.WriteAllText(_paths.SettingsPath, "{ \"library.lastFolder\": \"C:\\\\Users\\\\phil\\\\Music\\\\Album\", \"other\": \"D:\\\\Archive\" }");
        AboutSettingsViewModel vm = About();
        vm.RedactPaths.Should().BeTrue("a support zip should not carry a user name unasked");
        string zip = Path.Combine(_scratch, "redacted.zip");

        DiagnosticsExportResult? result = await vm.ExportAsync(zip);

        result!.Redactions.Should().Be(5);
        using ZipArchive archive = ZipFile.OpenRead(zip);
        Read(archive, "logs/tunqio-20260914.log").Should().Be(@"Scanned [library folder 1]\Album\01.flac and [library folder 2]\x.mp3 under [user profile]");
        Read(archive, "settings.json").Should().Be("{ \"library.lastFolder\": \"[library folder 1]\\\\Album\", \"other\": \"[library folder 2]\" }");
        Read(archive, "system-info.txt").Should().Contain("Paths redacted: yes");
        File.ReadAllText(log).Should().Contain(@"C:\Users\phil", "the original log is not touched");
        vm.Notice.Should().Contain("5 paths redacted");
    }

    [Fact]
    public void Placeholders_put_the_longer_path_first_and_redaction_is_case_insensitive()
    {
        IReadOnlyList<PathPlaceholder> placeholders = DiagnosticsExport.Placeholders(@"C:\Users\phil\", [@"C:\Users\phil\Music", string.Empty]);

        placeholders.Select(p => p.Path).Should().Equal(@"C:\Users\phil\Music", @"C:\Users\phil");
        DiagnosticsExport.Redact(@"C:\USERS\PHIL\Music\a and c:\users\phil\b", placeholders, out int count)
            .Should().Be(@"[library folder 1]\a and [user profile]\b");
        count.Should().Be(2);
        DiagnosticsExport.Redact("nothing here", placeholders, out count).Should().Be("nothing here");
        count.Should().Be(0);
        // A path is a whole path: the profile of one user is not the front of another's, and the scratch directory
        // this test runs in (under C:\Users\philw) is what found the prefix match.
        DiagnosticsExport.Redact(@"C:\Users\philw\Music and C:\Users\phil", placeholders, out count).Should().Be(@"C:\Users\philw\Music and [user profile]");
        count.Should().Be(1);
    }

    [Fact]
    public async Task An_export_that_cannot_be_written_is_a_notice_not_a_crash_Async()
    {
        AboutSettingsViewModel vm = About();
        string blocked = Path.Combine(_scratch, "blocked");
        Directory.CreateDirectory(blocked); // a directory where the zip should go

        DiagnosticsExportResult? result = await vm.ExportAsync(blocked);

        result.Should().BeNull();
        vm.HasNotice.Should().BeTrue();
        vm.NoticeIsError.Should().BeTrue();
        vm.Notice.Should().StartWith("The diagnostics zip could not be written");
    }

    [Fact]
    public void The_export_switch_takes_a_path_and_an_optional_redact_flag()
    {
        DiagnosticsExportSwitch.Path(["--export-diagnostics", @"C:\tmp\d.zip", "--redact-paths"]).Should().Be(@"C:\tmp\d.zip");
        DiagnosticsExportSwitch.Redact(["--export-diagnostics", @"C:\tmp\d.zip", "--redact-paths"]).Should().BeTrue();
        DiagnosticsExportSwitch.Redact(["--export-diagnostics", @"C:\tmp\d.zip"]).Should().BeFalse();
        DiagnosticsExportSwitch.Path(["--export-diagnostics"]).Should().BeNull();
        DiagnosticsExportSwitch.Path(["--export-diagnostics", "--redact-paths"]).Should().BeNull();
        DiagnosticsExportSwitch.Path(["--render-spike"]).Should().BeNull();
        DiagnosticsExport.SuggestedFileName(new DateTimeOffset(2026, 9, 14, 13, 5, 0, TimeSpan.Zero)).Should().Be("tunqio-diagnostics-20260914-1305.zip");
    }

    // ---- readout ------------------------------------------------------------------------------------------------------

    /// <summary>AC-446: the three lines come from the overlay's inputs, and say what is missing rather than showing zeros.</summary>
    [Fact]
    public void The_readout_reports_dropouts_frame_time_and_memory_from_the_overlay_sources()
    {
        PerformanceReadout none = Diagnostics.Performance(null, null, 0);
        none.Dropouts.Should().Be("no audio engine");
        none.FrameTime.Should().Be("no renderer");
        none.Memory.Should().Be("0 MB working set");

        var stopped = new EngineStats(0, 0, TimeSpan.Zero, 0, 0, TimeSpan.Zero, false, false, "unknown");
        Diagnostics.Performance(stopped, null, 0).Dropouts.Should().Be("output not started");

        var engine = new EngineStats(1200, 1, TimeSpan.FromMilliseconds(2.5), 48_000, 2, TimeSpan.FromMilliseconds(40), false, true, "float");
        PerformanceReadout live = Diagnostics.Performance(engine, Frames(), 150 * 1024 * 1024);
        live.Dropouts.Should().Be("1 underrun in 1200 callbacks · worst callback 2.50 ms");
        live.FrameTime.Should().Be("avg 6.97 ms · max 25.2 ms · 144.0 fps · 3 missed refreshes");
        live.Memory.Should().Be("150 MB working set");
        Diagnostics.Performance(engine with { Underruns = 2 }, null, 0).Dropouts.Should().StartWith("2 underruns");
    }

    /// <summary>AC-446's "while the page is visible only": the timer runs between activation and deactivation and not outside it.</summary>
    [Fact]
    public void The_readout_ticks_only_while_the_page_says_it_is_on_screen()
    {
        AboutSettingsViewModel vm = About();
        vm.Memory.Should().Be("123 MB working set", "the first reading is taken at construction");
        _workingSet = 200 * 1024 * 1024;
        _clock.Advance(AboutSettingsViewModel.RefreshInterval);
        vm.Memory.Should().Be("123 MB working set", "nothing ticks before the page is on screen");

        vm.IsReadoutActive = true;
        vm.Memory.Should().Be("200 MB working set", "activation refreshes at once");
        _workingSet = 210 * 1024 * 1024;
        _clock.Advance(AboutSettingsViewModel.RefreshInterval);
        vm.Memory.Should().Be("210 MB working set");
        vm.Dropouts.Should().Be("0 underruns in 0 callbacks · worst callback 0.00 ms", "the fake engine reports a started output with nothing counted");

        vm.IsReadoutActive = false;
        _workingSet = 300 * 1024 * 1024;
        _clock.Advance(AboutSettingsViewModel.RefreshInterval);
        vm.Memory.Should().Be("210 MB working set", "a hidden page costs no reads");

        vm.Dispose();
        vm.IsReadoutActive = true;
        _clock.Advance(AboutSettingsViewModel.RefreshInterval);
        vm.Memory.Should().Be("210 MB working set", "a disposed view model starts no timer");
    }

    [Fact]
    public void A_renderer_that_throws_is_reported_as_missing_rather_than_taking_the_readout_down()
    {
        var vm = new AboutSettingsViewModel(
            Environment(Path.Combine(_scratch, "app")), _source, _settings, _paths, _folders,
            () => throw new InvalidOperationException("mid-teardown"), new Crash.CrashReportStore(_paths.DataRoot), null, _clock, () => 0, string.Empty);

        vm.FrameTime.Should().Be("no renderer");
        vm.SystemInfo().Should().Contain("GPU adapter: unknown");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------

    private static string Read(ZipArchive archive, string entry)
    {
        using var reader = new StreamReader(archive.GetEntry(entry)!.Open());
        return reader.ReadToEnd();
    }

    /// <summary>The repository root from the test binary: the artifacts tree is inside it.</summary>
    private static string RepoFile(string name)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Tunqio.sln")))
            {
                return Path.Combine(dir, name);
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new InvalidOperationException("Tunqio.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>An <see cref="IAppPaths"/> over a scratch root, so nothing here touches %LocalAppData%.</summary>
    private sealed class ScratchPaths(string root) : IAppPaths
    {
        public string DataRoot { get; } = root;

        public string DatabasePath => Path.Combine(DataRoot, "library.db");

        public string SettingsPath => Path.Combine(DataRoot, "settings.json");

        public string LogsDirectory => Path.Combine(DataRoot, "logs");

        public string ArtDirectory => Path.Combine(DataRoot, "art");

        public string PresetsDirectory => Path.Combine(DataRoot, "presets");

        public string ExportsDirectory => Path.Combine(DataRoot, "exports");

        public void EnsureCreated()
        {
            Directory.CreateDirectory(DataRoot);
            Directory.CreateDirectory(LogsDirectory);
        }
    }
}
