using System.Globalization;
using FluentAssertions;
using Tunqio.Core.Library;
using Tunqio.Core.Visualization;
using Xunit.Abstractions;

namespace Tunqio.Core.Tests;

/// <summary>
/// AC-125: the background gradient follows the music with the configured smoothing, and can be turned off
/// (E4-S6). The mapping and the smoothing are a pure function of the frames and the time between them, so they
/// are tested as one - synthetic frames in, colours out, no window anywhere.
/// </summary>
public class ReactiveThemeEngineTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static readonly float[] NoSpectrum = [];

    /// <summary>The settings the smoothing tests walk: nothing, the default, half and all of it.</summary>
    private static readonly float[] SmoothingSettings = [0.0f, 0.15f, 0.5f, 1.0f];

    /// <summary>
    /// A harmonic ratio real music has: the middle of the 0.879..0.994 band T-176 measured over the whole library,
    /// and inside the 0.88..0.99 band saturation is mapped over. For a property the harmonic ratio does not drive,
    /// held equal at both ends of a step so that saturation cannot move and the property is measured on its own.
    /// </summary>
    private const float TypicalHarmonic = 0.93f;

    /// <summary>The top of the band: the most saturated colour the mapping emits, and so the most to drain.</summary>
    private const float MostHarmonic = 1.0f;

    /// <summary>
    /// A frame carrying only the three things the mapping reads, plus the restart counter. The harmonic ratio has
    /// no default (T-178): it drives saturation, and in the light theme saturation is the axis that moves the field,
    /// so a default here once held it still at both ends of the flash test and hid a WCAG 2.3.1 breach. Every
    /// call site says what it holds.
    /// </summary>
    private static AnalysisFrame Frame(float centroidHz, float rms, float harmonicRatio,
                                       byte discontinuities = 0, uint sequence = 1) =>
        new(sequence, 0, 0, NoSpectrum, NoSpectrum, rms, rms, centroidHz, harmonicRatio, NoSpectrum, false,
            discontinuities);

    private static ReactiveThemeEngine Engine(float smoothing = 0.15f, bool enabled = true, bool dark = true) =>
        new(dark, new ReactiveThemeOptions(enabled, smoothing));

    private static double Lightness(Srgb c) => ReactiveTheming.ToHsl(c).Lightness;

    // Hue distance the short way round, which is the only one that means anything on a circle.
    private static double HueGap(Srgb a, Srgb b)
    {
        double d = Math.Abs(ReactiveTheming.ToHsl(a).Hue - ReactiveTheming.ToHsl(b).Hue) % 360.0;
        return d > 180.0 ? 360.0 - d : d;
    }

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1.0 / 30.0);

    private static ReactiveThemePalette Settle(ReactiveThemeEngine engine, AnalysisFrame? frame, TimeSpan over)
    {
        ReactiveThemePalette palette = engine.Palette;
        for (TimeSpan t = TimeSpan.Zero; t < over; t += Tick)
        {
            palette = engine.Advance(frame, Tick);
        }

        return palette;
    }

    [Fact]
    public void A_bright_track_and_a_dark_one_are_not_the_same_colour()
    {
        // The spectral centroid is what moves the hue, and these are two ends of the range the mapping covers.
        // At a harmonic ratio music has rather than below the band: a hue is only as legible as the chroma under
        // it, and the floor saturation a sub-band ratio clamps to is the least colour there is to read one from.
        Srgb bass = Settle(Engine(), Frame(centroidHz: 90f, rms: 0.2f, TypicalHarmonic), TimeSpan.FromSeconds(8)).Primary;
        Srgb treble = Settle(Engine(), Frame(centroidHz: 9000f, rms: 0.2f, TypicalHarmonic), TimeSpan.FromSeconds(8)).Primary;

        double gap = HueGap(bass, treble);
        _output.WriteLine($"90 Hz centroid -> {bass.Hex}, 9 kHz -> {treble.Hex}, {gap:F1} degrees apart");
        gap.Should().BeGreaterThan(60.0, "a hue sweep nobody can see is not the theme following the music");
        bass.Should().NotBe(treble);
    }

    [Fact]
    public void Louder_is_lighter_in_a_dark_theme_and_the_colours_stay_ordered()
    {
        // The harmonic ratio is held equal at both ends, so the only input that differs is the loudness.
        Srgb quiet = Settle(Engine(), Frame(1000f, 0.004f, TypicalHarmonic), TimeSpan.FromSeconds(8)).Primary;
        Srgb loud = Settle(Engine(), Frame(1000f, 0.4f, TypicalHarmonic), TimeSpan.FromSeconds(8)).Primary;

        _output.WriteLine($"rms 0.004 -> {quiet.Hex} (L {quiet.Luminance:F4}), 0.4 -> {loud.Hex} (L {loud.Luminance:F4})");
        loud.Luminance.Should().BeGreaterThan(quiet.Luminance);
    }

    [Fact]
    public void The_configured_smoothing_is_a_time_constant_and_it_is_the_one_measured()
    {
        // The EMA's defining property: one time constant covers 1 - 1/e of the distance. Measured against a
        // step in the target rather than asserted from the code, so a wrong alpha shows here. Lightness rather
        // than luminance, because lightness is what the mapping smooths: sRGB luminance is a curve over it, and
        // 63.2% of the way along a line is not 63.2% of the way along its image under a curve. The step is in
        // loudness alone, with the harmonic ratio held equal, because a saturation step at the same time would
        // be a second EMA feeding the same colour and the fraction read back would be of neither.
        var loud = Frame(100f, 0.3f, TypicalHarmonic);
        var quiet = Frame(100f, 0.004f, TypicalHarmonic);
        foreach (float smoothing in SmoothingSettings)
        {
            TimeSpan tau = new ReactiveThemeOptions(true, smoothing).TimeConstant;

            var settled = Engine(smoothing);
            Settle(settled, loud, TimeSpan.FromSeconds(40));
            double from = Lightness(settled.Palette.Primary);
            Settle(settled, quiet, TimeSpan.FromSeconds(120));
            double to = Lightness(settled.Palette.Primary);

            // One step of exactly tau, so the measurement is of the smoothing and not of the tick size.
            var measuring = Engine(smoothing);
            Settle(measuring, loud, TimeSpan.FromSeconds(40));
            measuring.Advance(quiet, tau);
            double after = Lightness(measuring.Palette.Primary);

            double covered = (from - after) / (from - to);
            _output.WriteLine($"smoothing {smoothing} -> tau {tau.TotalSeconds:F3} s; one tau covered "
                              + $"{covered:P1} of the step (lightness {from:F4} -> {to:F4})");
            covered.Should().BeApproximately(1.0 - (1.0 / Math.E), 0.02,
                $"smoothing {smoothing} must be an exponential time constant, not a per-tick blend");
        }
    }

    [Fact]
    public void More_smoothing_is_slower_and_that_is_an_ordering_not_a_claim()
    {
        // An ordering over smoothing settings, at one step: which step does not matter so long as it is the same
        // for every setting, so it is a loudness step at a harmonic ratio held equal.
        double[] reached = SmoothingSettings.Select(s =>
        {
            var engine = Engine(s);
            Settle(engine, Frame(100f, 0.3f, TypicalHarmonic), TimeSpan.FromSeconds(20));
            double from = engine.Palette.Primary.Luminance;
            Settle(engine, Frame(100f, 0.004f, TypicalHarmonic), TimeSpan.FromSeconds(0.5));
            return from - engine.Palette.Primary.Luminance;
        }).ToArray();

        _output.WriteLine("moved in 0.5 s: "
                          + string.Join(", ", reached.Select(r => r.ToString("F5", CultureInfo.InvariantCulture))));
        reached.Should().BeInDescendingOrder("a larger smoothing setting must move the colour less, not more");
    }

    [Fact]
    public void Ten_small_steps_land_where_one_large_one_does()
    {
        // The smoothing is a function of elapsed time and not of how many times it was called, which is what
        // lets AC-127's "within one second" be about seconds on a machine whose frame rate nobody controls. A
        // property of the time arithmetic, so one frame with every input in the range music has is enough.
        var fine = Engine();
        var coarse = Engine();
        var frame = Frame(6000f, 0.35f, TypicalHarmonic);
        fine.Advance(frame, TimeSpan.Zero);
        coarse.Advance(frame, TimeSpan.Zero);

        for (int i = 0; i < 100; i++)
        {
            fine.Advance(frame, TimeSpan.FromMilliseconds(10));
        }

        for (int i = 0; i < 10; i++)
        {
            coarse.Advance(frame, TimeSpan.FromMilliseconds(100));
        }

        foreach ((Srgb a, Srgb b) in fine.Palette.All.Zip(coarse.Palette.All))
        {
            Math.Abs(a.R - b.R).Should().BeLessThanOrEqualTo(2);
            Math.Abs(a.G - b.G).Should().BeLessThanOrEqualTo(2);
            Math.Abs(a.B - b.B).Should().BeLessThanOrEqualTo(2);
        }
    }

    [Fact]
    public void Turned_off_it_is_the_resting_palette_and_stays_there()
    {
        // At the most saturated input there is, so that a disabled engine that leaked any of it would show.
        var engine = Engine(enabled: false);
        ReactiveThemePalette resting = engine.Resting;

        ReactiveThemePalette after = Settle(engine, Frame(9000f, 0.5f, MostHarmonic), TimeSpan.FromSeconds(10));

        after.Should().Be(resting, "the off switch is off, not quieter");
        engine.FramesSeen.Should().Be(0, "a frame folded in while disabled is state that would show on re-enabling");
    }

    [Fact]
    public void With_nothing_playing_the_colour_drains_back_to_rest_rather_than_freezing()
    {
        // From the most saturated colour the mapping emits. The default this test used to inherit was below the
        // harmonic band, which clamps to the saturation floor: it drained from the least colour there is.
        var engine = Engine();
        Settle(engine, Frame(9000f, 0.5f, MostHarmonic), TimeSpan.FromSeconds(10));
        Srgb playing = engine.Palette.Primary;
        ReactiveTheming.ToHsl(playing).Saturation.Should().BeGreaterThan(0.5, "the drain has to start from a colour");

        ReactiveThemePalette stopped = Settle(engine, null, TimeSpan.FromSeconds(30));

        _output.WriteLine($"playing {playing.Hex}, 30 s after the frames stopped {stopped.Primary.Hex}, "
                          + $"resting {engine.Resting.Primary.Hex}");
        ReactiveTheming.ToHsl(stopped.Primary).Saturation.Should().BeLessThan(0.02,
            "a paused player must not keep the last chord's colour on the wall");
    }

    [Fact]
    public void A_moved_discontinuity_count_changes_no_colour_and_is_still_counted()
    {
        // The decision recorded on ReactiveThemeEngine: the target is a pure function of one frame and the only
        // thing carried across frames is the colour on screen, so there is no history a gap invalidates -
        // and throwing away the colour on screen would snap the background, which is what the smoothing exists
        // to prevent. The count is surfaced so the decision is visible rather than silent. The two engines see
        // identical inputs apart from the count, so the harmonic ratio is any value music has.
        var continuous = Engine();
        var interrupted = Engine();
        for (int i = 0; i < 300; i++)
        {
            continuous.Advance(Frame(2000f + (i * 10), 0.2f, TypicalHarmonic, discontinuities: 0, sequence: (uint)i + 1), Tick);
            interrupted.Advance(Frame(2000f + (i * 10), 0.2f, TypicalHarmonic, discontinuities: (byte)(i / 40), sequence: (uint)i + 1),
                                Tick);
        }

        interrupted.Palette.Should().Be(continuous.Palette);
        interrupted.AnalysisRestarts.Should().Be(7, "the count moved every 40 frames over 300");
        interrupted.Discontinuities.Should().Be(7);
        continuous.AnalysisRestarts.Should().Be(0);
    }

    [Fact]
    public void Every_colour_the_engine_can_produce_is_one_text_reads_on()
    {
        // The link between AC-126's exhaustive sweep and what the mapping actually emits: the sweep proves
        // Constrain safe for every colour, and this proves nothing leaves the engine without going through it.
        foreach (bool dark in new[] { true, false })
        {
            IReadOnlyList<Srgb> foregrounds = dark ? ReactiveTheming.DarkForegrounds : ReactiveTheming.LightForegrounds;
            var engine = new ReactiveThemeEngine(dark, new ReactiveThemeOptions(true, 0.0f));
            double worst = double.PositiveInfinity;
            for (int centroid = 20; centroid <= 20000; centroid += 37)
            {
                for (int loudness = 0; loudness <= 20; loudness++)
                {
                    for (int harmonic = 0; harmonic <= 4; harmonic++)
                    {
                        engine.Advance(Frame(centroid, loudness / 20f, harmonic / 4f), TimeSpan.FromSeconds(60));
                        foreach (Srgb colour in engine.Palette.All)
                        {
                            foreach (Srgb foreground in foregrounds)
                            {
                                worst = Math.Min(worst, ReactiveContrast.Ratio(colour, foreground));
                            }
                        }
                    }
                }
            }

            _output.WriteLine($"{(dark ? "dark" : "light")}: worst emitted colour reads at {worst:F4}:1");
            worst.Should().BeGreaterThanOrEqualTo(ReactiveContrast.TextMinimum);
        }
    }

    // The grid the flash search below walks: both ends and the interior of each of the mapping's three inputs,
    // including the two harmonic-ratio band edges T-176 introduced (0.88 and 0.99) and both sides of them.
    private static readonly float[] FlashCentroids = [0f, 20f, 300f, 1000f, 3000f, 8500f, 20000f];
    private static readonly float[] FlashRms = [0f, 0.001f, 0.01f, 0.1f, 1.0f];
    private static readonly float[] FlashHarmonics = [0f, 0.5f, 0.88f, 0.93f, 0.99f, 1.0f];

    [Fact]
    public void No_pair_of_states_the_mapping_can_reach_is_a_flash()
    {
        // WCAG 2.3.1 as the accessibility contract states it: no full-field luminance change above 3 Hz. This
        // searches instead of guessing: every ordered pair of reachable states, both themes, at the 0.5 s floor,
        // and it reports the worst pair it found rather than only that it found none.
        //
        // It replaced a test that picked one pair by hand - silence at 20 Hz to full scale at 20 kHz - and built
        // both ends with a helper that defaulted the harmonic ratio to 0.8. So it never stepped saturation, which
        // since T-176 is the light theme's dominant axis, and it reported 0.0559 while the light theme's true worst
        // pair stood at 0.1005 over the threshold (T-178). That pair is on this grid - 0.8 is below the band and
        // clamps to the same saturation as 0 and 0.5 - so deleting it lost no state.
        //
        // A single Advance on a fresh engine is a settled state by construction - alpha is 1 until seeded - so
        // "settle at A then jump to B" is two calls and the whole grid is cheap enough to be exhaustive over it.
        AnalysisFrame?[] states =
        [
            null, // paused: saturation and energy drain to zero, which is itself a step
            ..from centroid in FlashCentroids
              from rms in FlashRms
              from harmonic in FlashHarmonics
              select (AnalysisFrame?)Frame(centroid, rms, harmonic),
        ];

        TimeSpan third = TimeSpan.FromSeconds(1.0 / 3.0);
        foreach (bool dark in new[] { true, false })
        {
            double[] worstPerSurface = new double[4];
            string[] worstPairPerSurface = ["none", "none", "none", "none"];

            foreach (AnalysisFrame? from in states)
            {
                foreach (AnalysisFrame? to in states)
                {
                    var engine = new ReactiveThemeEngine(dark, new ReactiveThemeOptions(true, 0.0f));
                    ReactiveThemePalette before = engine.Advance(from, third);
                    ReactiveThemePalette after = engine.Advance(to, third);

                    for (int i = 0; i < worstPerSurface.Length; i++)
                    {
                        double change = Math.Abs(after.All[i].Luminance - before.All[i].Luminance);
                        if (change > worstPerSurface[i])
                        {
                            worstPerSurface[i] = change;
                            worstPairPerSurface[i] = $"{Describe(from)} -> {Describe(to)}";
                        }
                    }
                }
            }

            string[] names = ["primary", "secondary", "accent", "background"];
            _output.WriteLine($"{(dark ? "dark" : "light")}: {states.Length * states.Length} ordered pairs, worst "
                              + "third-of-a-second luminance change per surface against the 0.10 threshold:");
            for (int i = 0; i < worstPerSurface.Length; i++)
            {
                _output.WriteLine($"    {names[i],-11} {worstPerSurface[i]:F4}  {worstPairPerSurface[i]}");
            }

            // Every surface, not only the primary the hand-picked case measured. The contract is about the
            // field, and the field is painted from the whole palette rather than from one of its four colours.
            for (int i = 0; i < worstPerSurface.Length; i++)
            {
                worstPerSurface[i].Should().BeLessThan(0.10,
                    $"no pair of states the mapping can reach may move the {names[i]} surface's luminance by "
                    + "0.10 in a third of a second");
            }
        }
    }

    private static string Describe(AnalysisFrame? frame) =>
        frame is { } f
            ? $"(c {f.SpectralCentroidHz:F0} Hz, rms {f.Rms:F3}, hr {f.HarmonicRatio:F2})"
            : "(no frame)";

    [Fact]
    public void The_art_palette_pulls_the_hue_without_taking_it_over()
    {
        // Both engines get the same frame and only one has art, so the harmonic ratio cancels out of the
        // comparison - but it sets how much chroma there is to carry a hue, so it is one music has.
        var plain = Engine();
        var withArt = Engine();
        // A sleeve whose hue is nowhere near the one 6 kHz asks for, so the blend has somewhere to show.
        var sleeve = new Srgb(30, 180, 140);
        withArt.SetArtPalette(new ArtPalette([
            new PaletteColor(sleeve.R, sleeve.G, sleeve.B, 0.6, PaletteColor.RelativeLuminance(sleeve.R, sleeve.G, sleeve.B)),
            new PaletteColor(20, 60, 50, 0.4, PaletteColor.RelativeLuminance(20, 60, 50)),
        ]));

        var frame = Frame(6000f, 0.3f, TypicalHarmonic);
        Settle(plain, frame, TimeSpan.FromSeconds(20));
        Settle(withArt, frame, TimeSpan.FromSeconds(20));

        double moved = HueGap(plain.Palette.Primary, withArt.Palette.Primary);
        double toArt = HueGap(withArt.Palette.Primary, sleeve);
        double wholeWay = HueGap(plain.Palette.Primary, sleeve);
        _output.WriteLine($"no art {plain.Palette.Primary.Hex}, teal sleeve {withArt.Palette.Primary.Hex}, "
                          + $"moved {moved:F1} of the {wholeWay:F1} degrees to the sleeve, still {toArt:F1} short");
        moved.Should().BeGreaterThan(20.0, "a blend nobody can see is not a blend");
        toArt.Should().BeGreaterThan(20.0, "the art is one voice in the hue, not the whole of it");
    }

    [Fact]
    public void A_near_grey_sleeve_is_not_allowed_to_choose_a_hue()
    {
        // At the most saturated input, where a hue borrowed from the sleeve would be most visible if it leaked.
        var plain = Engine();
        var withArt = Engine();
        withArt.SetArtPalette(new ArtPalette([
            new PaletteColor(70, 71, 69, 1.0, PaletteColor.RelativeLuminance(70, 71, 69)),
        ]));

        var frame = Frame(6000f, 0.3f, MostHarmonic);
        Settle(plain, frame, TimeSpan.FromSeconds(20));
        Settle(withArt, frame, TimeSpan.FromSeconds(20));

        withArt.Palette.Should().Be(plain.Palette,
            "the hue of a grey is whatever survived quantisation, and that is noise rather than a colour");
    }

    [Fact]
    public void The_smoothing_setting_maps_onto_the_documented_range()
    {
        new ReactiveThemeOptions(true, 0.0f).TimeConstant.Should().Be(ReactiveThemeOptions.MinimumTimeConstant);
        new ReactiveThemeOptions(true, 1.0f).TimeConstant.Should().Be(ReactiveThemeOptions.MaximumTimeConstant);
        ReactiveThemeOptions.Default.Enabled.Should().Be(SettingsKeys.Defaults.UiReactiveTheming);
        ReactiveThemeOptions.Default.Smoothing.Should().Be(SettingsKeys.Defaults.UiReactiveSmoothing);

        // Out of range in the settings file is clamped, not trusted: a hand-edited -3 must not become a
        // negative time constant and an instant background.
        new ReactiveThemeOptions(true, -3f).TimeConstant.Should().Be(ReactiveThemeOptions.MinimumTimeConstant);
        new ReactiveThemeOptions(true, 9f).TimeConstant.Should().Be(ReactiveThemeOptions.MaximumTimeConstant);
    }

    [Fact]
    public void The_options_are_read_from_the_settings_store()
    {
        var settings = new FakeSettingsStore();
        ReactiveThemeOptions.Read(settings).Should().Be(ReactiveThemeOptions.Default);

        settings.SetValue(SettingsKeys.UiReactiveTheming, false);
        settings.SetValue(SettingsKeys.UiReactiveSmoothing, 0.8f);
        ReactiveThemeOptions.Read(settings).Should().Be(new ReactiveThemeOptions(false, 0.8f));
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        private readonly Dictionary<string, object?> _values = [];

        public event EventHandler<string>? Changed;

        public T GetValue<T>(string key, T defaultValue) =>
            _values.TryGetValue(key, out object? value) && value is T typed ? typed : defaultValue;

        public void SetValue<T>(string key, T value)
        {
            _values[key] = value;
            Changed?.Invoke(this, key);
        }

        public bool Contains(string key) => _values.ContainsKey(key);

        public IReadOnlyList<string> KeysStartingWith(string prefix) =>
            [.. _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];

        public void Flush()
        {
        }

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
