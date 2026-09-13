namespace Tunqio.App.Shell;

/// <summary>Which of the three shapes the shell is in (docs/user-interface.md, "Window Size Adaptation").</summary>
public enum ShellLayoutMode
{
    /// <summary>Under 800 px: the panels stack, because side by side none of them would be usable.</summary>
    Compact,

    /// <summary>800 to 1200 px: still two columns, but the sidebar gives some of its share to Now Playing.</summary>
    Medium,

    /// <summary>1200 px and up: Now Playing 75, the sidebar 25.</summary>
    Full,
}

/// <summary>
/// The shares and shape of the shell's panels at one window width. A value, and the arithmetic is pure, so
/// the breakpoints can be asserted without a window: what a XAML-only <c>AdaptiveTrigger</c> would have hidden
/// inside the visual tree is a table here instead, and AC-68 is about the numbers.
/// </summary>
/// <remarks>
/// The controls panel is not a share in any shape. It is a transport bar at its natural height: beneath Now Playing
/// in the column shapes, and beneath both panels when they stack. Until T-182 it was a third column of 15%, which
/// measured 238 px at 1600 and 128 px at 1000 against a transport row that needs 242, and clipped Shuffle to nothing.
/// </remarks>
/// <param name="Mode">Which shape this width is in.</param>
/// <param name="NowPlaying">Now Playing's star weight, along the axis the panels are laid out on.</param>
/// <param name="Sidebar">The sidebar's star weight.</param>
/// <param name="Stacked">True when the panels are rows rather than columns.</param>
public readonly record struct ShellLayoutState(
    ShellLayoutMode Mode,
    double NowPlaying,
    double Sidebar,
    bool Stacked)
{
    /// <summary>The two shares as fractions of the space the panels divide, which is what "75 / 25" means.</summary>
    public (double NowPlaying, double Sidebar) Fractions
    {
        get
        {
            double total = NowPlaying + Sidebar;
            return total <= 0 ? (0, 0) : (NowPlaying / total, Sidebar / total);
        }
    }
}

/// <summary>
/// Where the shell's breakpoints are and what each side of them looks like (E2-S1, T-182). Separate from the window
/// so the table can be read and tested on its own: a layout rule that exists only as a <c>VisualState</c> can only
/// be checked by looking at a running window, and this one has a criterion about its proportions.
/// </summary>
public static class ShellLayout
{
    /// <summary>Below this the panels stack (docs/user-interface.md, <c>COMPACT_THRESHOLD</c>).</summary>
    public const double CompactThreshold = 800;

    /// <summary>Below this the sidebar is narrowed (docs/user-interface.md, <c>MEDIUM_THRESHOLD</c>).</summary>
    public const double MediumThreshold = 1200;

    /// <summary>Now Playing's floor in a column shape, which is also the floor of the transport bar beneath it.</summary>
    public const double NowPlayingMinWidth = 400;

    /// <summary>The sidebar's floor: a library grid narrower than this shows one tile a row.</summary>
    public const double SidebarMinWidth = 200;

    /// <summary>
    /// The width the transport's widest row asks for, measured in T-182: three 38 px mode buttons, a 92 px volume
    /// slider, three 4 px gaps and 24 px of padding. The bar is as wide as the Now Playing column it sits under, so
    /// Now Playing's floor has to cover this or the bar cuts off its own controls at the narrowest column shape.
    /// </summary>
    public const double TransportMinWidth = 242;

    /// <summary>
    /// The shape for <paramref name="windowWidth"/>. Compact stacks Now Playing over the sidebar with the transport
    /// bar beneath both; medium keeps two columns but takes some of the sidebar's share for Now Playing, since a
    /// 200 px sidebar is already at its floor there and widening it would come out of the art; full is 75 / 25.
    /// </summary>
    public static ShellLayoutState For(double windowWidth) => windowWidth switch
    {
        < CompactThreshold => new ShellLayoutState(ShellLayoutMode.Compact, 2, 1, Stacked: true),
        < MediumThreshold => new ShellLayoutState(ShellLayoutMode.Medium, 4, 1, Stacked: false),
        _ => new ShellLayoutState(ShellLayoutMode.Full, 3, 1, Stacked: false),
    };

    /// <summary>
    /// True when <paramref name="clientWidth"/> is narrow enough that dividing it by the star weights would put a
    /// panel under its floor, so the floors win and the shares are no longer the documented ones.
    /// </summary>
    /// <remarks>
    /// This is not a failure, it is the floors doing their job — a 160 px sidebar is not a sidebar. It is worth
    /// naming because it is the difference between two true statements about the same layout: at 1600 px the shares
    /// are 75 / 25, and at 900 px they are not, and both are correct. Anything checking the proportions has to know
    /// which regime it is in, or it reports a bug against a rule that never applied.
    /// </remarks>
    public static bool FloorsBind(double clientWidth)
    {
        ShellLayoutState state = ShellLayout.For(clientWidth);
        if (state.Stacked)
        {
            return false; // stacked panels each span the width; nothing divides it
        }

        (double nowPlaying, double sidebar) = state.Fractions;
        return clientWidth * nowPlaying < NowPlayingMinWidth
            || clientWidth * sidebar < SidebarMinWidth;
    }
}
