using Tunqio.App.Shell;
using Tunqio.Core.Audio;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Tests;

/// <summary>
/// E2-S8: what the diagnostics overlay says, and that it keeps saying it. The values and the copied text are pure
/// functions of the three things the overlay reports on, which is why they are asserted here rather than read off
/// a screen; that the overlay opens on Ctrl+Shift+D is the shortcut table's, and is asserted with it.
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class DiagnosticsTests : IAsyncLifetime
{
    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeSettings _settings = new();
    private readonly FakePlayHistory _history = new();
    private readonly FakeQueueStore _queues = new();
    private readonly StubSessionSource _source = new();
    private readonly ManualClock _clock = new();
    private PlaybackSession _session = null!;

    public Task InitializeAsync()
    {
        _tracks.Rows.AddRange([Rows.Track(11, "One"), Rows.Track(12, "Two")]);
        _session = new PlaybackSession(_engine, _tracks, _history, _queues, _settings, autoPoll: false);
        _source.Session = _session;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
    }

    private static RenderStats Frames(double fps = 144.0, long missed = 3) => new(
        Frames: 1200,
        Resizes: 2,
        Fps: fps,
        FrameLast: TimeSpan.FromMilliseconds(6.9),
        FrameMax: TimeSpan.FromMilliseconds(25.2),
        FrameAverage: TimeSpan.FromMilliseconds(6.97),
        FrameHistogram: [1143, 4, 0, 1, 0, 0],
        DxgiPresentCount: 1200,
        DxgiMissedRefreshes: missed,
        Width: 854,
        Height: 720,
        Warp: false,
        Headless: false,
        DeviceLost: false,
        Visible: true,
        Adapter: "NVIDIA GeForce RTX 4080 SUPER");

    private static string Value(IReadOnlyList<DiagnosticsSection> sections, string section, string label) =>
        sections.Single(s => s.Title == section).Rows.Single(r => r.Label == label).Value;

    // ---- what it says -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task It_reports_the_transport_the_output_and_the_renderer_Async()
    {
        await _session.PlayNowAsync([11, 12]);
        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(65) };
        await _session.PollAsync();

        IReadOnlyList<DiagnosticsSection> sections = Diagnostics.Describe(
            _session.Current, _session.EngineStats, Frames(), "0.1.0");

        sections.Select(s => s.Title).Should().Equal("Playback", "Output", "Renderer", "Build");
        Value(sections, "Playback", "State").Should().Be("Playing");
        Value(sections, "Playback", "Position").Should().Be("1:05 / 3:00");
        Value(sections, "Playback", "Track").Should().Be("One (id 11)");
        Value(sections, "Playback", "Queue").Should().Be("1 of 2");
        Value(sections, "Playback", "Output health").Should().Be("ok");
        Value(sections, "Output", "Latency").Should().Be("0.0 ms");
        Value(sections, "Output", "Underruns").Should().Be("0");
        Value(sections, "Renderer", "Frame rate").Should().Be("144.0 fps");
        Value(sections, "Renderer", "Missed refreshes").Should().Be("3");
        Value(sections, "Build", "Version").Should().Be("0.1.0");
    }

    /// <summary>The bare counts mean nothing without the buckets they are counts of, and the last one is open-ended.</summary>
    [Fact]
    public void The_frame_histogram_carries_the_edges_it_counts_between()
    {
        IReadOnlyList<DiagnosticsSection> sections = Diagnostics.Describe(null, null, Frames());

        Value(sections, "Renderer", "Frame histogram")
            .Should().Be("<8.4ms 1143 · <16.7ms 4 · <20ms 0 · <33.4ms 1 · <50ms 0 · >50ms 0");
    }

    [Fact]
    public async Task A_lost_device_is_in_the_report_because_that_is_what_someone_is_reading_it_about_Async()
    {
        await _session.PlayNowAsync([11, 12]);
        _engine.Raise(new EngineEvent(EngineEventType.DeviceLost, 3, 0, "usb-dac"));
        await _session.PollAsync();

        IReadOnlyList<DiagnosticsSection> sections = Diagnostics.Describe(_session.Current, _session.EngineStats, null);

        Value(sections, "Playback", "Output health").Should().Be("device lost (usb-dac)");
    }

    /// <summary>Every part can be missing on its own, and the report says which rather than dropping the section.</summary>
    [Fact]
    public void Anything_that_is_not_there_is_said_to_be_missing()
    {
        IReadOnlyList<DiagnosticsSection> sections = Diagnostics.Describe(null, null, null);

        Value(sections, "Playback", "State").Should().Be("no session");
        Value(sections, "Output", "Engine").Should().Be("unavailable");
        Value(sections, "Renderer", "Renderer").Should().Be("unavailable");
        Value(sections, "Build", "Version").Should().Be("unknown");
    }

    [Fact]
    public void A_renderer_that_would_not_start_says_why_rather_than_only_that_it_did_not()
    {
        IReadOnlyList<DiagnosticsSection> sections = Diagnostics.Describe(
            null, null, null, "0.1.0", "unavailable: mpcore.dll not found");

        Value(sections, "Renderer", "Renderer").Should().Be("unavailable: mpcore.dll not found");
    }

    // ---- copying it -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_copied_text_is_every_row_that_is_on_the_screen()
    {
        IReadOnlyList<DiagnosticsSection> sections = Diagnostics.Describe(null, null, Frames(), "0.1.0");

        string text = Diagnostics.Report(sections);

        foreach (DiagnosticsSection section in sections)
        {
            text.Should().Contain("[" + section.Title + "]");
            foreach (DiagnosticsRow row in section.Rows)
            {
                text.Should().Contain(row.Label + "\t" + row.Value);
            }
        }
    }

    // ---- and that it keeps moving ------------------------------------------------------------------------------------

    [Fact]
    public async Task The_values_follow_the_session_while_the_overlay_is_open_Async()
    {
        using var vm = new DiagnosticsViewModel(_source, () => Frames(), "0.1.0", ui: null, clock: _clock);
        await _session.PlayNowAsync([11, 12]);
        await _session.PollAsync();

        vm.Toggle();
        vm.IsVisible.Should().BeTrue();
        Value(vm.Sections, "Playback", "Position").Should().Be("0:00 / 3:00");

        _engine.Clock = _engine.Clock with { Position = TimeSpan.FromSeconds(30) };
        await _session.PollAsync();
        _clock.Advance(DiagnosticsViewModel.RefreshInterval);

        Value(vm.Sections, "Playback", "Position").Should().Be("0:30 / 3:00");
        vm.Text.Should().Contain("0:30 / 3:00", "what is copied is what is on the screen");
    }

    /// <summary>Closed, it costs nothing: a hidden overlay that kept polling the engine would be a leak with a lid on.</summary>
    [Fact]
    public void A_hidden_overlay_stops_reading_anything()
    {
        int reads = 0;
        using var vm = new DiagnosticsViewModel(_source, () => { reads++; return null; }, clock: _clock);
        vm.Toggle();
        _clock.Advance(DiagnosticsViewModel.RefreshInterval);
        int whileOpen = reads;
        whileOpen.Should().BeGreaterThan(0);

        vm.Toggle();
        _clock.Advance(TimeSpan.FromMinutes(10));

        reads.Should().Be(whileOpen);
        vm.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void A_renderer_that_throws_on_the_way_down_does_not_take_the_overlay_with_it()
    {
        using var vm = new DiagnosticsViewModel(_source, () => throw new InvalidOperationException("gone"), clock: _clock);

        vm.Toggle();

        Value(vm.Sections, "Renderer", "Renderer").Should().Be("unavailable");
    }

    // ---- the shortcut -------------------------------------------------------------------------------------------------

    /// <summary>
    /// The one shortcut that still fires while someone is typing. Ctrl+Shift+D means nothing to a text box, and the
    /// moment a diagnostic overlay is most wanted is the moment something has gone wrong wherever the user was.
    /// </summary>
    [Fact]
    public void The_overlay_opens_even_while_someone_is_typing()
    {
        ShellShortcut? typing = ShellShortcuts.Find(
            Windows.System.VirtualKey.D,
            Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
            typing: true);

        typing!.Value.Command.Should().Be(ShellCommand.Diagnostics);
        ShellShortcuts.All.Where(s => s.WhileTyping).Should().ContainSingle("it is the exception, not the rule");
    }
}
#pragma warning restore CA1001
