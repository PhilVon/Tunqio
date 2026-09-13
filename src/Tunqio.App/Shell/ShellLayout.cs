namespace Tunqio.App.Shell;

/// <summary>Which of the three shapes the shell is in (docs/user-interface.md, "Window Size Adaptation").</summary>
public enum ShellLayoutMode
{
    /// <summary>Under 800 px: the panels stack, sharing the height equally, because side by side neither would be usable.</summary>
    Compact,

    /// <summary>800 to 1200 px: two columns at 2:1, Now Playing and the sidebar.</summary>
    Medium,

    /// <summary>1200 px and up: Now Playing 60, the sidebar 40.</summary>
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
/// The shares below are Phil's (T-182, Q-54 and Q-56): the first cut of that change gave the freed width to Now
/// Playing, at 75/25 and 80/20, and was rejected because the sidebar narrowed too far to browse.
/// </remarks>
/// <param name="Mode">Which shape this width is in.</param>
/// <param name="NowPlaying">Now Playing's star weight, along the axis the panels are laid out on.</param>
/// <param name="Sidebar">The sidebar's star weight.</param>
/// <param name="Stacked">True when the panels are rows rather than columns.</param>
/// <param name="SidebarShown">False in Focus, where Now Playing takes the whole width (E5-S1).</param>
/// <param name="CurationEditor">
/// True in Curation, where the sidebar's place holds the dual-pane editor instead of the library pane (E5-S4). The library
/// pane is collapsed rather than removed, as in Focus, so its page and back stack are there when Curation is left.
/// </param>
public readonly record struct ShellLayoutState(
    ShellLayoutMode Mode,
    double NowPlaying,
    double Sidebar,
    bool Stacked,
    bool SidebarShown = true,
    bool CurationEditor = false)
{
    /// <summary>The two shares as fractions of the space the panels divide, which is what "60 / 40" means.</summary>
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

    /// <summary>Below this the sidebar's share shrinks (docs/user-interface.md, <c>MEDIUM_THRESHOLD</c>).</summary>
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
    /// The shape for <paramref name="windowWidth"/>. Compact stacks Now Playing over the sidebar at equal heights with
    /// the transport bar beneath both; medium is two columns at 2:1; full is 60 / 40.
    /// </summary>
    public static ShellLayoutState For(double windowWidth) => windowWidth switch
    {
        // Equal rows (T-182, Q-56): at 2:1 the sidebar got about 230 px of an 800x900 window under the art, which
        // Phil found impossible to navigate. The art scales down to its row instead.
        < CompactThreshold => new ShellLayoutState(ShellLayoutMode.Compact, 1, 1, Stacked: true),
        < MediumThreshold => new ShellLayoutState(ShellLayoutMode.Medium, 2, 1, Stacked: false),
        _ => new ShellLayoutState(ShellLayoutMode.Full, 3, 2, Stacked: false),
    };

    /// <summary>How long a mode transition takes when animations are on (docs/ui-screens-and-flows.md: 200-300 ms).</summary>
    public static readonly TimeSpan ModeTransitionDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The shape for <paramref name="windowWidth"/> in <paramref name="mode"/> (E5-S1). Focus gives Now Playing the
    /// whole width at every size and hides the sidebar. Discovery is the width table above. Curation swaps the two
    /// shares wherever the panels are columns - 40 / 60 from 1200 px, 1 : 2 between 800 and 1200 - because the library
    /// side is where its dual pane goes (Phil, Q-67). Stacked it stays at equal heights. Between 800 and 1200 the
    /// inverted share puts Now Playing under its 400 px floor, and the floor wins (<see cref="FloorsBind(double, ShellMode)"/>).
    /// </summary>
    public static ShellLayoutState For(double windowWidth, ShellMode mode)
    {
        ShellLayoutState shape = For(windowWidth);
        return mode switch
        {
            ShellMode.Focus => shape with { NowPlaying = 1, Sidebar = 0, SidebarShown = false },
            ShellMode.Curation when !shape.Stacked => shape with { NowPlaying = shape.Sidebar, Sidebar = shape.NowPlaying, CurationEditor = true },
            ShellMode.Curation => shape with { CurationEditor = true },
            _ => shape,
        };
    }

    /// <summary>The mode transition's length: <see cref="ModeTransitionDuration"/>, or instant under reduced motion (flow 10).</summary>
    public static TimeSpan ModeTransition(bool animationsEnabled) =>
        animationsEnabled ? ModeTransitionDuration : TimeSpan.Zero;

    /// <summary>
    /// True when <paramref name="clientWidth"/> is narrow enough that dividing it by the star weights would put a
    /// panel under its floor, so the floors win and the shares are no longer the documented ones.
    /// </summary>
    /// <remarks>
    /// At today's shares this never happens in a column shape: the narrowest column shape is an 800 px client at
    /// 2:1, which is 533 / 267 px, above both floors. It is kept because the floors are what stops a future change
    /// to the shares from making a panel unusable, and anything checking the proportions has to know whether a floor
    /// won, or it reports a bug against a rule that did not apply.
    /// </remarks>
    public static bool FloorsBind(double clientWidth) => FloorsBind(clientWidth, ShellMode.Discovery);

    /// <summary>
    /// <see cref="FloorsBind(double)"/> for <paramref name="mode"/>. A hidden sidebar has no floor to bind, so Focus
    /// only ever asks about Now Playing, which has the whole width. Curation between 800 and 1200 px is where a floor
    /// does win: 1 : 2 of a 1000 px client is 333 px of Now Playing against a floor of 400.
    /// </summary>
    public static bool FloorsBind(double clientWidth, ShellMode mode)
    {
        ShellLayoutState state = ShellLayout.For(clientWidth, mode);
        if (state.Stacked)
        {
            return false; // stacked panels each span the width; nothing divides it
        }

        (double nowPlaying, double sidebar) = state.Fractions;
        return clientWidth * nowPlaying < NowPlayingMinWidth
            || (state.SidebarShown && clientWidth * sidebar < SidebarMinWidth);
    }
}
