namespace Tunqio.Core.Visualization;

/// <summary>
/// One place the reactive surfaces and the text drawn on them are named, so "every reactive surface" in AC-126
/// is a list and not a figure of speech.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a reactive surface is.</b> The accessibility contract (docs/ui-screens-and-flows.md) says reactive
/// theming blends only the background layer. So the reactive surfaces are the four colours of
/// <see cref="ReactiveThemePalette"/> wherever the shell paints them - the two stops of the window's background
/// gradient, the accent, and the deepest of them behind the panels - and the foreground on all of them is the
/// active theme's text.
/// </para>
/// <para>
/// <b>Why the foreground tokens here are opaque</b> when WinUI's own <c>TextFillColorPrimary</c> is not. An
/// alpha'd foreground resolves against the very colour that is moving: white at 77% over a background that has
/// just darkened is itself darker, so a bound on the background alone cannot guarantee the pair. These are the
/// opaque equivalents, and the shell draws text on reactive surfaces with them; that is a commitment this
/// module makes rather than an approximation it hopes for.
/// </para>
/// <para>
/// <b>What that buys the test.</b> <see cref="ReactiveContrast.Constrain"/> is a function of a colour and a set
/// of foregrounds and of nothing else - not of which surface, not of the theme. Every reactive surface in a
/// theme is painted by putting some colour through it with that theme's foregrounds. So checking every one of
/// the 16 777 216 sRGB colours against each theme's foreground set covers every surface of that theme
/// exhaustively, whatever colours the mapping turns out to reach. That is the argument
/// <c>ReactiveContrastTests</c> makes, and it is why it does not sample.
/// </para>
/// </remarks>
public static class ReactiveTheming
{
    /// <summary>Primary and secondary text on a reactive surface in the dark theme.</summary>
    public static IReadOnlyList<Srgb> DarkForegrounds { get; } = [new Srgb(0xFF, 0xFF, 0xFF), new Srgb(0xC5, 0xC5, 0xC5)];

    /// <summary>Primary and secondary text on a reactive surface in the light theme.</summary>
    public static IReadOnlyList<Srgb> LightForegrounds { get; } = [new Srgb(0x1B, 0x1B, 0x1B), new Srgb(0x5D, 0x5D, 0x5D)];

    /// <summary>Both foreground sets, for a test that wants to quantify over every theme there is.</summary>
    public static IReadOnlyList<IReadOnlyList<Srgb>> AllForegrounds { get; } = [DarkForegrounds, LightForegrounds];

    /// <summary>HSL to sRGB. <paramref name="hue"/> is degrees 0..360, the other two 0..1.</summary>
    public static Srgb FromHsl(double hue, double saturation, double lightness)
    {
        double c = (1.0 - Math.Abs((2.0 * lightness) - 1.0)) * saturation;
        double h = (((hue % 360.0) + 360.0) % 360.0) / 60.0;
        double x = c * (1.0 - Math.Abs((h % 2.0) - 1.0));
        (double r, double g, double b) = (int)h switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        double m = lightness - (c / 2.0);
        return new Srgb(Channel(r + m), Channel(g + m), Channel(b + m));

        static byte Channel(double v) => (byte)Math.Clamp(Math.Round(v * 255.0), 0.0, 255.0);
    }

    /// <summary>sRGB to HSL: hue in degrees 0..360, saturation and lightness 0..1.</summary>
    public static (double Hue, double Saturation, double Lightness) ToHsl(Srgb colour)
    {
        double r = colour.R / 255.0;
        double g = colour.G / 255.0;
        double b = colour.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2.0;
        double delta = max - min;
        if (delta <= 0.0)
        {
            return (0.0, 0.0, lightness);
        }

        double saturation = delta / (1.0 - Math.Abs((2.0 * lightness) - 1.0));
        double hue;
        if (max == r)
        {
            hue = 60.0 * (((g - b) / delta) % 6.0);
        }
        else if (max == g)
        {
            hue = 60.0 * (((b - r) / delta) + 2.0);
        }
        else
        {
            hue = 60.0 * (((r - g) / delta) + 4.0);
        }

        return (((hue % 360.0) + 360.0) % 360.0, Math.Clamp(saturation, 0.0, 1.0), lightness);
    }
}
