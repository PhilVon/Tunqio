using System.Reactive.Linq;
using FluentAssertions;
using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Core.Visualization;
using Xunit.Abstractions;

namespace Tunqio.App.Tests;

/// <summary>
/// AC-125's off switch and AC-127's stop (E4-S6). The colours themselves are <c>ReactiveThemeEngineTests</c>'
/// job; what is tested here is the decision - when the theming runs, when it stops, and how long the stopping
/// takes measured on a clock the test owns rather than slept at.
/// </summary>
/// <remarks>
/// <b>What cannot be tested, and why.</b> Nothing here toggles Windows' own "Show animations" setting. Driving
/// it would mean writing a machine-wide user setting from a unit test - changing the environment under whoever
/// is at the keyboard and under every other UI test in the run - and restoring it is not guaranteed if the
/// process is killed mid-test. So the OS-to-signal leg is left to a human, and everything from the signal
/// onwards is exercised here, including the case the plumbing gets wrong: the switch thrown while nothing is
/// playing, when there is no next frame to notice it on.
/// </remarks>
public class ReactiveThemeControllerTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>A clock the test moves by hand, so an elapsed time is a measurement and never a wait.</summary>
    private sealed class TestClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private sealed class FakeFrames : IAnalysisFrameSource
    {
        private static readonly float[] Empty = [];

        public AnalysisFrame? Latest { get; set; }

        /// <summary>Never pushes: the controller polls, because a push stops arriving when the music does.</summary>
        public IObservable<AnalysisFrame> Frames => Observable.Empty<AnalysisFrame>();

        public bool TryGetLatest(out AnalysisFrame frame)
        {
            frame = Latest ?? default;
            return Latest is not null;
        }

        public void Publish(uint sequence, float centroidHz = 4000f, float rms = 0.25f) =>
            Latest = new AnalysisFrame(sequence, 0, 0, Empty, Empty, rms, rms, centroidHz, 0.8f, Empty, false, 0);

        /// <summary>A frame that is different from the last one, so "it follows the music" has music to follow.</summary>
        public void PublishMoving(uint sequence) =>
            Publish(sequence, 200f + (sequence * 130f), 0.05f + (((sequence * 7) % 40) / 100f));
    }

    private sealed class FakeAccessibility : IAccessibilitySignals
    {
        public event EventHandler? Changed;

        public bool AnimationsEnabled { get; set; } = true;

        public bool HighContrast { get; set; }

        /// <summary>What Windows does when the user throws the switch: change the value and say so.</summary>
        public void Set(bool animations, bool highContrast)
        {
            AnimationsEnabled = animations;
            HighContrast = highContrast;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>The same change with the notification lost, which is what the poll is the bound for.</summary>
        public void SetSilently(bool animations, bool highContrast)
        {
            AnimationsEnabled = animations;
            HighContrast = highContrast;
        }
    }

    private sealed class RecordingSink : IReactiveThemeSink
    {
        public List<ReactiveThemePalette> Applied { get; } = [];

        public int Cleared { get; private set; }

        public void Apply(ReactiveThemePalette palette) => Applied.Add(palette);

        public void Clear() => Cleared++;
    }

    private sealed class RecordingRenderer : IVisualizationHost
    {
        public List<ThemeColors> Themes { get; } = [];

        public bool IsAttached => true;

        public IReadOnlyList<PresetInfo> Presets => [];

        public string? ActivePresetId => "spectrum-bars";

        public IObservable<RenderStats> Stats => Observable.Empty<RenderStats>();

        public Task AttachAsync(nint swapChainPanelNative, RendererConfig config) => Task.CompletedTask;

        public void Detach()
        {
        }

        public void Dispose()
        {
        }

        public void Resize(int width, int height, float scaleX, float scaleY)
        {
        }

        public void SetParameter(string name, float value)
        {
        }

        public Task SetPresetAsync(string id) => Task.CompletedTask;

        public QualityPolicy Quality { get; private set; } = QualityPolicy.Auto;

        public void SetQualityPolicy(QualityPolicy policy) => Quality = policy;

        public void SetThemeColors(ThemeColors colors) => Themes.Add(colors);

        public IReadOnlyList<PresetParameter> GetPresetParameters(string presetId) => [];

        public void SetUserPresetRoot(string path)
        {
        }

        public IReadOnlyList<PresetInfo> RefreshPresets() => Presets;

        public RenderStats? TryGetStats() => null;

        public void SetVisible(bool visible)
        {
        }
    }

    private sealed class Harness : IDisposable
    {
        public TestClock Clock { get; } = new();

        public FakeFrames Frames { get; } = new();

        public FakeSettings Settings { get; } = new();

        public FakeAccessibility Accessibility { get; } = new();

        public RecordingSink Sink { get; } = new();

        public RecordingRenderer Renderer { get; } = new();

        public ReactiveThemeController Controller { get; private set; } = null!;

        public bool Dark { get; set; } = true;

        public Harness Build()
        {
            Controller = new ReactiveThemeController(
                Frames, Settings, Accessibility, Sink, () => Dark, Renderer, Clock);
            Controller.Evaluate();
            return this;
        }

        /// <summary>n polls, each one <see cref="ReactiveThemeController.PollInterval"/> after the last.</summary>
        public void Run(int ticks, uint fromSequence = 1)
        {
            for (int i = 0; i < ticks; i++)
            {
                Clock.Advance(ReactiveThemeController.PollInterval);
                Frames.PublishMoving(fromSequence + (uint)i);
                Controller.Tick();
            }
        }

        public void Dispose() => Controller.Dispose();
    }

    [Fact]
    public void While_everything_allows_it_the_gradient_follows_the_frames()
    {
        using var h = new Harness().Build();

        h.Run(60);

        h.Controller.Active.Should().BeTrue();
        h.Controller.StoppedBecause.Should().BeNull();
        h.Sink.Applied.Should().HaveCount(60);
        h.Sink.Applied.Distinct().Should().HaveCountGreaterThan(1,
            "a gradient that paints the same colour sixty times is not following anything");
        h.Renderer.Themes.Should().HaveCount(60, "the presets are painted from the same palette as the window");
    }

    [Fact]
    public void Turning_the_setting_off_stops_it_and_puts_the_theme_back()
    {
        using var h = new Harness().Build();
        h.Run(30);

        h.Settings.SetValue(SettingsKeys.UiReactiveTheming, false);

        h.Controller.Active.Should().BeFalse();
        h.Controller.StoppedBecause.Should().Be("ui.reactiveTheming is off");
        h.Sink.Cleared.Should().Be(1);

        int painted = h.Sink.Applied.Count;
        h.Run(30);
        h.Sink.Applied.Should().HaveCount(painted, "off is off, and a tick while off paints nothing");

        h.Settings.SetValue(SettingsKeys.UiReactiveTheming, true);
        h.Run(5);
        h.Sink.Applied.Count.Should().BeGreaterThan(painted, "and turning it back on starts it again");
    }

    [Fact]
    public void Changing_the_smoothing_setting_is_picked_up_without_a_restart()
    {
        using var h = new Harness().Build();
        h.Run(10);
        h.Controller.Options.Smoothing.Should().Be(SettingsKeys.Defaults.UiReactiveSmoothing);

        h.Settings.SetValue(SettingsKeys.UiReactiveSmoothing, 0.9f);

        h.Controller.Options.Smoothing.Should().Be(0.9f);
        h.Controller.Active.Should().BeTrue();
    }

    [Theory]
    [InlineData(false, false, "Windows is asking for reduced motion")]
    [InlineData(true, true, "a high-contrast theme is in force")]
    public void The_switch_stops_the_theming_with_no_frame_to_notice_it_on(bool animations, bool highContrast, string because)
    {
        using var h = new Harness().Build();
        h.Run(30);
        h.Controller.Active.Should().BeTrue();
        long thrown = h.Clock.GetTimestamp();

        // Deliberately nothing after this: no tick, no frame. A player that is paused publishes nothing, and a
        // theming that only re-read the switches when a frame arrived would sit on the last chord's colour for
        // as long as the music stayed paused - which is not "within one second".
        h.Accessibility.Set(animations, highContrast);

        h.Controller.Active.Should().BeFalse();
        h.Controller.StoppedBecause.Should().Be(because);
        h.Sink.Cleared.Should().Be(1);
        h.Controller.StoppedAt.Should().NotBeNull();
        TimeSpan took = h.Clock.GetElapsedTime(thrown, h.Controller.StoppedAt!.Value);
        _output.WriteLine($"{because}: theming stopped {took.TotalMilliseconds:F3} ms after the switch was thrown");
        took.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1), "AC-127");
    }

    [Fact]
    public void A_switch_whose_notification_never_arrives_still_stops_it_inside_one_poll()
    {
        // The bound rather than the fast path. UISettings raises a change event, but an event is a thing that can
        // be missed, marshalled late or arrive after a window has gone; the properties are re-read every tick so
        // the worst case is one poll interval and not "until Windows tells us again".
        using var h = new Harness().Build();
        h.Run(30);
        long thrown = h.Clock.GetTimestamp();

        h.Accessibility.SetSilently(animations: false, highContrast: false);
        h.Controller.Active.Should().BeTrue("no notification has been raised yet");

        h.Run(1, fromSequence: 100);

        h.Controller.Active.Should().BeFalse();
        TimeSpan took = h.Clock.GetElapsedTime(thrown, h.Controller.StoppedAt!.Value);
        _output.WriteLine($"with the notification lost, the poll stopped it after {took.TotalMilliseconds:F3} ms "
                          + $"(one poll interval is {ReactiveThemeController.PollInterval.TotalMilliseconds:F3} ms)");
        took.Should().BeLessThanOrEqualTo(ReactiveThemeController.PollInterval);
        took.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1), "AC-127");
    }

    [Fact]
    public void The_poll_interval_is_the_thirty_hertz_the_analysis_publishes_at()
    {
        ReactiveThemeController.PollInterval.Should().Be(TimeSpan.FromMilliseconds(1000.0 / 30.0));
        ReactiveThemeController.PollInterval.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "the poll is what bounds AC-127 when a change notification goes missing");
    }

    [Fact]
    public void Reduced_motion_wins_over_the_setting_being_on()
    {
        using var h = new Harness().Build();
        h.Accessibility.Set(animations: false, highContrast: false);

        h.Run(30);

        h.Controller.Active.Should().BeFalse();
        h.Sink.Applied.Should().BeEmpty("the user's accessibility choice is not a preference this can outvote");
        h.Renderer.Themes.Should().BeEmpty();
    }

    [Fact]
    public void Coming_back_from_a_stop_seeds_from_the_music_rather_than_sweeping_up_from_rest()
    {
        // Restarting resets the engine, and its first frame is its starting colour rather than something it
        // smooths toward: a sweep up from grey would be a run of colours nobody played, on a layer that was
        // invisible a moment ago, and it would happen every time the user turned the feature back on. The step
        // is bounded anyway - the contrast guarantee holds at every colour either side of it.
        using var h = new Harness().Build();
        h.Run(60);
        ReactiveThemePalette playing = h.Sink.Applied[^1];
        h.Controller.Active.Should().BeTrue();

        h.Accessibility.Set(animations: false, highContrast: false);
        h.Sink.Cleared.Should().Be(1);
        h.Accessibility.Set(animations: true, highContrast: false);
        h.Clock.Advance(ReactiveThemeController.PollInterval);
        h.Controller.Tick(); // the same frame the run above ended on; nothing new has been published

        var fresh = new ReactiveThemeEngine(true, ReactiveThemeOptions.Default);
        ReactiveThemePalette seeded = fresh.Advance(h.Frames.Latest, ReactiveThemeController.PollInterval);
        h.Sink.Applied[^1].Should().Be(seeded, "resuming is a fresh engine taking this frame as its starting colour");
        h.Sink.Applied[^1].Should().NotBe(playing,
            "the colour it stopped on was sixty frames of smoothing behind the music, and that lag is not resumed");
    }

    [Fact]
    public void A_theme_change_rebuilds_the_engine_against_the_other_theme_s_text()
    {
        using var h = new Harness().Build();
        h.Run(60);
        h.Controller.Options.Should().Be(ReactiveThemeOptions.Default);
        ReactiveThemePalette inDark = h.Sink.Applied[^1];

        h.Dark = false;
        h.Settings.SetValue(SettingsKeys.UiTheme, "light");
        h.Run(60, fromSequence: 500);

        ReactiveThemePalette inLight = h.Sink.Applied[^1];
        _output.WriteLine($"dark {inDark.Primary.Hex} (L {inDark.Primary.Luminance:F4}), "
                          + $"light {inLight.Primary.Hex} (L {inLight.Primary.Luminance:F4})");
        inLight.Primary.Luminance.Should().BeGreaterThan(inDark.Primary.Luminance);
        foreach (Srgb colour in inLight.All)
        {
            foreach (Srgb foreground in ReactiveTheming.LightForegrounds)
            {
                ReactiveContrast.Ratio(colour, foreground).Should().BeGreaterThanOrEqualTo(ReactiveContrast.TextMinimum);
            }
        }
    }

    [Fact]
    public void Disposing_puts_the_theme_back_and_stops_answering()
    {
        var h = new Harness().Build();
        h.Run(30);

        h.Controller.Dispose();

        h.Sink.Cleared.Should().Be(1);
        int painted = h.Sink.Applied.Count;
        h.Run(10);
        h.Sink.Applied.Should().HaveCount(painted);
        h.Accessibility.Set(animations: false, highContrast: false);
        h.Sink.Cleared.Should().Be(1, "a disposed controller has nothing left to clear");
    }
}
