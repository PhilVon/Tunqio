using Tunqio.App.Shell;

namespace Tunqio.App.Tests;

/// <summary>
/// E2-S1, AC-68: the shell's proportions at 1600 px and its collapse at 700 px. Asserted against the table rather
/// than against a running window, which is the reason the table exists — a proportion living inside an
/// <c>AdaptiveTrigger</c> can only be checked by looking, and looking is not a regression test.
/// </summary>
public class ShellLayoutTests
{
    [Fact]
    public void At_1600_px_now_playing_and_the_sidebar_are_sixty_forty()
    {
        ShellLayoutState state = ShellLayout.For(1600);

        state.Mode.Should().Be(ShellLayoutMode.Full);
        state.Stacked.Should().BeFalse();
        (double nowPlaying, double sidebar) = state.Fractions;
        // T-182, Q-54: the controls became a bar under Now Playing, and the width their column freed went to the
        // sidebar. A first cut gave it to Now Playing at 75/25 and was rejected: the sidebar narrowed too far.
        nowPlaying.Should().BeApproximately(0.60, 0.001);
        sidebar.Should().BeApproximately(0.40, 0.001);
    }

    [Fact]
    public void At_700_px_the_panels_stack_and_share_the_height_equally()
    {
        ShellLayoutState state = ShellLayout.For(700);

        state.Mode.Should().Be(ShellLayoutMode.Compact);
        state.Stacked.Should().BeTrue();
        // T-182, Q-56: at 2:1 the library got about 230 px of an 800x900 window under the art and could not be
        // navigated. The art scales down to its half instead.
        state.Fractions.NowPlaying.Should().BeApproximately(0.5, 0.001);
        state.Fractions.Sidebar.Should().BeApproximately(0.5, 0.001);
    }

    [Fact]
    public void Between_the_breakpoints_the_sidebar_gives_some_of_its_share_to_now_playing()
    {
        ShellLayoutState medium = ShellLayout.For(1000);
        ShellLayoutState full = ShellLayout.For(1600);

        medium.Mode.Should().Be(ShellLayoutMode.Medium);
        medium.Stacked.Should().BeFalse("the columns survive down to the compact threshold");
        medium.Fractions.Sidebar.Should().BeApproximately(1.0 / 3.0, 0.001);
        medium.Fractions.Sidebar.Should().BeLessThan(full.Fractions.Sidebar);
        medium.Fractions.NowPlaying.Should().BeGreaterThan(full.Fractions.NowPlaying);
    }

    [Theory]
    [InlineData(799.9, ShellLayoutMode.Compact)]
    [InlineData(800, ShellLayoutMode.Medium)]
    [InlineData(1199.9, ShellLayoutMode.Medium)]
    [InlineData(1200, ShellLayoutMode.Full)]
    public void The_thresholds_are_where_the_document_puts_them(double width, ShellLayoutMode expected) =>
        ShellLayout.For(width).Mode.Should().Be(expected);

    [Fact]
    public void A_window_narrower_than_anything_still_has_a_shape()
    {
        ShellLayoutState state = ShellLayout.For(0);

        state.Mode.Should().Be(ShellLayoutMode.Compact);
        state.Fractions.NowPlaying.Should().BeGreaterThan(0, "a zero-width window is a transient during start-up, not a reason to divide by zero");
    }

    [Fact]
    public void Every_column_shape_leaves_room_for_what_the_panel_has_to_show()
    {
        double floors = ShellLayout.NowPlayingMinWidth + ShellLayout.SidebarMinWidth;

        floors.Should().BeLessThanOrEqualTo(
            ShellLayout.CompactThreshold,
            "below the compact threshold the panels stack, so both floors must fit above it or a medium window cannot honour them");
    }

    [Fact]
    public void The_transport_bar_is_never_narrower_than_the_transport()
    {
        // T-182. The bar is as wide as the Now Playing column it sits under, so the narrowest that column can be is
        // the narrowest the transport will ever get. As a third column it went down to 128 px and clipped Shuffle.
        ShellLayout.NowPlayingMinWidth.Should().BeGreaterThanOrEqualTo(
            ShellLayout.TransportMinWidth,
            "a Now Playing floor under the transport's own width would bring back the clipping T-182 removed");
    }

