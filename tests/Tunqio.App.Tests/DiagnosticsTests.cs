using Tunqio.App.Shell;
using Tunqio.Core.Audio;
using Tunqio.Core.Library;
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

    private static RenderStats Frames(
        double fps = 144.0,
        long missed = 3,
        QualityPolicy policy = QualityPolicy.Auto,
        QualityTier tier = QualityTier.High,
        int qualityChanges = 0,
        int renderWidth = 854,
        int renderHeight = 720,
        float renderScale = 1f,
        double frameCostMs = 4.1,
        RenderCostSource costSource = RenderCostSource.GpuTimestamp) => new(
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
        Adapter: "NVIDIA GeForce RTX 4080 SUPER",
        Policy: policy,
        Tier: tier,
        QualityChanges: qualityChanges,
        RenderWidth: renderWidth,
        RenderHeight: renderHeight,
        RenderScale: renderScale,
        FrameCost: TimeSpan.FromMilliseconds(frameCostMs),
        CostSource: costSource);

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

        sections.Select(s => s.Title).Should().Equal("Playback", "Output", "Renderer", "Reactive theming", "Build");
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

    /// <summary>
    /// AC-128's second half: the tier the renderer dropped to has to be visible to the person watching it drop.
    /// Checked here rather than off a screen because a number formatted inside a TextBlock can only be checked
    /// by reading it, and this is the function that formats it.
    /// </summary>
    [Fact]
    public void The_overlay_says_which_quality_tier_is_drawing_and_why()
    {
        IReadOnlyList<DiagnosticsSection> full = Diagnostics.Describe(null, null, Frames());
        Value(full, "Renderer", "Quality").Should().Be("high (auto) · 854×720 at 1× · 0 changes");
        Value(full, "Renderer", "Frame cost").Should().Be("4.10 ms (GPU timestamp)");

        // A renderer that WARP has driven down to Low: the tier, the rectangle it is really drawing, and how
        // many times the controller has changed its mind - which is the half of "without oscillating" that a
        // person can see.
        IReadOnlyList<DiagnosticsSection> dropped = Diagnostics.Describe(null, null, Frames(
            tier: QualityTier.Low, qualityChanges: 2, renderWidth: 427, renderHeight: 360, renderScale: 0.5f,
            frameCostMs: 21.35));
        Value(dropped, "Renderer", "Quality").Should().Be("low (auto) · 427×360 at 0.5× · 2 changes");
        Value(dropped, "Renderer", "Frame cost").Should().Be("21.35 ms (GPU timestamp)");

        // A tier the user chose reads as their choice rather than as the renderer's judgement, and a cost the
        // controller had to fall back to measuring by frame interval says so, because the two numbers do not
        // mean the same thing.
        IReadOnlyList<DiagnosticsSection> pinned = Diagnostics.Describe(null, null, Frames(
            policy: QualityPolicy.Medium, tier: QualityTier.Medium, qualityChanges: 1, renderWidth: 641,
            renderHeight: 540, renderScale: 0.75f, costSource: RenderCostSource.FrameInterval));
        Value(pinned, "Renderer", "Quality").Should().Be("medium (set to medium) · 641×540 at 0.75× · 1 change");
        Value(pinned, "Renderer", "Frame cost").Should().Be("4.10 ms (frame interval)");
    }

    /// <summary>
    /// T-179: the one renderer failure that looks exactly like success has to be reported in words.
    /// </summary>
    /// <remarks>
    /// A renderer with no engine cannot see a single note, but every shipped preset answers "nothing is
    /// playing" with an idle animation - a smooth travelling sine - so the panel goes on looking like a working
    /// visualizer. That is how it survived nine stories: reviewers, including the ones who went looking, saw a
    /// moving picture and read the movement as reaction. Nothing in the overlay distinguished the two states,
    /// so this row does.
    /// </remarks>
    [Fact]
    public void A_visualizer_with_no_engine_behind_it_says_so_rather_than_looking_like_one_that_works()
    {
        Value(Diagnostics.Describe(null, null, Frames(), rendererHasAudioSource: true), "Renderer", "Audio source")
            .Should().Be("engine attached");
        Value(Diagnostics.Describe(null, null, Frames(), rendererHasAudioSource: false), "Renderer", "Audio source")
            .Should().Be("NONE - every preset is drawing its idle animation");
        Value(Diagnostics.Describe(null, null, Frames()), "Renderer", "Audio source")
            .Should().Be("unknown", "nobody has said, which is not the same as no");
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
        Value(sections, "Reactive theming", "Theming").Should().Be("not started (no analysis stream)");
        Value(sections, "Build", "Version").Should().Be("unknown");
    }

    [Fact]
    public void A_renderer_that_would_not_start_says_why_rather_than_only_that_it_did_not()
    {
        IReadOnlyList<DiagnosticsSection> sections = Diagnostics.Describe(
            null, null, null, "0.1.0", "unavailable: mpcore.dll not found");

        Value(sections, "Renderer", "Renderer").Should().Be("unavailable: mpcore.dll not found");
    }

    // ---- T-155: the reactive theming readout ------------------------------------------------------------------------

    /// <summary>The palette AC-266 could not see, as four named hex triples a person can watch change.</summary>
    private static ReactiveThemeStatus Theming(
        bool active = true,
        string? stoppedBecause = null,
        long rendererPushes = 900,
        long rendererSkips = 0,
        string? rendererProblem = null,
        bool painted = true,
        string? artHash = "aaaa1111deadbeef") =>
        new(
            active,
            stoppedBecause,
            painted
                ? new ReactiveThemePalette(
                    new Srgb(0x16, 0x32, 0x4f),
                    new Srgb(0x2a, 0x4a, 0x6e),
                    new Srgb(0x6f, 0x9a, 0xd6),
                    new Srgb(0x0c, 0x1a, 0x2b))
                : null,
            Ticks: 900,
            Applied: 900,
            rendererPushes,
            rendererSkips,
            rendererProblem,
            artHash,
            artHash is null
                ? null
                : [new PaletteColor(40, 90, 160, 0.6, PaletteColor.RelativeLuminance(40, 90, 160))]);

    [Fact]
    public void A_running_theming_says_so_and_names_the_colours_it_is_painting()
    {
        IReadOnlyList<DiagnosticsSection> sections = Diagnostics.Describe(null, null, null, theming: Theming());

        Value(sections, "Reactive theming", "State").Should().Be("running");
        Value(sections, "Reactive theming", "Palette")
            .Should().Be("primary #16324f · secondary #2a4a6e · accent #6f9ad6 · background #0c1a2b");
        Value(sections, "Reactive theming", "Ticks").Should().Be("900 · 900 painted");
        Value(sections, "Reactive theming", "Visualizer").Should().Be("900 palettes sent");
        Value(sections, "Reactive theming", "Album art").Should().Be("aaaa1111 · #285aa0");
    }

    /// <summary>AC-286, and the observation AC-288 asks a person to make: the reason is on the readout.</summary>
    [Fact]
    public void A_stopped_theming_gives_the_reason_and_shows_the_static_theme_back()
    {
        IReadOnlyList<DiagnosticsSection> sections = Diagnostics.Describe(
            null, null, null,
            theming: Theming(active: false, stoppedBecause: "Windows is asking for reduced motion", painted: false));

        Value(sections, "Reactive theming", "State").Should().Be("stopped: Windows is asking for reduced motion");
        Value(sections, "Reactive theming", "Palette").Should().Be("none (the static theme is showing)");
    }

    /// <summary>
    /// The row that would have answered AC-266 before T-156 existed. It is the difference between "the theming is
    /// running" and "the theming is reaching the thing you are looking at", which is the whole of why the check
    /// could not be made: the window's gradient moved and the visualizer inside it did not.
    /// </summary>
    [Fact]
    public void A_theme_that_is_not_reaching_the_visualizer_says_so_rather_than_only_that_it_is_running()
    {
        IReadOnlyList<DiagnosticsSection> sections = Diagnostics.Describe(
            null, null, null,
            theming: Theming(rendererPushes: 0, rendererSkips: 900, rendererProblem: "no renderer was passed"));

        Value(sections, "Reactive theming", "State").Should().Be("running");
        Value(sections, "Reactive theming", "Visualizer")
            .Should().Be("not told: no renderer was passed (900 skipped, 0 sent)");
    }

    [Fact]
    public void A_track_with_no_art_says_so_rather_than_showing_the_last_album_s_colours()
    {
        IReadOnlyList<DiagnosticsSection> sections =
            Diagnostics.Describe(null, null, null, theming: Theming(artHash: null));

        Value(sections, "Reactive theming", "Album art").Should().Be("none (this track has no art)");
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

    /// <summary>
    /// T-159. The refresh runs from a timer and the engine's statistics are a native read that throws on a refusal or
    /// a closed handle. On the UI thread an exception out of it reaches the XAML handler, which does not recover.
    /// </summary>
    [Fact]
    public void An_engine_whose_statistics_throw_does_not_take_the_overlay_with_it()
    {
        _engine.StatsFault = new InvalidOperationException("mp_engine_get_stats refused");
        using var vm = new DiagnosticsViewModel(_source, () => Frames(), clock: _clock);

        vm.Toggle();
        _clock.Advance(DiagnosticsViewModel.RefreshInterval);

        Value(vm.Sections, "Output", "Engine").Should().Be("unavailable");
        Value(vm.Sections, "Renderer", "Adapter").Should().Be("NVIDIA GeForce RTX 4080 SUPER", "one failed read does not blank the others");
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
