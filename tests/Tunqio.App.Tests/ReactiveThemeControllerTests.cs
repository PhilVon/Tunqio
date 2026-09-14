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

        /// <summary>
        /// The harmonic ratio every published frame carries, named rather than buried in the constructor call (T-178).
        /// It drives saturation, which nothing in this file asserts on: these are tests of the controller's wiring -
        /// when it ticks, what it reaches, what stops it - and of the one luminance ordering that the theme's
        /// lightness decides. A value music has, inside the 0.88..0.99 band, so a palette here is a colour.
        /// </summary>
        public const float HarmonicRatio = 0.93f;

        public void Publish(uint sequence, float centroidHz = 4000f, float rms = 0.25f) =>
            Latest = new AnalysisFrame(sequence, 0, 0, Empty, Empty, rms, rms, centroidHz, HarmonicRatio, Empty, false, 0);

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

        /// <summary>Settable, because "the renderer went away under a running theming" is a case (T-156).</summary>
        public bool IsAttached { get; set; } = true;

        public bool HasAudioSource { get; set; } = true;

        /// <summary>What the next <see cref="SetThemeColors"/> throws, if anything. The detach race, and worse.</summary>
        public Exception? Refuses { get; set; }

        public IReadOnlyList<PresetInfo> Presets => [];

        public string? ActivePresetId => "spectrum-bars";

        public IObservable<RenderStats> Stats => Observable.Empty<RenderStats>();

#pragma warning disable CS0067 // Nothing switches a preset under this fake; the theme is what is under test.
        public event EventHandler<string>? PresetChanged;
