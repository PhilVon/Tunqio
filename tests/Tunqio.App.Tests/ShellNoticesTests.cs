using Tunqio.App;
using Tunqio.App.Library;
using Tunqio.App.Shell;
using Tunqio.Core.Audio;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;

namespace Tunqio.App.Tests;

/// <summary>
/// E2-S7: the shell's error surfaces over a real <see cref="PlaybackSession"/> and a fake engine. The rule the
/// story turns on is which bars stay — an unplugged device is a state the user is still in a minute later, and a
/// bar that timed out would leave silence with no explanation, which is the failure flow 4 exists to prevent.
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class ShellNoticesTests : IAsyncLifetime
{
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeSettings _settings = new();
    private readonly FakePlayHistory _history = new();
    private readonly FakeQueueStore _queues = new();
    private readonly StubSessionSource _source = new();
    private readonly ManualClock _clock = new();
    private PlaybackSession _session = null!;
    private ShellNotices _notices = null!;

    public Task InitializeAsync()
    {
        _tracks.Rows.AddRange([Rows.Track(11, "One"), Rows.Track(12, "Two")]);
        _session = new PlaybackSession(_engine, _tracks, _history, _queues, _settings, autoPoll: false);
        _source.Session = _session;
        _notices = new ShellNotices(_source, scans: null, ui: null, clock: _clock);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _notices.Dispose();
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
    }

    private ShellNotice? Of(NoticeKind kind) => _notices.Items.FirstOrDefault(n => n.Kind == kind);

    private void Unplug() => _engine.Raise(new EngineEvent(EngineEventType.DeviceLost, 3, 0, "usb-dac"));

    // ---- AC-79: the sticky bar and its action -----------------------------------------------------------------------

    [Fact]
    public async Task Unplugging_the_device_puts_up_a_bar_that_stays_and_offers_the_default_Async()
    {
        await _session.PlayNowAsync([11, 12]);
        Unplug();
        await _session.PollAsync();

        ShellNotice bar = Of(NoticeKind.Output)!;
        bar.Should().NotBeNull();
        bar.Title.Should().Be("Output device disconnected");
        bar.IsSticky.Should().BeTrue();
        bar.ActionText.Should().Be("Use default device");
        bar.HasAction.Should().BeTrue();

        // Ten more snapshots and an hour later, the bar is still the true thing to be showing.
        await _session.PollAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        Of(NoticeKind.Output).Should().BeSameAs(bar);
    }

    [Fact]
    public async Task The_bars_action_reopens_the_output_and_takes_the_bar_away_Async()
    {
        await _session.PlayNowAsync([11, 12]);
        Unplug();
        await _session.PollAsync();
        _engine.Drain();

        await Of(NoticeKind.Output)!.Action!();

        _engine.Calls.Should().Contain("init:-1:shared:40").And.Contain("resume");
        _session.Current.State.Should().Be(PlaybackState.Playing);
        Of(NoticeKind.Output).Should().BeNull("the output is fine again, so there is nothing to say about it");
    }

    [Fact]
    public async Task An_action_that_fails_leaves_the_bar_saying_what_is_still_true_Async()
    {
        await _session.PlayNowAsync([11, 12]);
        Unplug();
        await _session.PollAsync();
        _engine.UnopenableDevices.Add(OutputConfig.DefaultDevice);

        await Of(NoticeKind.Output)!.Action!();

        Of(NoticeKind.Output)!.Title.Should().Be("Output device disconnected");
    }

    [Fact]
    public async Task The_device_coming_back_replaces_the_bar_with_the_offer_Async()
    {
        await _session.PlayNowAsync([11, 12]);
        Unplug();
        await _session.PollAsync();

        _engine.Raise(new EngineEvent(EngineEventType.DeviceChanged, 3, 0, "usb-dac"));
        await _session.PollAsync();

        _notices.Items.Should().ContainSingle(n => n.Kind == NoticeKind.Output, "one bar about the output, not two");
        Of(NoticeKind.Output)!.ActionText.Should().Be("Switch back");
    }

    // ---- engine errors: told once, and they go ----------------------------------------------------------------------

    [Fact]
    public async Task An_engine_error_is_told_once_and_goes_on_its_own_Async()
    {
        _engine.Raise(new EngineEvent(EngineEventType.Error, 0, 0, "exclusive mode refused by the driver"));
        await _session.PollAsync();

        ShellNotice bar = Of(NoticeKind.EngineError)!;
        bar.Message.Should().Be("exclusive mode refused by the driver");
        bar.IsSticky.Should().BeFalse();
        bar.HasAction.Should().BeFalse();

        _clock.Advance(ShellNotices.TransientLifetime + TimeSpan.FromSeconds(1));

        Of(NoticeKind.EngineError).Should().BeNull();
    }

    [Fact]
    public async Task A_second_error_replaces_the_first_rather_than_stacking_on_it_Async()
    {
        _engine.Raise(new EngineEvent(EngineEventType.Error, 0, 0, "first"));
        _engine.Raise(new EngineEvent(EngineEventType.Error, 0, 0, "second"));
        await _session.PollAsync();

        _notices.Items.Should().ContainSingle(n => n.Kind == NoticeKind.EngineError);
        Of(NoticeKind.EngineError)!.Message.Should().Be("second");
    }

    /// <summary>An engine that fails on every buffer must not turn the window into a column of bars.</summary>
    [Fact]
    public async Task A_hundred_errors_are_still_one_bar_Async()
    {
        for (int i = 0; i < 100; i++)
        {
            _engine.Raise(new EngineEvent(EngineEventType.Error, 0, 0, "buffer " + i));
            await _session.PollAsync();
        }

        _notices.Items.Should().HaveCount(1);
    }

    // ---- the kinds do not crowd each other out ----------------------------------------------------------------------

    [Fact]
    public async Task A_device_going_while_an_error_is_up_leaves_both_Async()
    {
        await _session.PlayNowAsync([11, 12]);
        _engine.Raise(new EngineEvent(EngineEventType.Error, 0, 0, "something went wrong"));
        await _session.PollAsync();
        Unplug();
        await _session.PollAsync();

        _notices.Items.Should().HaveCount(2, "they are about different things, and one hiding the other loses one");
        _notices.Any.Should().BeTrue();
    }

    [Fact]
    public void A_start_up_notice_stays_until_it_is_dismissed()
    {
        _notices.Show(new StartupNotice("Library recovered", "A fresh database was created.", StartupNoticeSeverity.Warning));

        ShellNotice bar = Of(NoticeKind.Startup)!;
        bar.IsSticky.Should().BeTrue();
        _clock.Advance(TimeSpan.FromHours(1));
        Of(NoticeKind.Startup).Should().NotBeNull();

        _notices.Dismiss(bar);

        Of(NoticeKind.Startup).Should().BeNull();
        _notices.Any.Should().BeFalse();
    }

    [Fact]
    public void A_shell_with_no_session_and_no_scanner_simply_has_nothing_to_say()
    {
        using var quiet = new ShellNotices(source: null, scans: null);

        quiet.Items.Should().BeEmpty();
        quiet.Any.Should().BeFalse();
    }

    // ---- scan reports -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_finished_scan_is_reported_once_Async()
    {
        var scanner = new FakeScanner();
        using var scans = new LibraryScanCoordinator(scanner, _clock);
        using var notices = new ShellNotices(source: null, scans, ui: null, clock: _clock);

        await ScanAsync(scans, scanner, FakeScanner.Report(added: 30, unchanged: 86));

        ShellNotice bar = notices.Items.Should().ContainSingle().Subject;
        bar.Kind.Should().Be(NoticeKind.Scan);
        bar.Title.Should().Be("Scan finished");
        bar.Message.Should().Be(LibraryScanCoordinator.Describe(scans.LastReport!));
        bar.Severity.Should().Be(StartupNoticeSeverity.Informational);
        bar.IsSticky.Should().BeFalse("a scan that has finished is not a state anyone is still in");
    }

    [Fact]
    public async Task A_scan_that_could_not_read_some_files_says_so_Async()
    {
        var scanner = new FakeScanner();
        using var scans = new LibraryScanCoordinator(scanner, _clock);
        using var notices = new ShellNotices(source: null, scans, ui: null, clock: _clock);

        await ScanAsync(scans, scanner, FakeScanner.Report(added: 30, failed: 2));

        ShellNotice bar = notices.Items.Should().ContainSingle().Subject;
        bar.Title.Should().Be("Scan finished with problems");
        bar.Severity.Should().Be(StartupNoticeSeverity.Warning);
    }

    /// <summary>
    /// The launch scan runs every time the app starts and usually finds exactly what it found last time. A bar
    /// saying so at every launch trains the user to ignore the bar the device-disconnected case needs them to read.
    /// </summary>
    [Fact]
    public async Task A_scan_that_found_nothing_new_says_nothing_Async()
    {
        var scanner = new FakeScanner();
        using var scans = new LibraryScanCoordinator(scanner, _clock);
        using var notices = new ShellNotices(source: null, scans, ui: null, clock: _clock);

        await ScanAsync(scans, scanner, FakeScanner.Report(unchanged: 116));

        notices.Items.Should().BeEmpty();
        scans.LastReport.Should().NotBeNull("the settings page still shows it; it is the bar that stays quiet");
    }

    /// <summary>
    /// The coordinator raises its change event for progress as well as for the report, and a scan that changed
    /// nothing does not move the library's version — so neither of those can be what tells one report from the next.
    /// </summary>
    [Fact]
    public async Task Two_scans_that_each_changed_something_are_two_reports_Async()
    {
        var scanner = new FakeScanner();
        using var scans = new LibraryScanCoordinator(scanner, _clock);
        using var notices = new ShellNotices(source: null, scans, ui: null, clock: _clock);

        await ScanAsync(scans, scanner, FakeScanner.Report(added: 1, unchanged: 115));
        ShellNotice first = notices.Items.Should().ContainSingle().Subject;

        _clock.Advance(TimeSpan.FromMinutes(5));
        await ScanAsync(scans, scanner, FakeScanner.Report(added: 1, unchanged: 116));

        notices.Items.Should().ContainSingle().Which.Should().NotBeSameAs(first);
    }

    /// <summary>Starts a scan, hands the fake scanner its report, and waits for the coordinator to have taken it.</summary>
    private static async Task ScanAsync(LibraryScanCoordinator scans, FakeScanner scanner, ScanReport report)
    {
        Task<ScanReport?> running = scans.ScanAsync(new ScanRequest());
        for (int i = 0; i < 200 && scanner.Requests.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        scanner.Finish(report);
        await running;
    }

}
#pragma warning restore CA1001
