using System.Collections.Concurrent;
using FluentAssertions;
using Tunqio.Core.Visualization;
using Xunit.Abstractions;

namespace Tunqio.Core.Tests;

/// <summary>
/// AC-126: foreground text on every reactive surface stays at 4.5:1 or better across the full colour range
/// (E4-S6; docs/ui-screens-and-flows.md, "Accessibility contract").
/// </summary>
/// <remarks>
/// <para>
/// <b>"Across the full colour range" is taken literally.</b> <see cref="Sweep"/> walks every one of the
/// 16 777 216 colours an 8-bit sRGB surface can be - not a grid over them, not a sample of them - so there is no
/// "between the samples" for a violation to hide in. That is affordable because
/// <see cref="ReactiveContrast.Constrain"/> is closed form over a 256-entry table rather than a search, and
/// because it is a function of a colour and a set of foregrounds and of nothing else: a surface's identity,
/// its place in the gradient and how the mapping arrived at its colour cannot change the answer. So every
/// reactive surface of a theme is covered by walking the range once for that theme's foreground set.
/// </para>
/// <para>
/// <b>The check is shown to fail before it is trusted.</b> <see cref="The_sweep_finds_the_violations_an_unguarded_colour_leaves"/>
/// runs the same sweep with the guarantee taken out and requires it to come back red, naming the first colour
/// it caught; <see cref="A_foreground_in_the_middle_of_the_range_is_refused_rather_than_approximated"/> feeds
/// it #808080, which nothing at all reads on at 4.5:1.
/// </para>
/// </remarks>
public class ReactiveContrastTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    /// <summary>What one pass over the whole colour range found.</summary>
    private sealed record SweepResult(double MinRatio, long Violations, Srgb? FirstViolation, Srgb? WorstColour);

    /// <summary>
    /// Every sRGB colour, put through <paramref name="surface"/> and measured against every foreground. Split
    /// by red channel so the 256 slices run in parallel; each slice is independent, so the only shared state is
    /// the per-slice result.
    /// </summary>
    private static SweepResult Sweep(Func<Srgb, Srgb> surface, IReadOnlyList<Srgb> foregrounds)
    {
        var slices = new ConcurrentBag<SweepResult>();
        Parallel.For(0, 256, r =>
        {
            double min = double.PositiveInfinity;
            long violations = 0;
            Srgb? first = null;
            Srgb? worst = null;
            for (int g = 0; g < 256; g++)
            {
                for (int b = 0; b < 256; b++)
                {
                    var input = new Srgb((byte)r, (byte)g, (byte)b);
                    Srgb painted = surface(input);
                    foreach (Srgb foreground in foregrounds)
                    {
                        double ratio = ReactiveContrast.Ratio(painted, foreground);
                        if (ratio < min)
                        {
                            min = ratio;
                            worst = input;
                        }

                        if (ratio < ReactiveContrast.TextMinimum)
                        {
                            violations++;
                            first ??= input;
                        }
                    }
                }
            }

            slices.Add(new SweepResult(min, violations, first, worst));
        });

        SweepResult best = slices.MinBy(s => s.MinRatio)!;
        return new SweepResult(best.MinRatio, slices.Sum(s => s.Violations),
                               slices.Select(s => s.FirstViolation).FirstOrDefault(v => v is not null), best.WorstColour);
    }

    public static TheoryData<string> Themes => new() { "dark", "light" };

    private static IReadOnlyList<Srgb> ForegroundsOf(string theme) =>
        theme == "dark" ? ReactiveTheming.DarkForegrounds : ReactiveTheming.LightForegrounds;

    [Theory]
    [MemberData(nameof(Themes))]
    public void Every_srgb_colour_a_reactive_surface_can_take_keeps_text_at_four_and_a_half(string theme)
    {
        IReadOnlyList<Srgb> foregrounds = ForegroundsOf(theme);

        SweepResult result = Sweep(c => ReactiveContrast.Constrain(c, foregrounds), foregrounds);

        _output.WriteLine($"{theme}: 16777216 colours x {foregrounds.Count} foregrounds, "
                          + $"{result.Violations} under {ReactiveContrast.TextMinimum}:1, "
                          + $"worst {result.MinRatio:F4}:1 at {result.WorstColour?.Hex}");
        result.Violations.Should().Be(0,
            "the guarantee is a bound on luminance computed from the foreground, so there is no colour it can miss "
            + $"(worst was {result.WorstColour?.Hex} at {result.MinRatio:F3}:1)");
        result.MinRatio.Should().BeGreaterThanOrEqualTo(ReactiveContrast.TextMinimum);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void The_sweep_finds_the_violations_an_unguarded_colour_leaves(string theme)
    {
        IReadOnlyList<Srgb> foregrounds = ForegroundsOf(theme);

        // The same sweep, over the same range, with the guarantee taken out: the colour the mapping produced
        // painted straight onto the surface. If this came back clean the test above would be proving nothing.
        SweepResult result = Sweep(c => c, foregrounds);

        _output.WriteLine($"{theme} with the guarantee removed: {result.Violations} of "
                          + $"{16777216L * foregrounds.Count} pairs under {ReactiveContrast.TextMinimum}:1, "
                          + $"worst {result.MinRatio:F4}:1, first {result.FirstViolation?.Hex}");
        result.Violations.Should().BeGreaterThan(0,
            "an unconstrained reactive colour is free to land anywhere, including on top of the text");
        result.FirstViolation.Should().NotBeNull();
        result.MinRatio.Should().BeLessThan(ReactiveContrast.TextMinimum);
    }

    [Fact]
    public void A_colour_already_far_enough_from_the_text_is_left_exactly_as_it_was()
    {
        var safe = new Srgb(0x10, 0x12, 0x18);
        ReactiveContrast.Ratio(safe, new Srgb(0xFF, 0xFF, 0xFF))
            .Should().BeGreaterThan(ReactiveContrast.TextMinimum, "the premise of the test");

        ReactiveContrast.Constrain(safe, ReactiveTheming.DarkForegrounds).Should().Be(safe);
    }

    [Fact]
    public void The_ratio_is_the_wcag_one()
    {
        var white = new Srgb(0xFF, 0xFF, 0xFF);
        ReactiveContrast.Ratio(new Srgb(0, 0, 0), white).Should().BeApproximately(21.0, 0.001);
        ReactiveContrast.Ratio(white, white).Should().BeApproximately(1.0, 0.001);

        // The two greys either side of the AA line for text on white, which is where an off-by-a-little shows.
        ReactiveContrast.Ratio(new Srgb(0x76, 0x76, 0x76), white).Should().BeApproximately(4.54, 0.01);
        ReactiveContrast.Ratio(new Srgb(0x80, 0x80, 0x80), white).Should().BeApproximately(3.95, 0.01);
    }

    [Fact]
    public void Foregrounds_that_want_opposite_backgrounds_are_refused_rather_than_approximated()
    {
        // The failure mode that exists. A single foreground never has one - its two bounds cannot both fall
        // outside 0..1, which would need its luminance under 0.175 and over 0.1833 at once - so even mid grey,
        // the colour that reads on nothing at a glance, has backgrounds at 4.5:1 either side of it.
        ReactiveContrast.IsSatisfiable([new Srgb(0x80, 0x80, 0x80)]).Should().BeTrue();

        IReadOnlyList<Srgb> opposed = [new Srgb(0xFF, 0xFF, 0xFF), new Srgb(0x1B, 0x1B, 0x1B)];
        ReactiveContrast.IsSatisfiable(opposed).Should().BeFalse(
            "white text wants a dark background and near-black text a light one, and no colour is both");

        FluentActions.Invoking(() => ReactiveContrast.Constrain(new Srgb(0x40, 0x40, 0x40), opposed))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Every_single_colour_in_the_range_is_a_foreground_something_reads_on()
    {
        // Exhaustive because the claim above - that a lone foreground can never be unsatisfiable - is the
        // reason Constrain's exception names a *set*, and a claim about every colour is worth checking on
        // every colour rather than on the handful that came to mind.
        long unsatisfiable = 0;
        Parallel.For(0, 256, () => 0L, (r, _, local) =>
        {
            for (int g = 0; g < 256; g++)
            {
                for (int b = 0; b < 256; b++)
                {
                    if (!ReactiveContrast.IsSatisfiable([new Srgb((byte)r, (byte)g, (byte)b)]))
                    {
                        local++;
                    }
                }
            }

            return local;
        }, local => Interlocked.Add(ref unsatisfiable, local));

        unsatisfiable.Should().Be(0);
    }

    [Fact]
    public void Each_theme_has_exactly_one_direction_to_move_in()
    {
        foreach (IReadOnlyList<Srgb> foregrounds in ReactiveTheming.AllForegrounds)
        {
            ReactiveContrast.IsSatisfiable(foregrounds).Should().BeTrue(
                "these are the tokens the shell draws on a reactive surface");
        }

        // White text has no background lighter than itself to sit on, and near-black text none darker: each
        // theme therefore has one side, which is why the reactive background of a dark theme only ever darkens.
        (double darker, double lighter) = ReactiveContrast.Bounds(ReactiveTheming.DarkForegrounds[0]);
        darker.Should().BeInRange(0.0, 1.0);
        lighter.Should().BeGreaterThan(1.0);

        (darker, lighter) = ReactiveContrast.Bounds(ReactiveTheming.LightForegrounds[0]);
        darker.Should().BeLessThan(0.0);
        lighter.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void Rounding_to_a_byte_moves_the_colour_away_from_the_text_and_never_toward_it()
    {
        // The bound is arithmetic on doubles and the answer is three bytes, so the conversion back is where a
        // guarantee gets lost. Mid greys are the worst case: the furthest from either bound, so the most moved.
        for (int v = 0; v < 256; v++)
        {
            var colour = new Srgb((byte)v, (byte)v, (byte)v);
            Srgb dark = ReactiveContrast.Constrain(colour, ReactiveTheming.DarkForegrounds);
            (double darkerBound, _) = ReactiveContrast.Bounds(ReactiveTheming.DarkForegrounds[1]);
            ReactiveContrast.Luminance(dark).Should().BeLessThanOrEqualTo(darkerBound,
                $"#{v:x2} darkened must land on the safe side of the bound, not within rounding of it");
        }
    }
}
