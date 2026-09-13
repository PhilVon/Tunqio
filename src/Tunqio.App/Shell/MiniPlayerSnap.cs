namespace Tunqio.App.Shell;

/// <summary>A rectangle in physical screen pixels, as <c>AppWindow</c> and <c>DisplayArea</c> report them.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;
}

/// <summary>
/// Where the mini player snaps (E5-S6, docs/ui-screens-and-flows.md: "snap to corners"). A rule and not a behaviour of
/// the window, so it can be asserted without dragging one: a window dropped with its corner near a corner of the work
/// area goes into that corner, a margin in from the edges; anywhere else it stays where it was put.
/// </summary>
public static class MiniPlayerSnap
{
    /// <summary>The mini player's size in effective pixels (docs: 360 × 120).</summary>
    public const int Width = 360;

    /// <inheritdoc cref="Width"/>
    public const int Height = 120;

    /// <summary>How close to a corner position, on both axes, a drop has to be to snap.</summary>
    public const int SnapDistance = 48;

    /// <summary>How far in from the work area's edges a snapped window sits.</summary>
    public const int Margin = 12;

    /// <summary>
    /// The corner <paramref name="window"/> should move to, or null when it is not near one or is already there.
    /// Both axes have to be near: a window pushed against one edge halfway along is where somebody put it.
    /// </summary>
    public static (int X, int Y)? Snap(ScreenRect window, ScreenRect workArea)
    {
        int left = workArea.X + Margin;
        int top = workArea.Y + Margin;
        int right = workArea.Right - Margin - window.Width;
        int bottom = workArea.Bottom - Margin - window.Height;

        bool nearLeft = Math.Abs(window.X - left) <= SnapDistance;
        bool nearRight = Math.Abs(window.X - right) <= SnapDistance;
        bool nearTop = Math.Abs(window.Y - top) <= SnapDistance;
        bool nearBottom = Math.Abs(window.Y - bottom) <= SnapDistance;
        if (!(nearLeft || nearRight) || !(nearTop || nearBottom))
        {
            return null;
        }

        int x = nearLeft ? left : right;
        int y = nearTop ? top : bottom;
        return x == window.X && y == window.Y ? null : (x, y);
    }
}
