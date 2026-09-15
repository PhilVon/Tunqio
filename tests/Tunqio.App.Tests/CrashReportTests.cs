using System.IO.Compression;
using Tunqio.App.Crash;
using Tunqio.App.Shell;
using Tunqio.Core;

namespace Tunqio.App.Tests;

/// <summary>
/// E8-S5 (T-84): the crash report store, the log line buffer, the opt-in rule, the test switch, the next launch's dialog and
/// the export of a kept report, over a scratch data root. The crash handlers themselves (a real dump from a dying process)
/// are tools/check-crash-report.ps1's.
/// </summary>
public sealed class CrashReportTests : IDisposable
{
    private static readonly DateTimeOffset Earlier = new(2026, 9, 14, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 9, 14, 11, 30, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunqio-crash-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSettings _settings = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private CrashReportStore Store => new(_root);

    private CrashReport WriteReport(DateTimeOffset when, string type = "System.InvalidOperationException", bool withDump = true, bool withInfo = true)
    {
        string folder = Store.CreateReportFolder(when, 4242);
        if (withDump)
        {
            File.WriteAllBytes(Path.Combine(folder, CrashReportStore.DumpFileName), new byte[3000]);
        }

        CrashReportStore.WriteLog(folder, ["line one", @"line two C:\Users\phil\Music\a.flac"]);
        if (withInfo)
        {
            CrashReportStore.WriteInfo(folder, new CrashReportInfo("AppDomain", type, "boom", "at Somewhere()", null, when, "session-1", "0.1.0", withDump ? 3000 : 0, withDump ? null : "MiniDumpWriteDump failed with error 5"));
        }

        return CrashReportStore.Load(folder)!;
    }

    private void TurnOn() => _settings.SetValue(SettingsKeys.DiagnosticsCrashReporting, true);

    // ---- store ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_report_round_trips_through_its_folder_and_the_newest_is_listed_first()
    {
        CrashReport older = WriteReport(Earlier, "System.IO.IOException");
        CrashReport newer = WriteReport(Later);

        IReadOnlyList<CrashReport> listed = Store.List();

        listed.Select(r => r.Id).Should().Equal(newer.Id, older.Id);
        newer.Folder.Should().StartWith(Path.Combine(_root, CrashReportStore.FolderName));
        newer.Info!.ExceptionType.Should().Be("System.InvalidOperationException");
        newer.Info.Message.Should().Be("boom");
        newer.Info.TimeUtc.Should().Be(Later);
        newer.LogLines.Should().HaveCount(2);
        newer.DumpBytes.Should().Be(3000);
        newer.Kept.Should().BeFalse();
        Store.Pending().Should().HaveCount(2);
    }

    [Fact]
    public void Keep_marks_a_report_as_answered_and_Delete_removes_its_folder()
    {
        CrashReport report = WriteReport(Later);

        CrashReportStore.Keep(report);
        Store.Pending().Should().BeEmpty();
        Store.KeptReports().Should().ContainSingle().Which.Kept.Should().BeTrue();

        CrashReportStore.Delete(report).Should().BeTrue();
        Directory.Exists(report.Folder).Should().BeFalse();
        Store.List().Should().BeEmpty();
    }

    [Fact]
    public void A_report_the_crash_did_not_finish_is_still_listed_and_an_empty_folder_is_not_a_report()
    {
        CrashReport unfinished = WriteReport(Later, withInfo: false);
        Directory.CreateDirectory(Path.Combine(Store.Root, "stray"));

        Store.List().Should().ContainSingle().Which.Id.Should().Be(unfinished.Id);
        unfinished.Info.Should().BeNull();
        Store.CreateReportFolder(Later, 4242).Should().NotBe(unfinished.Folder, "a second crash in the same millisecond gets its own folder");
    }

    [Fact]
    public void With_no_crash_folder_there_is_nothing_to_list()
    {
        Store.List().Should().BeEmpty();
        Store.Pending().Should().BeEmpty();
    }

    // ---- log buffer ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_log_buffer_keeps_the_last_200_lines_oldest_first_splitting_multi_line_events()
    {
        var buffer = new CrashLogBuffer("{Message}{NewLine}");
        for (int i = 0; i < 250; i++)
        {
            buffer.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"line {i}\n"));
        }

