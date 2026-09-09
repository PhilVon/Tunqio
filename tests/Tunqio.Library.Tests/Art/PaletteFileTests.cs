using System.Text;
using System.Text.Json;
using Tunqio.Core.Library;
using Tunqio.Library.Art;

namespace Tunqio.Library.Tests.Art;

/// <summary>AC-97: the palette.json shape (five colours as hex, population and luminance) and its round trip.</summary>
public class PaletteFileTests
{
    private static ArtPalette Sample() => new(
    [
        new PaletteColor(18, 52, 86, 0.5, PaletteColor.RelativeLuminance(18, 52, 86)),
        new PaletteColor(255, 255, 255, 0.2, 1),
        new PaletteColor(0, 0, 0, 0.15, 0),
        new PaletteColor(200, 10, 10, 0.1, 0.13),
        new PaletteColor(10, 200, 10, 0.05, 0.5),
    ]);

    [Fact]
    public void Writes_five_colours_with_hex_population_and_luminance()
    {
        byte[] json = PaletteFile.Write(Sample());

        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("version").GetInt32().Should().Be(1);
        JsonElement colours = document.RootElement.GetProperty("colors");
        colours.GetArrayLength().Should().Be(5);
        colours[0].GetProperty("hex").GetString().Should().Be("#123456");
        colours[0].GetProperty("population").GetDouble().Should().Be(0.5);
        colours[0].GetProperty("luminance").GetDouble().Should().BeApproximately(PaletteColor.RelativeLuminance(18, 52, 86), 0.0001);
        colours[1].GetProperty("hex").GetString().Should().Be("#ffffff");
    }

    [Fact]
    public void Round_trips()
    {
        ArtPalette? read = PaletteFile.Read(PaletteFile.Write(Sample()));

        read.Should().NotBeNull();
        read!.Colors.Select(c => c.Hex).Should().Equal(Sample().Colors.Select(c => c.Hex));
        read.Colors.Select(c => c.Population).Should().Equal(Sample().Colors.Select(c => c.Population));
        read.Dominant.Should().Be(new PaletteColor(18, 52, 86, 0.5, Math.Round(PaletteColor.RelativeLuminance(18, 52, 86), 4)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"version\":2,\"colors\":[{\"hex\":\"#000000\",\"population\":1,\"luminance\":0}]}")]
    [InlineData("{\"version\":1,\"colors\":[]}")]
    [InlineData("{\"version\":1,\"colors\":[{\"hex\":\"000000\",\"population\":1,\"luminance\":0}]}")]
    [InlineData("{\"version\":1}")]
    public void Anything_else_reads_as_no_palette(string json)
    {
        PaletteFile.Read(Encoding.UTF8.GetBytes(json)).Should().BeNull();
    }
}
