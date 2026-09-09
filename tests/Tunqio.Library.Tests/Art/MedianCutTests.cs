using Tunqio.Core.Library;
using Tunqio.Library.Art;

namespace Tunqio.Library.Tests.Art;

/// <summary>E3-S7 (AC-97): five colours, most populous first, each with its relative luminance.</summary>
public class MedianCutTests
{
    private static byte[] Bgra(int width, int height, Func<int, int, (byte R, byte G, byte B)> pixel)
    {
        var bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = pixel(x, y);
                int p = (y * width + x) * 4;
                bgra[p] = b;
                bgra[p + 1] = g;
                bgra[p + 2] = r;
                bgra[p + 3] = 255;
            }
        }

        return bgra;
    }

    [Fact]
    public void Four_flat_quadrants_give_four_populated_colours_and_one_filled_slot()
    {
        byte[] pixels = Bgra(100, 100, (x, y) => x < 50
            ? (y < 50 ? ((byte)220, (byte)30, (byte)30) : ((byte)30, (byte)30, (byte)220))
            : (y < 50 ? ((byte)30, (byte)200, (byte)30) : ((byte)250, (byte)250, (byte)250)));

        ArtPalette palette = MedianCut.Extract(pixels, 100, 100);

        palette.Colors.Should().HaveCount(ArtPalette.Size);
        palette.Colors.Take(4).Should().OnlyContain(c => Math.Abs(c.Population - 0.25) < 0.001, "each quadrant is a quarter of the pixels");
        palette.Colors[4].Population.Should().Be(0, "a fifth colour does not exist in the image, so its slot is filled");
        palette.Colors.Select(c => c.Population).Should().BeInDescendingOrder();
        palette.Colors.Sum(c => c.Population).Should().BeApproximately(1, 0.001);

        PaletteColor white = palette.Colors.Single(c => c.R > 240 && c.G > 240 && c.B > 240);
        white.Luminance.Should().BeGreaterThan(0.9);
        PaletteColor blue = palette.Colors.Single(c => c.B > 200 && c.R < 60);
        blue.Luminance.Should().BeLessThan(0.1);
        palette.Colors.Should().Contain(c => c.R > 200 && c.G < 60 && c.B < 60, "red survives quantisation");
        palette.Colors.Should().Contain(c => c.G > 180 && c.R < 60 && c.B < 60, "green survives quantisation");
        palette.Lightest().Should().Be(white);
        palette.Darkest().Population.Should().BeGreaterThan(0, "a real colour is preferred to a filled slot");
    }

    [Fact]
    public void A_gradient_fills_all_five_slots_with_real_colours()
    {
        byte[] pixels = Bgra(120, 80, (x, y) => ((byte)(x * 2), (byte)(y * 3), (byte)(128 + (x + y) % 100)));

        ArtPalette palette = MedianCut.Extract(pixels, 120, 80);

        palette.Colors.Should().HaveCount(ArtPalette.Size);
        palette.Colors.Should().OnlyContain(c => c.Population > 0.02);
        palette.Colors.Sum(c => c.Population).Should().BeApproximately(1, 0.001);
        palette.Colors.Select(c => c.Population).Should().BeInDescendingOrder();
        palette.Colors.Select(c => c.Hex).Distinct().Should().HaveCount(ArtPalette.Size, "the boxes cover different regions");
    }

    [Fact]
    public void A_flat_image_is_its_colour_followed_by_darker_shades()
    {
        byte[] pixels = Bgra(10, 10, (_, _) => ((byte)200, (byte)100, (byte)40));

        ArtPalette palette = MedianCut.Extract(pixels, 10, 10);

        palette.Dominant.Population.Should().Be(1);
        palette.Dominant.R.Should().BeInRange(196, 204, "the bin centre is within half a quantisation step");
        palette.Dominant.G.Should().BeInRange(96, 104);
        palette.Dominant.B.Should().BeInRange(36, 44);
        palette.Colors.Skip(1).Should().OnlyContain(c => c.Population == 0);
        palette.Colors.Select(c => c.Luminance).Should().BeInDescendingOrder("each fill is a darker shade of the last");
        palette.Colors.Should().HaveCount(ArtPalette.Size);
    }

    [Fact]
    public void Luminance_is_the_wcag_relative_luminance()
    {
        PaletteColor.RelativeLuminance(255, 255, 255).Should().BeApproximately(1, 1e-9);
        PaletteColor.RelativeLuminance(0, 0, 0).Should().Be(0);
        PaletteColor.RelativeLuminance(255, 0, 0).Should().BeApproximately(0.2126, 1e-9);
        PaletteColor.RelativeLuminance(0, 255, 0).Should().BeApproximately(0.7152, 1e-9);
        PaletteColor.RelativeLuminance(0, 0, 255).Should().BeApproximately(0.0722, 1e-9);
        PaletteColor.RelativeLuminance(128, 128, 128).Should().BeApproximately(0.2158, 0.001);
        new PaletteColor(1, 171, 255, 0, 0).Hex.Should().Be("#01abff");
    }

    [Fact]
    public void An_empty_image_still_yields_five_entries()
    {
        ArtPalette palette = MedianCut.Extract([], 0, 0);

        palette.Colors.Should().HaveCount(ArtPalette.Size);
        palette.Colors.Should().OnlyContain(c => c.Population == 0);
    }
}
