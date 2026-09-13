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
    public void At_1600_px_now_playing_and_the_sidebar_are_seventyfive_twentyfive()
    {
        ShellLayoutState state = ShellLayout.For(1600);

        state.Mode.Should().Be(ShellLayoutMode.Full);
        state.Stacked.Should().BeFalse();
        (double nowPlaying, double sidebar) = state.Fractions;
        // T-182: the controls panel is a bar under Now Playing rather than a 15% column, and the 15% went to Now
        // Playing, so the sidebar keeps the 25% it has always had.
        nowPlaying.Should().BeApproximately(0.75, 0.001);
        sidebar.Should().BeApproximately(0.25, 0.001);
    }

    [Fact]
    public void At_700_px_the_panels_stack_with_the_music_on_top()
    {
        ShellLayoutState state = ShellLayout.For(700);

        state.Mode.Should().Be(ShellLayoutMode.Compact);
        state.Stacked.Should().BeTrue();
        state.NowPlaying.Should().BeGreaterThan(state.Sidebar, "the music keeps the larger share when there is least room");
    }

    [Fact]
    public void Between_the_breakpoints_the_sidebar_gives_its_share_to_now_playing()
    {
        ShellLayoutState medium = ShellLayout.For(1000);
        ShellLayoutState full = ShellLayout.For(1600);

        medium.Mode.Should().Be(ShellLayoutMode.Medium);
        medium.Stacked.Should().BeFalse("the columns survive down to the compact threshold");
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

    [Fact]
    public void At_the_widths_the_criterion_names_the_shares_are_the_shares_and_no_floor_binds()
    {
        ShellLayout.FloorsBind(1600).Should().BeFalse("1600 px is wide enough for the documented shares to be the real ones");
        ShellLayout.FloorsBind(700).Should().BeFalse("stacked panels each span the width, so nothing divides it");
    }

    [Fact]
    public void Just_above_the_compact_threshold_the_floors_win_and_the_shares_are_not_the_documented_ones()
    {
        ShellLayout.FloorsBind(900).Should().BeTrue(
            "a fifth of 900 px is 180, and a 180 px sidebar is not a sidebar — the floor takes it to 200 and the shares move");

        ShellLayoutState state = ShellLayout.For(900);
        (900 * state.Fractions.Sidebar).Should().BeLessThan(
            ShellLayout.SidebarMinWidth, "which is exactly why the floor has to override the share here");
    }

    [Fact]
    public void The_floors_stop_binding_before_the_full_layout_starts()
    {
        ShellLayout.FloorsBind(ShellLayout.MediumThreshold).Should().BeFalse(
            "the documented 75/25 must be achievable from the moment the full layout applies, or it is not the layout");
    }
}