#pragma warning restore CS0067

        public Task AttachAsync(nint swapChainPanelNative, nint audioEngineNative, RendererConfig config)
        {
            HasAudioSource = audioEngineNative != nint.Zero;
            return Task.CompletedTask;
        }

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

        public void SetThemeColors(ThemeColors colors)
        {
            if (Refuses is { } problem)
            {
                throw problem;
            }

            Themes.Add(colors);
        }

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

    /// <summary>
    /// The order the app actually has, which the test above does not use (E6-S3). Settings &gt; Appearance writes
    /// <c>ui.theme</c> and this controller hears it at once, while the window still has the old theme; the window repaints
    /// on its next dispatcher turn, and only then does <c>ActualThemeChanged</c> flip the dark flag and call
    /// <see cref="ReactiveThemeController.Evaluate"/>. Written while diagnosing T-69's "controls bar stays light" and it
    /// passed on the code as it was, which ruled this controller out; kept so the order stays covered.
    /// </summary>
    [Fact]
    public void A_theme_that_is_applied_after_its_setting_changed_is_still_followed()
    {
        using var h = new Harness().Build();
        h.Run(60);
        ReactiveThemePalette inDark = h.Sink.Applied[^1];

        // The setting first, with the window still dark...
        h.Settings.SetValue(SettingsKeys.UiTheme, "light");
        // ...then the repaint, and what the window's ActualThemeChanged handler does.
        h.Dark = false;
        h.Controller.Evaluate();
        h.Run(60, fromSequence: 500);

        ReactiveThemePalette after = h.Sink.Applied[^1];
        after.Primary.Luminance.Should().BeGreaterThan(
            inDark.Primary.Luminance, "the light theme's palette is lighter, and the window is light now");
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

        /// <summary>False builds the controller with no visualizer at all - the shape the shell had before T-156.</summary>
        public bool WithRenderer { get; init; } = true;

        public Harness Build()
        {
            Controller = new ReactiveThemeController(
                Frames, Settings, Accessibility, Sink, () => Dark, WithRenderer ? Renderer : null, Clock);
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

    // ---- T-156: the palette reaching the presets, and what happens when it cannot -------------------------------

    /// <summary>
    /// The argument that was never passed. Before T-156 the shell built this controller with five arguments, so
    /// <c>mp_renderer_set_theme</c> had no caller in the running app and the sixteen floats E4-S6 added to every
    /// preset's <c>b0</c> carried nothing. The count is what the diagnostics overlay shows.
    /// </summary>
    [Fact]
    public void Every_painted_palette_reaches_the_presets_and_is_counted()
    {
        using var h = new Harness().Build();

        h.Run(60);

        h.Controller.RendererPushes.Should().Be(60);
        h.Controller.RendererSkips.Should().Be(0);
        h.Controller.RendererProblem.Should().BeNull();
        // The same colours, not a second mapping: one palette painted twice.
        h.Renderer.Themes.Should().HaveCount(60);
        h.Renderer.Themes[^1].Primary.Should().Equal(h.Sink.Applied[^1].ToThemeColors().Primary);
    }

    /// <summary>
    /// AC-298. A renderer that never came up is the shipped case on a machine without a GPU, and it must cost
    /// the theming nothing: the window's own gradient keeps moving and nothing throws off the timer thread.
    /// </summary>
    [Fact]
    public void A_detached_renderer_costs_the_theming_nothing()
    {
        using var h = new Harness().Build();
        h.Renderer.IsAttached = false;

        h.Run(60);

        h.Sink.Applied.Should().HaveCount(60, "the window's gradient does not depend on the visualizer");
        h.Renderer.Themes.Should().BeEmpty();
        h.Controller.RendererSkips.Should().Be(60);
        h.Controller.RendererPushes.Should().Be(0);
        h.Controller.RendererProblem.Should().Be("the renderer is not attached");
        h.Controller.TickFailure.Should().BeNull("nothing here is a fault");
    }

    /// <summary>
    /// The race the guard alone cannot cover: attached when checked, gone by the time the call lands, which is
    /// what closing the window does. <c>NativeException</c> is an <c>InvalidOperationException</c>, so a core
    /// that refuses the call arrives here too.
    /// </summary>
    [Fact]
    public void A_renderer_that_goes_away_mid_call_is_caught_rather_than_thrown_at_thirty_hertz()
    {
        using var h = new Harness().Build();
        h.Renderer.Refuses = new InvalidOperationException("the visualization host is not attached");

        FluentActions.Invoking(() => h.Run(60)).Should().NotThrow();

        h.Sink.Applied.Should().HaveCount(60);
        h.Controller.RendererSkips.Should().Be(60);
        h.Controller.RendererProblem.Should().Be("the visualization host is not attached");
    }

    /// <summary>A renderer that comes back is used again, because detachment is transient and not terminal.</summary>
    [Fact]
    public void A_renderer_that_comes_back_is_told_again()
    {
        using var h = new Harness().Build();
        h.Renderer.IsAttached = false;
        h.Run(10);

        h.Renderer.IsAttached = true;
        h.Run(10, fromSequence: 11);

        h.Controller.RendererPushes.Should().Be(10);
        h.Controller.RendererSkips.Should().Be(10);
        h.Controller.RendererProblem.Should().BeNull("the visualizer is being told again");
    }

    /// <summary>
    /// A renderer with no theming to give it. The controller is built without one by every test that does not
    /// name it, and the shell built it that way for the whole of E4-S6's life.
    /// </summary>
    [Fact]
    public void No_renderer_at_all_is_the_same_shape_as_a_detached_one()
    {
        using var h = new Harness { WithRenderer = false }.Build();

        h.Run(30);

        h.Sink.Applied.Should().HaveCount(30);
        h.Controller.RendererProblem.Should().Be("no renderer was passed");
        h.Controller.RendererSkips.Should().Be(30);
    }

    /// <summary>
    /// A theming that never starts still tells the visualizer once, and the readout must not assert a renderer
    /// state nobody has looked at. It said "the renderer is not attached" beside a Renderer section reporting
    /// 144 fps on the first live run of the harness, which is the readout being wrong in exactly the way T-155
    /// exists to stop.
    /// </summary>
    [Fact]
    public void A_theming_that_never_runs_still_puts_the_static_theme_on_the_presets()
    {
        using var h = new Harness().Build();
        h.Accessibility.Set(animations: false, highContrast: false);

        h.Run(30);

        h.Controller.Active.Should().BeFalse();
        h.Controller.RendererPushes.Should().Be(1, "stopping is said once, not thirty times");
        h.Controller.RendererSkips.Should().Be(0);
        h.Controller.RendererProblem.Should().BeNull();
    }

    /// <summary>The same, with no renderer to tell: the reason is what nobody has looked at, not a claim.</summary>
    [Fact]
    public void With_no_renderer_the_readout_says_that_rather_than_guessing_at_one()
    {
        using var h = new Harness { WithRenderer = false }.Build();
        h.Accessibility.Set(animations: false, highContrast: false);

        h.Run(30);

        h.Controller.RendererProblem.Should().Be("no renderer was passed");
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
        // One push, and it is the resting palette: see Stopping_tells_the_visualizer_to_stop_too.
        h.Renderer.Themes.Should().HaveCount(1);
    }

    /// <summary>
    /// AC-297. The renderer holds the theme across a preset switch by design, so a theming that just stops
    /// talking leaves the presets on the last chord for ever - the window back to its static colours and the
    /// visualizer inside it frozen, which is precisely the two-palettes-in-one-window state this class exists
    /// to prevent. Stopping has to be said, not merely stopped saying.
    /// </summary>
    [Fact]
    public void Stopping_tells_the_visualizer_to_stop_too()
    {
        using var h = new Harness().Build();
        h.Run(30);
        h.Renderer.Themes.Clear();

        h.Settings.SetValue(SettingsKeys.UiReactiveTheming, false);

        h.Sink.Cleared.Should().BeGreaterThan(0);
        h.Renderer.Themes.Should().HaveCount(1, "the presets are told once, and it is not the last chord");
        // Resting is the engine's own "what off looks like", which is what the window has gone back to: one
        // palette across the whole window, in the stopped state as well as the running one.
        ThemeColors rest = new ReactiveThemeEngine(h.Dark, ReactiveThemeOptions.Read(h.Settings)).Resting.ToThemeColors();
        h.Renderer.Themes[0].Primary.Should().Equal(rest.Primary);
        h.Renderer.Themes[0].Background.Should().Equal(rest.Background);
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