    [Theory]
    [InlineData(700)]
    [InlineData(800)]
    [InlineData(900)]
    [InlineData(1199.9)]
    [InlineData(1200)]
    [InlineData(1600)]
    public void At_the_documented_shares_no_floor_ever_binds(double width)
    {
        // The narrowest column shape is an 800 px client at 2:1: 533 / 267 px, above both floors. Under the old
        // 4:1:1 a 900 px window put the sidebar at 150 px and the floor had to take over; that regime is gone.
        ShellLayout.FloorsBind(width).Should().BeFalse("the shares alone keep both panels above their floors at every width");
    }

    // ---- E5-S1: modes ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(700)]
    [InlineData(1000)]
    [InlineData(1600)]
    public void Focus_gives_now_playing_the_whole_width_and_hides_the_sidebar(double width)
    {
        ShellLayoutState focus = ShellLayout.For(width, ShellMode.Focus);

        focus.SidebarShown.Should().BeFalse();
        focus.Fractions.NowPlaying.Should().Be(1);
        focus.Fractions.Sidebar.Should().Be(0);
        focus.Mode.Should().Be(ShellLayout.For(width).Mode, "Focus changes the shares, not which shape the width is in");
    }

    [Theory]
    [InlineData(700)]
    [InlineData(1000)]
    [InlineData(1600)]
    public void Discovery_is_the_width_table(double width)
    {
        ShellLayout.For(width, ShellMode.Discovery).Should().Be(ShellLayout.For(width));
    }

    [Fact]
    public void Curation_gives_the_library_side_the_larger_share_wherever_the_panels_are_columns()
    {
        // Q-67: Phil chose to invert the shares, because the library side is where Curation's dual pane goes.
        ShellLayoutState full = ShellLayout.For(1600, ShellMode.Curation);
        full.Fractions.NowPlaying.Should().BeApproximately(0.40, 0.001);
        full.Fractions.Sidebar.Should().BeApproximately(0.60, 0.001);
        full.SidebarShown.Should().BeTrue();

        ShellLayoutState medium = ShellLayout.For(1000, ShellMode.Curation);
        medium.Fractions.NowPlaying.Should().BeApproximately(1.0 / 3.0, 0.001);
        medium.Fractions.Sidebar.Should().BeApproximately(2.0 / 3.0, 0.001);
    }

    [Fact]
    public void Curation_stacked_keeps_the_equal_heights()
    {
        ShellLayout.For(700, ShellMode.Curation).Should().Be(ShellLayout.For(700), "Q-67 inverts the columns, not the rows");
    }

    [Theory]
    [InlineData(800, true)]
    [InlineData(1000, true)]
    [InlineData(1199.9, true)]
    [InlineData(1200, false)]
    [InlineData(1600, false)]
    public void In_curation_now_playings_floor_wins_below_1200_px(double width, bool binds)
    {
        // 1 : 2 of 1000 px is 333 px of Now Playing against a floor of 400, which is also the transport bar's.
        ShellLayout.FloorsBind(width, ShellMode.Curation).Should().Be(binds);
    }

    [Theory]
    [InlineData(800)]
    [InlineData(1600)]
    public void A_hidden_sidebar_has_no_floor_to_bind(double width)
    {
        ShellLayout.FloorsBind(width, ShellMode.Focus).Should().BeFalse();
    }

    [Fact]
    public void A_mode_transition_takes_between_200_and_300_ms_and_none_under_reduced_motion()
    {
        ShellLayout.ModeTransition(animationsEnabled: true).Should()
            .BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200)).And.BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(300));
        ShellLayout.ModeTransition(animationsEnabled: false).Should().Be(TimeSpan.Zero, "flow 10: mode transitions become instant");
    }

    [Fact]
    public void The_sidebar_is_never_narrower_than_a_third_of_the_window_in_a_column_shape()
    {
        // Q-54, stated as the property Phil asked for rather than as the numbers that deliver it.
        foreach (double width in new[] { ShellLayout.CompactThreshold, 1000, ShellLayout.MediumThreshold - 0.1, ShellLayout.MediumThreshold, 1600 })
        {
            ShellLayout.For(width).Fractions.Sidebar.Should().BeGreaterThanOrEqualTo(1.0 / 3.0 - 0.001, $"at {width} px");
        }
    }
}
