using Tunqio.App.Shell;

namespace Tunqio.App.Tests;

/// <summary>
/// T-182, Q-56: when the stacked shell gives Now Playing half the height, the art scales down to what is left rather
/// than being clipped at 360 px.
/// </summary>
public class NowPlayingArtLayoutTests
{
    [Fact]
    public void A_tall_panel_draws_the_art_at_its_full_size() =>
        NowPlayingArtLayout.ArtEdge(panelHeight: 800, panelWidth: 900, metadataHeight: 150)
            .Should().Be(NowPlayingArtLayout.MaxEdge);

    [Fact]
    public void A_short_panel_scales_the_art_down_to_the_height_left_over()
    {
        double edge = NowPlayingArtLayout.ArtEdge(panelHeight: 360, panelWidth: 780, metadataHeight: 150);

        edge.Should().Be(360 - 150 - NowPlayingArtLayout.Spacing - (2 * NowPlayingArtLayout.Clearance),
            "the art takes exactly the height the metadata block and the spacing leave, so nothing is clipped");
        edge.Should().BeLessThan(NowPlayingArtLayout.MaxEdge);
    }

    [Fact]
    public void A_very_short_panel_never_draws_the_art_below_the_floor() =>
        NowPlayingArtLayout.ArtEdge(panelHeight: 120, panelWidth: 300, metadataHeight: 150)
            .Should().Be(NowPlayingArtLayout.MinEdge);

    [Fact]
    public void A_narrow_panel_is_bounded_by_its_width_as_well() =>
        NowPlayingArtLayout.ArtEdge(panelHeight: 900, panelWidth: 200, metadataHeight: 150)
            .Should().Be(200 - (2 * NowPlayingArtLayout.Clearance));

    [Fact]
    public void A_panel_that_has_not_laid_out_yet_gets_the_full_size_rather_than_the_floor() =>
        NowPlayingArtLayout.ArtEdge(panelHeight: 0, panelWidth: 0, metadataHeight: 0)
            .Should().Be(NowPlayingArtLayout.MaxEdge);
}
