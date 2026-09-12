using Tunqio.Core.Library;

namespace Tunqio.Core.Visualization;

/// <summary>One sRGB colour, 8 bits a channel - which is what a brush, a swap chain and a PNG all carry.</summary>
public readonly record struct Srgb(byte R, byte G, byte B)
{
    /// <summary>Parses <c>#rrggbb</c> (the form <see cref="PaletteColor.Hex"/> writes).</summary>
    public static Srgb Parse(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        ReadOnlySpan<char> digits = hex.AsSpan().TrimStart('#');
        if (digits.Length != 6)
        {
            throw new FormatException($"\"{hex}\" is not #rrggbb");
        }

        return new Srgb(
            byte.Parse(digits[..2], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture),
            byte.Parse(digits[2..4], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture),
            byte.Parse(digits[4..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary><c>#rrggbb</c>.</summary>
    public string Hex => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}");

    /// <summary>WCAG relative luminance, 0 black to 1 white.</summary>
    public double Luminance => ReactiveContrast.Luminance(this);
}

/// <summary>
/// The contrast guarantee of the accessibility contract (docs/ui-screens-and-flows.md), as a function rather
/// than as a hope: <see cref="Constrain"/> takes any colour the audio-reactive mapping can produce and returns
/// one that foreground text reads on at <see cref="TextMinimum"/> or better.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a bound on luminance and not a search.</b> WCAG contrast is a ratio of two relative
/// luminances, and relative luminance is a fixed linear combination of <i>linearised</i> channels. So "text at
/// 4.5:1 reads on this background" is exactly "the background's luminance is outside an interval either side of
/// the text's", and that interval is arithmetic on the foreground alone. Multiplying all three linear channels
/// by <c>k</c> multiplies luminance by <c>k</c> exactly, and mixing toward white by <c>t</c> moves it to
/// <c>L + t(1 - L)</c> exactly, so the colour that just satisfies the bound is closed form. Nothing iterates,
/// nothing searches, and the result is not "close enough" - it is on the bound or past it.
/// </para>
/// <para>
/// <b>Scaling in linear light rather than sliding HSL lightness</b> keeps chromaticity: the hue the music chose
/// survives, which is the whole point of the feature. Mixing toward white necessarily desaturates, because that
/// is what adding white is; it is only used where the foreground is dark, and there is no darker direction
/// available.
/// </para>
/// <para>
/// <b>Quantisation is part of the guarantee, not an afterthought.</b> The answer has to come back as a byte per
/// channel, and rounding to the nearest byte can move luminance the wrong way across the bound. So the
/// conversion back rounds toward the safe side - down when the colour was darkened, up when it was lightened -
/// which can only take it further from the foreground. That is what lets
/// <c>ReactiveContrastTests</c> check every one of the 16 777 216 sRGB colours rather than sample some.
/// </para>
/// </remarks>
public static class ReactiveContrast
{
    /// <summary>WCAG 2.1 AA for body text, and what AC-126 asks for.</summary>
    public const double TextMinimum = 4.5;

    // sRGB byte -> linear, the WCAG transfer function. 256 entries, so a luminance is three lookups and a dot
    // product; the table is also what the inverse binary-searches, which is why there is no Math.Pow anywhere
    // on the hot path and why the round trip byte -> linear -> byte is exact by construction.
    private static readonly double[] LinearOf = BuildLinearTable();

    private static double[] BuildLinearTable()
    {
        var table = new double[256];
        for (int i = 0; i < 256; i++)
        {
            double c = i / 255.0;
            table[i] = c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return table;
    }

    /// <summary>WCAG relative luminance of an sRGB colour.</summary>
    public static double Luminance(Srgb colour) =>
        (0.2126 * LinearOf[colour.R]) + (0.7152 * LinearOf[colour.G]) + (0.0722 * LinearOf[colour.B]);

    /// <summary>The WCAG contrast ratio between two colours; 1.0 when they are the same, 21.0 black on white.</summary>
    public static double Ratio(Srgb a, Srgb b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        (double hi, double lo) = la >= lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>
    /// The luminance a background may not exceed if it is to be the darker of the pair, and the one it may not
    /// fall below if it is to be the lighter. Either may be outside 0..1, which is that side saying "no colour
    /// exists there": white text has no lighter background and black text has no darker one.
    /// </summary>
    public static (double Darker, double Lighter) Bounds(Srgb foreground, double minimum = TextMinimum)
    {
        double lf = Luminance(foreground);
        return (((lf + 0.05) / minimum) - 0.05, (minimum * (lf + 0.05)) - 0.05);
    }

    /// <summary>
    /// True when one background exists that every one of <paramref name="foregrounds"/> reads on at
    /// <paramref name="minimum"/>. It takes a set because a single foreground always has one: the two bounds
    /// cannot both fall outside 0..1, since that would need its luminance below 0.175 and above 0.1833 at once.
    /// A set can fail, and the way it fails is the obvious one - white text and near-black text asked to share
    /// a surface want opposite backgrounds, and no colour is both.
    /// </summary>
    public static bool IsSatisfiable(IReadOnlyList<Srgb> foregrounds, double minimum = TextMinimum)
    {
        ArgumentNullException.ThrowIfNull(foregrounds);
        if (foregrounds.Count == 0)
        {
            return true;
        }

        double darker = double.PositiveInfinity;
        double lighter = double.NegativeInfinity;
        foreach (Srgb foreground in foregrounds)
        {
            (double d, double l) = Bounds(foreground, minimum);
            darker = Math.Min(darker, d);
            lighter = Math.Max(lighter, l);
        }

        return darker >= 0.0 || lighter <= 1.0;
    }

    /// <summary>
    /// <paramref name="colour"/> moved, along the line toward black or toward white, to wherever every one of
    /// <paramref name="foregrounds"/> reads on it at <paramref name="minimum"/> or better. A colour already
    /// there comes back unchanged, so a palette that never gets loud is never touched.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// A foreground no background can satisfy. That is a mistake in the theme's tokens rather than a colour the
    /// music produced, so it is thrown at the caller instead of quietly returning something that fails.
    /// </exception>
    public static Srgb Constrain(Srgb colour, IReadOnlyList<Srgb> foregrounds, double minimum = TextMinimum)
    {
        ArgumentNullException.ThrowIfNull(foregrounds);
        if (foregrounds.Count == 0)
        {
            return colour;
        }

        // The tightest bound each side wants, so one colour answers every foreground drawn on this surface.
        double darker = double.PositiveInfinity;
        double lighter = double.NegativeInfinity;
        foreach (Srgb foreground in foregrounds)
        {
            (double d, double l) = Bounds(foreground, minimum);
            darker = Math.Min(darker, d);
            lighter = Math.Max(lighter, l);
        }

        bool darkerReachable = darker >= 0.0;
        bool lighterReachable = lighter <= 1.0;
        if (!darkerReachable && !lighterReachable)
        {
            throw new ArgumentException(
                $"no sRGB colour reads at {minimum}:1 against every one of these foregrounds", nameof(foregrounds));
        }

        double luminance = Luminance(colour);
        if ((darkerReachable && luminance <= darker) || (lighterReachable && luminance >= lighter))
        {
            return colour;
        }

        // Whichever side is both reachable and nearer, measured in luminance - the smaller move is the one that
        // keeps more of what the music chose.
        bool goDarker = darkerReachable && (!lighterReachable || (luminance - darker) <= (lighter - luminance));
        return goDarker ? ScaleToLuminance(colour, darker) : MixToLuminance(colour, lighter);
    }

    // Toward black: every linear channel times k, so luminance times k, so chromaticity untouched.
    private static Srgb ScaleToLuminance(Srgb colour, double target)
    {
        double luminance = Luminance(colour);
        if (luminance <= 0.0)
        {
            return colour; // already black, and black is under every reachable darker bound
        }

        double k = Math.Clamp(target / luminance, 0.0, 1.0);
        return new Srgb(
            ByteAtMost(LinearOf[colour.R] * k),
            ByteAtMost(LinearOf[colour.G] * k),
            ByteAtMost(LinearOf[colour.B] * k));
    }

    // Toward white: L becomes L + t(1 - L) in every channel alike.
    private static Srgb MixToLuminance(Srgb colour, double target)
    {
        double luminance = Luminance(colour);
        double t = Math.Clamp((target - luminance) / (1.0 - luminance), 0.0, 1.0);
        return new Srgb(
            ByteAtLeast(LinearOf[colour.R] + (t * (1.0 - LinearOf[colour.R]))),
            ByteAtLeast(LinearOf[colour.G] + (t * (1.0 - LinearOf[colour.G]))),
            ByteAtLeast(LinearOf[colour.B] + (t * (1.0 - LinearOf[colour.B]))));
    }

    // The largest byte whose linear value does not exceed `linear`: rounding that can only darken.
    private static byte ByteAtMost(double linear)
    {
        int index = Array.BinarySearch(LinearOf, linear);
        if (index >= 0)
        {
            return (byte)index;
        }

        int insert = ~index; // the first entry above `linear`
        return (byte)Math.Clamp(insert - 1, 0, 255);
    }

    // The smallest byte whose linear value is at least `linear`: rounding that can only lighten.
    private static byte ByteAtLeast(double linear)
    {
        int index = Array.BinarySearch(LinearOf, linear);
        if (index >= 0)
        {
            return (byte)index;
        }

        return (byte)Math.Clamp(~index, 0, 255);
    }
}
