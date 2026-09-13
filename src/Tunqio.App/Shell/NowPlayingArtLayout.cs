namespace Tunqio.App.Shell;

/// <summary>
/// How large the Now Playing art is drawn, given the room the panel has. Pure, so it can be asserted without a window.
/// </summary>
/// <remarks>
/// The art was a <c>Viewbox</c> capped at 360 px inside a vertically centred grid, and nothing ever bounded that grid's
/// height: in a short panel the art was clipped rather than scaled. T-182 made the stacked shell split its height
/// equally (Q-56), so at every narrow width Now Playing is now a short row, and the art is sized from what is left over
/// once the metadata block has its height.
/// </remarks>
public static class NowPlayingArtLayout
{
    /// <summary>The largest the art is ever drawn, and the size it is decoded to.</summary>
    public const double MaxEdge = 360;

    /// <summary>The smallest it is drawn: below this the art stops being recognisable and the placeholder's initials stop fitting.</summary>
    public const double MinEdge = 96;

    /// <summary>The gap between the art and the metadata block beneath it (the panel's <c>RowSpacing</c>).</summary>
    public const double Spacing = 20;

    /// <summary>Room kept clear on each side of the art, so it never touches the panel's edges.</summary>
    public const double Clearance = 24;

    /// <summary>
    /// The edge of the square the art is drawn in: the height left once the metadata block and the spacing are taken
    /// out, bounded by the width, and clamped to <see cref="MinEdge"/>..<see cref="MaxEdge"/>. A panel that has not been
    /// laid out yet reports no size, and gets the full edge rather than the floor.
    /// </summary>
    public static double ArtEdge(double panelHeight, double panelWidth, double metadataHeight)
    {
        if (double.IsNaN(panelHeight) || panelHeight <= 0)
        {
            return MaxEdge;
        }

        double byHeight = panelHeight - metadataHeight - Spacing - (2 * Clearance);
        double byWidth = double.IsNaN(panelWidth) || panelWidth <= 0 ? MaxEdge : panelWidth - (2 * Clearance);
        return Math.Clamp(Math.Min(byHeight, byWidth), MinEdge, MaxEdge);
    }
}