        IReadOnlyList<string> lines = buffer.Snapshot();
        lines.Should().HaveCount(CrashLogBuffer.Capacity);
        lines[0].Should().Be("line 50");
        lines[^1].Should().Be("line 249");

        buffer.Add("exception\r\n   at Frame()\r\n");
        buffer.Snapshot().TakeLast(2).Should().Equal("exception", "   at Frame()");
    }

    // ---- opt-in rule --------------------------------------------------------------------------------------------------

    /// <summary>AC-532: with diagnostics.crashReporting off, the default, a crash captures nothing and leaves no folder.</summary>
    [Fact]
    public void Off_by_default_a_crash_captures_nothing()
    {
        var reporter = new CrashReporter(Store, new CrashLogBuffer("{Message}{NewLine}"), _settings, "0.1.0", Guid.NewGuid());

        reporter.IsEnabled.Should().BeFalse();
        CrashReporter.ReadEnabled(_settings).Should().Be(SettingsKeys.Defaults.DiagnosticsCrashReporting).And.BeFalse();
        reporter.CaptureManaged("AppDomain", new InvalidOperationException("boom")).Should().BeNull();
        Directory.Exists(Store.Root).Should().BeFalse();
    }

    /// <summary>AC-530: on, a managed crash writes a minidump of this very process and the buffered log lines, once per process.</summary>
    [Fact]
    public void On_a_crash_writes_a_minidump_the_log_lines_and_what_the_exception_was_once()
    {
        var buffer = new CrashLogBuffer("{Message}{NewLine}");
        buffer.Add("before the crash\n");
        var reporter = new CrashReporter(Store, buffer, _settings, "0.1.0", Guid.NewGuid());
        TurnOn();
        reporter.IsEnabled.Should().BeTrue("the reporter follows the setting while the app runs");

        CrashReport? report = reporter.CaptureManaged("AppDomain", new InvalidOperationException("boom"));

        report.Should().NotBeNull();
        // The problem text first, so a failed write names its Win32 error rather than only a size (T-209).
        report!.Info!.DumpProblem.Should().BeNull("MiniDumpWriteDump of this test host should succeed, but the report says: {0}", report.Info.DumpProblem);
        report.DumpBytes.Should().BeGreaterThan(10_000, "MiniDumpWriteDump wrote this test host's threads and modules");
        File.ReadAllBytes(report.DumpPath).Take(4).Should().Equal((byte)'M', (byte)'D', (byte)'M', (byte)'P');
        report.LogLines.Should().Contain("before the crash");
        report.Info!.Source.Should().Be("AppDomain");
        report.Info.ExceptionType.Should().Be("System.InvalidOperationException");
        report.Info.Message.Should().Be("boom");
        report.Info.DumpBytes.Should().Be(report.DumpBytes);
        reporter.CaptureManaged("XAML", new InvalidOperationException("again")).Should().BeNull("a process ends once, so it reports once");
        Store.List().Should().ContainSingle();
    }

    [Fact]
    public void Turning_it_off_while_running_stops_capture()
    {
        TurnOn();
        var reporter = new CrashReporter(Store, new CrashLogBuffer("{Message}{NewLine}"), _settings, "0.1.0", Guid.NewGuid());
        _settings.SetValue(SettingsKeys.DiagnosticsCrashReporting, false);

        reporter.IsEnabled.Should().BeFalse();
        reporter.CaptureManaged("AppDomain", null).Should().BeNull();
        Store.List().Should().BeEmpty();
    }

    [Fact]
    public void Native_exception_codes_are_named_and_sizes_are_said_in_plain_units()
    {
        CrashReporter.NativeExceptionText.Type(0xC0000005).Should().Be("Native exception 0xC0000005 (access violation)");
        CrashReporter.NativeExceptionText.Name(0xE0000001).Should().Be("structured exception");
        CrashReporter.NativeExceptionText.Where(0, 0xC0000005).Should().Be("no exception record");
        CrashReporter.FormatSize(512).Should().Be("512 bytes");
        CrashReporter.FormatSize(3000).Should().Be("3 KB");
        CrashReporter.FormatSize(5_300_000).Should().Be("5.1 MB");
    }

    // ---- test switch --------------------------------------------------------------------------------------------------

    /// <summary>AC-533: the crash switch needs all three of the variable, a scratch data root and a known kind.</summary>
    [Fact]
    public void The_crash_test_switch_needs_the_variable_a_data_root_and_a_known_kind()
    {
        string[] native = ["--data-root", @"C:\scratch", "--crash-test", "native"];
        CrashTestSwitch.Requested(native, "1").Should().Be(CrashTestKind.Native);
        CrashTestSwitch.Requested(["--data-root", @"C:\scratch", "--crash-test", "managed"], "1").Should().Be(CrashTestKind.Managed);
        CrashTestSwitch.Requested(["--crash-test", "xaml", "--data-root", @"C:\scratch"], "1").Should().Be(CrashTestKind.Xaml);

        CrashTestSwitch.Requested(native, null).Should().BeNull("no variable, no crash");
        CrashTestSwitch.Requested(native, "true").Should().BeNull();
        CrashTestSwitch.Requested(["--crash-test", "native"], "1").Should().BeNull("the real profile is never crashed on purpose");
        CrashTestSwitch.Requested(["--data-root", @"C:\scratch", "--crash-test", "everything"], "1").Should().BeNull();
        CrashTestSwitch.Requested(["--data-root", @"C:\scratch", "--crash-test"], "1").Should().BeNull();
    }

    // ---- dialog -------------------------------------------------------------------------------------------------------

    /// <summary>AC-532: with the setting off no report is offered, even one captured while it was on.</summary>
    [Fact]
    public void With_the_setting_off_no_report_is_offered()
    {
        WriteReport(Later);
        var vm = new CrashReportViewModel(Store, _settings);

        vm.Next().Should().BeNull();
        vm.Keep().Should().BeFalse("there is nothing to decide");
        Store.Pending().Should().ContainSingle("the report stays on disk untouched");
    }

    /// <summary>AC-531: the dialog says in plain words what was captured: the exception, the log lines, the dump's size and place.</summary>
    [Fact]
    public void The_dialog_says_what_was_captured_in_plain_words()
    {
        WriteReport(Earlier, "System.IO.IOException");
        CrashReport newest = WriteReport(Later);
        TurnOn();
        var vm = new CrashReportViewModel(Store, _settings);

        vm.Next()!.Id.Should().Be(newest.Id, "the newest report is offered first");

        vm.Summary.Should().StartWith("Tunqio stopped because of an error on ").And.Contain("saved a report about it on this PC").And.Contain("Nothing has been sent anywhere");
        vm.ExceptionLine.Should().Be("System.InvalidOperationException: boom");
        vm.LogCaption.Should().Be("The last 2 lines of Tunqio's log before it stopped:");
        vm.LogText.Should().Be("line one" + Environment.NewLine + @"line two C:\Users\phil\Music\a.flac");
        vm.DumpLine.Should().Be("A crash dump of 3 KB is saved at " + newest.DumpPath + ". A crash dump is a copy of part of Tunqio's memory when it stopped, so it can contain file paths and track names.");
        CrashReportViewModel.ChoiceLine.Should().StartWith("Nothing leaves this PC unless you send it yourself.").And.Contain("Export diagnostics");
    }

    [Fact]
    public void A_report_with_no_dump_and_no_record_says_so()
    {
        WriteReport(Later, withDump: false);
        TurnOn();
        var vm = new CrashReportViewModel(Store, _settings);
        vm.Next();
        vm.DumpLine.Should().Be("No crash dump could be saved (MiniDumpWriteDump failed with error 5).");

        CrashReportStore.Delete(vm.Report!);
        WriteReport(Earlier, withInfo: false);
        var unfinished = new CrashReportViewModel(Store, _settings);
        unfinished.Next();
        unfinished.ExceptionLine.Should().StartWith("Not recorded");
        unfinished.Summary.Should().StartWith("Tunqio stopped because of an error. ");
    }

    /// <summary>AC-165 and AC-531: Keep marks the report for the export and it is never offered again.</summary>
    [Fact]
    public void Keep_is_recorded_once_and_the_report_is_not_offered_again()
    {
        CrashReport report = WriteReport(Later);
        TurnOn();
        var vm = new CrashReportViewModel(Store, _settings);
        vm.Next();

        vm.Keep().Should().BeTrue();
        vm.Delete().Should().BeFalse("the first choice stands");
        vm.CloseWithoutChoice();

        vm.Decision.Should().Be(CrashReportDecision.Kept);
        Directory.Exists(report.Folder).Should().BeTrue();
        Store.KeptReports().Should().ContainSingle();
        vm.Next().Should().BeNull("this launch has offered it");
        new CrashReportViewModel(Store, _settings).Next().Should().BeNull("the next launch does not offer a kept report either");
    }

    /// <summary>AC-165 and AC-531: declining (Delete) removes the report, once.</summary>
    [Fact]
    public void Declining_deletes_the_report_and_nothing_is_offered_again()
    {
        CrashReport report = WriteReport(Later);
        TurnOn();
        var vm = new CrashReportViewModel(Store, _settings);
        vm.Next();

        vm.Delete().Should().BeTrue();
        vm.CloseWithoutChoice();
        vm.Keep().Should().BeFalse();

        vm.Decision.Should().Be(CrashReportDecision.Deleted);
        Directory.Exists(report.Folder).Should().BeFalse();
        vm.Next().Should().BeNull();
        new CrashReportViewModel(Store, _settings).Next().Should().BeNull();
    }

    [Fact]
    public void Closing_without_a_choice_keeps_rather_than_deletes()
    {
        CrashReport report = WriteReport(Later);
        TurnOn();
        var vm = new CrashReportViewModel(Store, _settings);
        vm.Next();

        vm.CloseWithoutChoice();

        vm.Decision.Should().Be(CrashReportDecision.Kept);
        Directory.Exists(report.Folder).Should().BeTrue();
        Store.Pending().Should().BeEmpty();
    }

    [Fact]
    public void Each_waiting_report_is_offered_in_turn_up_to_the_launch_limit()
    {
        for (int i = 0; i < CrashReportViewModel.MaxOfferedPerLaunch + 1; i++)
        {
            WriteReport(Earlier.AddMinutes(i));
        }

        TurnOn();
        var vm = new CrashReportViewModel(Store, _settings);
        var offered = new List<string>();
        while (vm.Next() is { } report)
        {
            offered.Add(report.Id);
            vm.Delete();
        }

        offered.Should().HaveCount(CrashReportViewModel.MaxOfferedPerLaunch).And.OnlyHaveUniqueItems();
        Store.Pending().Should().ContainSingle("the oldest waits for the next launch");
    }

    // ---- export -------------------------------------------------------------------------------------------------------

    /// <summary>AC-531: a kept report goes into the diagnostics zip, its text redacted like the logs and its dump as it is.</summary>
    [Fact]
    public void A_kept_report_is_in_the_diagnostics_zip_with_its_dump()
    {
        CrashReport report = WriteReport(Later);
        CrashReportStore.Keep(report);
        string logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        string zip = Path.Combine(_root, "diag.zip");
        IReadOnlyList<PathPlaceholder> redact = DiagnosticsExport.Placeholders(@"C:\Users\phil", []);

        DiagnosticsExportResult result = DiagnosticsExport.Write(zip, new DiagnosticsExportRequest(logs, null, "info", redact, [.. Store.KeptReports().Select(r => r.Folder)]));

        string prefix = "crashes/" + report.Id + "/";
        result.Entries.Should().Equal("system-info.txt", prefix + "log.txt", prefix + "report.json", prefix + "tunqio.dmp");
        using ZipArchive archive = ZipFile.OpenRead(zip);
        archive.GetEntry(prefix + "tunqio.dmp")!.Length.Should().Be(3000);
        archive.GetEntry(prefix + "kept").Should().BeNull();
        using var reader = new StreamReader(archive.GetEntry(prefix + "log.txt")!.Open());
        reader.ReadToEnd().Should().Contain(@"[user profile]\Music\a.flac");
        result.Redactions.Should().Be(1);
    }
}
