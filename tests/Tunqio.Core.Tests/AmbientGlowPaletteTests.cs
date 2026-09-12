using System.Reactive.Subjects;
using Tunqio.Core.Library;
using Tunqio.Core.Visualization;

namespace Tunqio.Core.Tests;

/// <summary>
/// E4-S5 / AC-124: the managed half of the path from E3-S7's album art palette to the <c>ambient-glow</c>
/// preset's parameters. The native half - that the colours arrive, that they are what is drawn, and that every
/// "when available" failure still draws a picture - is <c>mpcore.tests [art]</c>, which needs a GPU.
/// </summary>
public class AmbientGlowPaletteTests
{
    private static PaletteColor Colour(byte r, byte g, byte b, double population = 0.4) =>
        new(r, g, b, population, PaletteColor.RelativeLuminance(r, g, b));

    private static ArtPalette Palette(params PaletteColor[] colours) => new(colours);

    [Fact]
    public void A_colour_packs_into_one_float_exactly()
    {
        AmbientGlowPalette.Pack(Colour(0, 0, 0)).Should().Be(0f);
        AmbientGlowPalette.Pack(Colour(1, 2, 3)).Should().Be(66_051f); // 65536 + 512 + 3
        // The largest packed value there is. float32 carries every integer below 2^24 exactly, which is the whole
        // reason for this encoding: a colour that survived the round trip all but one step would be invisible.
        AmbientGlowPalette.Pack(Colour(255, 255, 255)).Should().Be(16_777_215f);
        AmbientGlowPalette.Pack(Colour(255, 255, 254)).Should().Be(16_777_214f);
    }

    [Fact]
    public void Packed_colours_round_trip_at_the_corners_of_the_encoding()
    {
        // Not a sample: the corners and edges of the encoding, where a lost bit would hide.
        foreach ((byte r, byte g, byte b) in new (byte, byte, byte)[]
                 {
                     (0, 0, 0), (0, 0, 1), (0, 1, 0), (1, 0, 0), (255, 0, 0), (0, 255, 0), (0, 0, 255),
                     (255, 255, 255), (254, 255, 255), (128, 128, 128), (17, 34, 51),
                 })
        {
            float packed = AmbientGlowPalette.Pack(Colour(r, g, b));
            int value = (int)packed;
            ((float)value).Should().Be(packed, "the float has to be an exact integer");
            (value >> 16).Should().Be(r);
            ((value >> 8) & 0xFF).Should().Be(g);
            (value & 0xFF).Should().Be(b);
        }
    }

    [Fact]
    public void A_track_with_no_art_sends_the_no_art_value_in_all_three()
    {
        AmbientGlowPalette.Parameters(null).Should()
            .Be((AmbientGlowPalette.NoArt, AmbientGlowPalette.NoArt, AmbientGlowPalette.NoArt));
    }

    [Fact]
    public void A_palette_with_no_colours_at_all_is_the_same_as_no_art()
    {
        AmbientGlowPalette.Parameters(Palette()).Should()
            .Be((AmbientGlowPalette.NoArt, AmbientGlowPalette.NoArt, AmbientGlowPalette.NoArt));
    }

    [Fact]
    public void The_dominant_colour_leads()
    {
        ArtPalette palette = Palette(
            Colour(200, 30, 40, population: 0.5),
            Colour(30, 200, 40, population: 0.3),
            Colour(240, 240, 250, population: 0.2));

        (float primary, float secondary, float accent) = AmbientGlowPalette.Parameters(palette);
        primary.Should().Be(AmbientGlowPalette.Pack(Colour(200, 30, 40)));
        secondary.Should().Be(AmbientGlowPalette.Pack(Colour(30, 200, 40)));
        accent.Should().Be(AmbientGlowPalette.Pack(Colour(240, 240, 250)), "the lightest reads as the highlight");
    }

    [Fact]
    public void A_filled_out_slot_is_skipped_for_the_second_colour()
    {
        // An image with fewer than five distinct colours has its remaining slots filled with darker shades of the
        // dominant at population 0 (see ArtPalette). Those say nothing the dominant does not.
        ArtPalette palette = Palette(
            Colour(200, 30, 40, population: 0.8),
            Colour(100, 15, 20, population: 0.0),
            Colour(40, 180, 200, population: 0.2));

        (_, float secondary, _) = AmbientGlowPalette.Parameters(palette);
        secondary.Should().Be(AmbientGlowPalette.Pack(Colour(40, 180, 200)));
    }

    [Fact]
    public void A_single_colour_palette_uses_that_colour_three_times_rather_than_falling_back_to_no_art()
    {
        // One colour is still art. What to do with a colour too dark to be a glow is the shader's decision and
        // is made per colour there; this side does not second-guess the extractor.
        ArtPalette palette = Palette(Colour(12, 14, 18, population: 1.0));
        float packed = AmbientGlowPalette.Pack(Colour(12, 14, 18));
        AmbientGlowPalette.Parameters(palette).Should().Be((packed, packed, packed));
    }

    [Fact]
    public void Apply_sets_the_three_parameters_the_preset_declares()
    {
        RecordingHost host = new() { ActivePresetId = AmbientGlowPalette.PresetId };
        ArtPalette palette = Palette(Colour(200, 30, 40, 0.6), Colour(30, 200, 40, 0.4));

        AmbientGlowPalette.Apply(host, palette);

        host.Parameters.Should().HaveCount(3);
        host.Parameters.Select(p => p.Name).Should().Equal("art_primary", "art_secondary", "art_accent");
        host.Parameters[0].Value.Should().Be(AmbientGlowPalette.Pack(Colour(200, 30, 40)));
    }

    [Fact]
    public void Apply_with_no_art_still_sets_them_so_the_previous_track_s_colours_do_not_stay()
    {
        RecordingHost host = new() { ActivePresetId = AmbientGlowPalette.PresetId };
        AmbientGlowPalette.Apply(host, null);
        host.Parameters.Select(p => p.Value).Should().AllBeEquivalentTo(AmbientGlowPalette.NoArt);
    }

    [Fact]
    public void Apply_does_nothing_while_another_preset_is_running()
    {
        // The core refuses a parameter the active preset does not declare, and a track change while the waveform
        // is on screen is not an error.
        RecordingHost host = new() { ActivePresetId = "waveform" };
        AmbientGlowPalette.Apply(host, Palette(Colour(200, 30, 40)));
        host.Parameters.Should().BeEmpty();
    }

    private sealed class RecordingHost : IVisualizationHost
    {
        public List<(string Name, float Value)> Parameters { get; } = [];

        public bool IsAttached => true;

        public IReadOnlyList<PresetInfo> Presets { get; } = [];

        public string? ActivePresetId { get; init; }

        public IObservable<RenderStats> Stats { get; } = new Subject<RenderStats>();

        public Task AttachAsync(nint swapChainPanelNative, RendererConfig config) => Task.CompletedTask;

        public void Detach()
        {
        }

        public void Resize(int width, int height, float scaleX, float scaleY)
        {
        }

        public void SetVisible(bool visible)
        {
        }

        public Task SetPresetAsync(string id) => Task.CompletedTask;

        public void SetParameter(string name, float value) => Parameters.Add((name, value));

        public void SetThemeColors(ThemeColors colors) => throw new NotSupportedException("E4-S6");

        public void SetQualityPolicy(QualityPolicy policy) => throw new NotSupportedException("E4-S7");

        public void Dispose()
        {
        }
    }
}
