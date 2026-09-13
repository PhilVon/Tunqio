using Tunqio.App.Shell;

namespace Tunqio.App.Tests;

/// <summary>E5-S6: dragging the mini player near a corner of the screen snaps it into that corner.</summary>
public class MiniPlayerSnapTests
{
    // A 1920 x 1040 work area (a 1080p screen less a 40 px taskbar) and a 360 x 120 window.
    private static readonly ScreenRect WorkArea = new(0, 0, 1920, 1040);

    private static ScreenRect At(int x, int y) => new(x, y, MiniPlayerSnap.Width, MiniPlayerSnap.Height);

    [Theory]
    [InlineData(30, 20, 12, 12)]                  // top left
    [InlineData(1530, 5, 1548, 12)]               // top right: 1920 - 12 - 360
    [InlineData(0, 900, 12, 908)]                 // bottom left: 1040 - 12 - 120
    [InlineData(1560, 930, 1548, 908)]            // bottom right
    public void A_drop_near_a_corner_goes_into_that_corner(int x, int y, int wantX, int wantY)
    {
        MiniPlayerSnap.Snap(At(x, y), WorkArea).Should().Be((wantX, wantY));
    }

    [Fact]
    public void A_drop_in_the_middle_stays_where_it_was_put()
    {
        MiniPlayerSnap.Snap(At(800, 460), WorkArea).Should().BeNull();
    }

    [Fact]
    public void Near_one_edge_but_not_a_corner_is_not_a_corner()
    {
        MiniPlayerSnap.Snap(At(12, 460), WorkArea).Should().BeNull("pushed against the left edge halfway down is a place somebody chose");
    }

    [Fact]
    public void A_window_already_in_its_corner_is_not_moved_again()
    {
        MiniPlayerSnap.Snap(At(12, 12), WorkArea).Should().BeNull("snapping must not fire the position-changed event that asked for it forever");
    }

    [Fact]
    public void The_work_area_can_start_anywhere_on_a_second_monitor()
    {
        var secondScreen = new ScreenRect(1920, -200, 2560, 1400);

        MiniPlayerSnap.Snap(At(1950, -180), secondScreen).Should().Be((1932, -188));
    }

    [Fact]
    public void Just_beyond_the_snap_distance_does_not_snap()
    {
        MiniPlayerSnap.Snap(At(12 + MiniPlayerSnap.SnapDistance + 1, 12), WorkArea).Should().BeNull();
    }
}
