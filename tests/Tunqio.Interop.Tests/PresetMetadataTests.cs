using Tunqio.Core.Visualization;

namespace Tunqio.Interop.Tests;

/// <summary>
/// T-142 and AC-133 through the managed wrapper: the parameter metadata a settings page builds its controls
/// from, the second preset root, and the refresh. The native suite proves the ABI; what these prove is the
/// marshalling - the fixed-size UTF-8 fields, the packed choice labels, the flags - and that
/// <see cref="VisualizationHost"/> keeps its cached catalogue in step with the core's.
/// </summary>
[Collection("native renderer")]
public class PresetMetadataTests
{
    /// <summary>A complete preset in <paramref name="dir"/>, declaring every kind of metadata there is.</summary>
    private static void WritePreset(string dir, string id, string name)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "solid.hlsl"), """
struct VSOut { float4 pos : SV_Position; };
VSOut VSMain(uint vid : SV_VertexID) {
    float2 corners[3] = { float2(-1.0, -3.0), float2(-1.0, 1.0), float2(3.0, 1.0) };
    VSOut o;
    o.pos = float4(corners[vid], 0.0, 1.0);
    return o;
}
float4 PSMain(VSOut i) : SV_Target { return float4(1.0, 0.0, 0.0, 1.0); }
""");
        File.WriteAllText(Path.Combine(dir, "preset.json"), $$"""
{
  "schema": 1,
  "id": "{{id}}",
  "name": "{{name}}",
  "shader": "solid.hlsl",
  "vertex_count": 3,
  "instance_count": 1,
  "parameters": [
    { "name": "count", "label": "Count", "default": 64.0, "min": 8.0, "max": 128.0, "step": 1.0 },
    { "name": "thickness", "label": "Thickness", "unit": "px", "default": 2.5, "min": 1.0, "max": 8.0 },
    { "name": "mode", "label": "Mode", "default": 0.0, "min": 0.0, "max": 2.0,
      "choices": ["First", "Second", "Third"] },
    { "name": "art_primary", "default": -1.0, "min": -1.0, "max": 16777215.0, "hidden": true }
  ]
}
""");
    }

    private static string Scratch(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tunqio-interop-user-presets", name + "-" + Environment.ProcessId);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Every_shipped_preset_arrives_with_labels_and_ranges()
    {
        using var scope = new PresetRootScope(RepoPaths.File("presets"));
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(new RendererConfig(64, 64, ForceWarp: true, VSync: false));

        IReadOnlyList<PresetInfo> presets = renderer.EnumeratePresets();
        presets.Should().Contain(p => p.Id == "spectrum-bars");

        IReadOnlyList<PresetParameter> bars = renderer.EnumerateParameters("spectrum-bars");
        bars.Should().HaveCount(5); // bars, smoothing, colour, gain, and T-162's theme_mix
        PresetParameter count = bars.Single(p => p.Name == "bars");
        count.Label.Should().Be("Bars");
        count.Minimum.Should().Be(8f);
        count.Maximum.Should().Be(128f);
        count.Default.Should().Be(64f);
        count.Step.Should().Be(1f);
        count.Hidden.Should().BeFalse();
        count.IsChoice.Should().BeFalse();

        // The colour source is the parameter a page would otherwise show as a slider from 0 to 2.
        PresetParameter colour = bars.Single(p => p.Name == "colour");
        colour.IsChoice.Should().BeTrue();
        colour.Choices.Should().Equal("Position", "Loudness", "Spectral centroid");
    }

    // T-162. Every preset on disk now draws with the renderer-wide theme, and theme_mix is how somebody who
    // wants a preset's own palette back gets it. That only means anything if a settings page can find it, so
    // what is asserted here is the metadata E4-S9 binds to - visible, labelled, and a plain 0..1.
    [Fact]
    public void Every_shipped_preset_offers_the_theme_opt_out()
    {
        using var scope = new PresetRootScope(RepoPaths.File("presets"));
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(new RendererConfig(64, 64, ForceWarp: true, VSync: false));

        foreach (PresetInfo preset in renderer.EnumeratePresets().Where(p => p.Id != "builtin-bars"))
        {
            PresetParameter mix = renderer.EnumerateParameters(preset.Id).Single(p => p.Name == "theme_mix");
            mix.Label.Should().Be("Follow app theme", preset.Id);
            mix.Minimum.Should().Be(0f, preset.Id);
            mix.Maximum.Should().Be(1f, preset.Id);
            // Default 1: the theme applies without anybody opting in, which is the whole point of T-162.
            mix.Default.Should().Be(1f, preset.Id);
            mix.Hidden.Should().BeFalse(preset.Id);
            mix.IsChoice.Should().BeFalse(preset.Id);
        }
    }

    [Fact]
    public void Ambient_glows_art_parameters_are_the_only_hidden_ones_and_they_are_all_hidden()
    {
        using var scope = new PresetRootScope(RepoPaths.File("presets"));
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(new RendererConfig(64, 64, ForceWarp: true, VSync: false));

        IReadOnlyList<PresetParameter> glow = renderer.EnumerateParameters("ambient-glow");
        glow.Where(p => p.Hidden).Select(p => p.Name)
            .Should().Equal("art_primary", "art_secondary", "art_accent");

        // And the claim that makes the flag worth having: what is left is offerable. 16777215 is a packed sRGB
        // colour, and nothing a person sees carries one.
        glow.Where(p => !p.Hidden).Should().OnlyContain(p => p.Maximum <= 1000f && p.Label.Length > 0);

        foreach (PresetInfo preset in renderer.EnumeratePresets().Where(p => p.Id != "ambient-glow"))
        {
            renderer.EnumerateParameters(preset.Id).Should().OnlyContain(p => !p.Hidden, preset.Id);
        }
    }

    [Fact]
    public void Parameters_can_be_read_for_a_preset_that_is_not_the_one_drawing()
    {
        using var scope = new PresetRootScope(RepoPaths.File("presets"));
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(new RendererConfig(64, 64, ForceWarp: true, VSync: false));

        // The renderer starts on the compiled-in preset, and a settings page describes a preset before it
        // switches to it. Nothing here has switched.
        renderer.EnumerateParameters("builtin-bars").Should().ContainSingle(p => p.Name == "gain");
        renderer.EnumerateParameters("waveform").Should().Contain(p => p.Name == "thickness" && p.Unit == "px");
    }

    [Fact]
    public void An_unknown_preset_id_is_an_exception_that_names_it()
    {
        using var scope = new PresetRootScope(PresetRootScope.Fixtures);
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(new RendererConfig(64, 64, ForceWarp: true, VSync: false));

        Action read = () => renderer.EnumerateParameters("no-such-preset");
        read.Should().Throw<NativeException>().WithMessage("*no-such-preset*");
    }

    [Fact]
    public async Task A_user_preset_appears_after_a_refresh_and_not_before_one()
    {
        using var scope = new PresetRootScope(RepoPaths.File("presets"));
        string userRoot = Scratch("refresh");
        try
        {
            using var host = new VisualizationHost();
            await host.AttachAsync(nint.Zero, new RendererConfig(64, 64, ForceWarp: true, VSync: false, Headless: true));
            int shipped = host.Presets.Count;

            host.SetUserPresetRoot(userRoot);
            host.Presets.Should().HaveCount(shipped, "an empty user root adds nothing");

            // AC-133: dropped in while the app is running.
            WritePreset(Path.Combine(userRoot, "mine"), "mine", "My Preset");
            host.Presets.Should().HaveCount(shipped, "the catalogue is read once; that is what the refresh is for");

            IReadOnlyList<PresetInfo> after = host.RefreshPresets();
            after.Should().HaveCount(shipped + 1);
            after.Should().Contain(p => p.Id == "mine" && p.Name == "My Preset");
            host.Presets.Should().Contain(p => p.Id == "mine", "the host's own cache moves with the core's");

            // And it is a preset like any other: its metadata is discoverable and it loads.
            IReadOnlyList<PresetParameter> mine = host.GetPresetParameters("mine");
            mine.Should().HaveCount(4);
            mine.Single(p => p.Name == "mode").Choices.Should().Equal("First", "Second", "Third");
            mine.Single(p => p.Name == "art_primary").Hidden.Should().BeTrue();
            await host.SetPresetAsync("mine");
            host.ActivePresetId.Should().Be("mine");
        }
        finally
        {
            Directory.Delete(userRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_rescan_that_loses_the_active_preset_leaves_it_drawing()
    {
        using var scope = new PresetRootScope(RepoPaths.File("presets"));
        string userRoot = Scratch("vanish");
        try
        {
            using var host = new VisualizationHost();
            await host.AttachAsync(nint.Zero, new RendererConfig(64, 64, ForceWarp: true, VSync: false, Headless: true));
            WritePreset(Path.Combine(userRoot, "doomed"), "doomed", "Doomed");
            host.SetUserPresetRoot(userRoot);
            await host.SetPresetAsync("doomed");

            Directory.Delete(Path.Combine(userRoot, "doomed"), recursive: true);
            host.RefreshPresets().Should().NotContain(p => p.Id == "doomed");

            // The compiled preset is still the renderer's, which is what stops a deleted folder blanking the
            // panel. ActivePresetId is deliberately still it, rather than a null the shell would read as "no
            // picture" while the picture is on screen.
            host.ActivePresetId.Should().Be("doomed");
            Thread.Sleep(100);
            host.TryGetStats().Should().NotBeNull();
            host.TryGetStats()!.DeviceLost.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(userRoot, recursive: true);
        }
    }

    [Fact]
    public async Task A_user_preset_cannot_take_a_shipped_presets_id()
    {
        using var scope = new PresetRootScope(RepoPaths.File("presets"));
        string userRoot = Scratch("impostor");
        try
        {
            using var host = new VisualizationHost();
            await host.AttachAsync(nint.Zero, new RendererConfig(64, 64, ForceWarp: true, VSync: false, Headless: true));
            int shipped = host.Presets.Count;

            WritePreset(Path.Combine(userRoot, "impostor"), "spectrum-bars", "Not the real one");
            host.SetUserPresetRoot(userRoot);

            host.Presets.Should().HaveCount(shipped);
            host.Presets.Single(p => p.Id == "spectrum-bars").Name.Should().Be("Spectrum Bars");
            // And it is the shipped one's parameters that answer, not the impostor's.
            host.GetPresetParameters("spectrum-bars").Should().Contain(p => p.Name == "bars");
        }
        finally
        {
            Directory.Delete(userRoot, recursive: true);
        }
    }
}
