using Tunqio.Core.Library;

namespace Tunqio.Core.Visualization;

/// <summary>
/// The album art palette, as the <c>ambient-glow</c> preset takes it (E4-S5, AC-124). Turns an
/// <see cref="ArtPalette"/> - E3-S7's five median-cut colours, reached through
/// <see cref="IArtCache.LoadPaletteAsync"/> - into the three parameter values the preset declares, and sets them
/// through <see cref="IVisualizationHost.SetParameter"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why parameters and not <see cref="IVisualizationHost.SetThemeColors"/>.</b> That is E4-S6's, and the core
/// refuses it until then. E4-S6 is a renderer-wide theme: four colours reaching every preset and the shell's own
/// background gradient, polled at 30 Hz with smoothing and a contrast guarantee. AC-124 is narrower - one
/// preset's colour <i>source</i> - and the preset contract already expresses that, so this needs no ABI change,
/// no new field in the shader's constant buffer and none of E4-S6's policy decided early.
/// </para>
/// <para>
/// <b>The packing.</b> A parameter is one <see cref="float"/>, so a colour is packed into one:
/// <c>r * 65536 + g * 256 + b</c>. Every value is an integer below 2^24 and so exactly representable, which is
/// what keeps the preset's golden image reproducible. One float per colour rather than three is not only
/// economy: the native side stores parameters as independent relaxed atomics that the render thread reads once a
/// frame, so three channels set in sequence could be read half-applied and show a wrong colour for a frame.
/// </para>
/// <para>
/// <b>"When available".</b> <see cref="NoArt"/> is what a track with no art sends, and it is also the preset's
/// declared default, so a renderer nobody has told about a picture already draws its own colours. A palette
/// whose colours are too dark to glow is handled in the shader, per colour, for the same reason: a black sleeve
/// quantises to near-black entries and amplifying one of those is not a glow.
/// </para>
/// </remarks>
public static class AmbientGlowPalette
{
    /// <summary>The preset these parameters belong to; <see cref="Apply"/> does nothing when another is running.</summary>
    public const string PresetId = "ambient-glow";

    /// <summary>"This track has no album art." The preset's declared default for all three parameters.</summary>
    public const float NoArt = -1.0f;

    /// <summary>The three parameters, in the order <see cref="Parameters"/> returns them.</summary>
    public static IReadOnlyList<string> ParameterNames { get; } = ["art_primary", "art_secondary", "art_accent"];

    /// <summary>One sRGB colour as the preset carries it: <c>r * 65536 + g * 256 + b</c>.</summary>
    public static float Pack(PaletteColor colour) => (colour.R * 65536) + (colour.G * 256) + colour.B;

    /// <summary>
    /// The three values for <see cref="ParameterNames"/>, or <see cref="NoArt"/> in each when there is no usable
    /// palette. The dominant colour leads; the second is the next most populous one the extractor actually found
    /// (a slot filled out to five has population 0 and is a shade of the dominant, so it would say nothing new);
    /// the accent is the palette's lightest, which is the one that reads as a highlight against a dark panel.
    /// </summary>
    public static (float Primary, float Secondary, float Accent) Parameters(ArtPalette? palette)
    {
        if (palette is null || palette.Colors.Count == 0)
        {
            return (NoArt, NoArt, NoArt);
        }

        PaletteColor dominant = palette.Dominant;
        PaletteColor secondary = palette.Colors.Skip(1).FirstOrDefault(c => c.Population > 0)
            ?? palette.Colors.Skip(1).FirstOrDefault()
            ?? dominant;
        return (Pack(dominant), Pack(secondary), Pack(palette.Lightest()));
    }

    /// <summary>
    /// Sets the three parameters on <paramref name="host"/>, or does nothing when the preset running is not
    /// <see cref="PresetId"/> - the core refuses a parameter the active preset does not declare, and a track
    /// change while some other visualizer is on screen is not an error.
    /// </summary>
    public static void Apply(IVisualizationHost host, ArtPalette? palette)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.ActivePresetId != PresetId)
        {
            return;
        }

        (float primary, float secondary, float accent) = Parameters(palette);
        host.SetParameter(ParameterNames[0], primary);
        host.SetParameter(ParameterNames[1], secondary);
        host.SetParameter(ParameterNames[2], accent);
    }
}
