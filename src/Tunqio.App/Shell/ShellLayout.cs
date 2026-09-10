namespace Tunqio.App.Shell;

/// <summary>Which of the three shapes the shell is in (docs/user-interface.md, "Window Size Adaptation").</summary>
public enum ShellLayoutMode
{
    /// <summary>Under 800 px: the panels stack, because side by side none of them would be usable.</summary>
    Compact,

    /// <summary>800 to 1200 px: still three columns, but the sidebar gives its share to Now Playing.</summary>
    Medium,

    /// <summary>1200 px and up: the documented 60 / 25 / 15.</summary>
    Full,
}

/// <summary>
/// The shares and shape of the shell's three panels at one window width. A value, and the arithmetic is pure, so
/// the breakpoints can be asserted without a window: what a XAML-only <c>AdaptiveTrigger</c> would have hidden
/// inside the visual tree is a table here instead, and AC-68 is about the numbers.
/// </summary>
/// <param name="Mode">Which shape this width is in.</param>
/// <param name="NowPlaying">Now Playing's star weight, along the axis the panels are laid out on.</param>
/// <param name="Sidebar">The sidebar's star weight.</param>
/// <param name="Controls">
/// The controls panel's star weight, or null when it takes its natural size — which is what stacking means for it:
/// a transport bar across the bottom rather than a third of the height.
/// </param>
/// <param name="Stacked">True when the panels are rows rather than columns.</param>
public readonly record struct ShellLayoutState(
    ShellLayoutMode Mode,
    double NowPlaying,
    double Sidebar,
    double? Controls,
    bool Stacked)
{
    /// <summary>
    /// The shares as fractions of the space the starred panels divide, which is what the documented 60 / 25 / 15
    /// means. A controls panel taking its natural size is not in the division and reads as zero.
    /// </summary>
    public (double NowPlaying, double Sidebar, double Controls) Fractions
    {
        get
        {
            double controls = Controls ?? 0;
            double total = NowPlaying + Sidebar + controls;
            return total <= 0 ? (0, 0, 0) : (NowPlaying / total, Sidebar / total, controls / total);
        }
    }
}

/// <summary>
/// Where the shell's breakpoints are and what each side of them looks like (E2-S1). Separate from the window so the
/// table can be read and tested on its own: a layout rule that exists only as a <c>VisualState</c> can only be
/// checked by looking at a running window, and this one has a criterion about its proportions.
/// </summary>
public static class ShellLayout
{
    /// <summary>Below this the panels stack (docs/user-interface.md, <c>COMPACT_THRESHOLD</c>).</summary>
    public const double CompactThreshold = 800;

    /// <summary>Below this the sidebar is narrowed (docs/user-interface.md, <c>MEDIUM_THRESHOLD</c>).</summary>
    public const double MediumThreshold = 1200;

    /// <summary>Now Playing's floor in a three-column shape.</summary>
    public const double NowPlayingMinWidth = 400;

    /// <summary>The sidebar's floor: a library grid narrower than this shows one tile a row.</summary>
    public const double SidebarMinWidth = 200;

    /// <summary>The controls panel's floor: the transport buttons and the volume slider side by side.</summary>
    public const double ControlsMinWidth = 120;

    /// <summary>
    /// The shape for <paramref name="windowWidth"/>. Compact stacks Now Playing over the sidebar with the controls
    /// as a bar beneath both; medium keeps three columns but takes the sidebar's share for Now Playing, since a
    /// 200 px sidebar is already at its floor there and widening it would come out of the art; full is the
    /// documented 60 / 25 / 15.
    /// </summary>
    public static ShellLayoutState For(double windowWidth) => windowWidth switch
    {
        < CompactThreshold => new ShellLayoutState(ShellLayoutMode.Compact, 2, 1, null, Stacked: true),
        < MediumThreshold => new ShellLayoutState(ShellLayoutMode.Medium, 4, 1, 1, Stacked: false),
        _ => new ShellLayoutState(ShellLayoutMode.Full, 3, 1.25, 0.75, Stacked: false),
    };

    /// <summary>
    /// True when <paramref name="clientWidth"/> is narrow enough that dividing it by the star weights would put a
    /// panel under its floor, so the floors win and the shares are no longer the documented ones.
    /// </summary>
    /// <remarks>
    /// This is not a failure, it is the floors doing their job — a 150 px sidebar is not a sidebar. It is worth
    /// naming because it is the difference between two true statements about the same layout: at 1600 px the shares
    /// are 60 / 25 / 15, and at 900 px they are not, and both are correct. Anything checking the proportions has to
    /// know which regime it is in, or it reports a bug against a rule that never applied.
    /// </remarks>
    public static bool FloorsBind(double clientWidth)
    {
        ShellLayoutState state = ShellLayout.For(clientWidth);
        if (state.Stacked)
        {
            return false; // stacked panels each span the width; nothing divides it
        }

        (double nowPlaying, double sidebar, double controls) = state.Fractions;
        return clientWidth * nowPlaying < NowPlayingMinWidth
            || clientWidth * sidebar < SidebarMinWidth
            || clientWidth * controls < ControlsMinWidth;
    }
}
