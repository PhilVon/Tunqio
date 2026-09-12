using Tunqio.Core.Visualization;

namespace Tunqio.Interop.Tests;

/// <summary>
/// Points the native preset scan at a directory for the life of the object. Process-wide, which is why
/// everything that creates a renderer shares the "native renderer" collection.
/// </summary>
internal sealed class PresetRootScope : IDisposable
{
    private const string Variable = "MPCORE_PRESET_ROOT";
    private readonly string? _previous;

    public PresetRootScope(string path)
    {
        _previous = Environment.GetEnvironmentVariable(Variable);
        Environment.SetEnvironmentVariable(Variable, path);
    }

    /// <summary>The Catch2 preset fixtures: two that compile, one that does not, two malformed manifests.</summary>
    public static string Fixtures => RepoPaths.File("native", "mpcore.tests", "fixtures", "presets");

    public static string Empty => Path.Combine(Path.GetTempPath(), "tunqio-interop-no-presets");

    public void Dispose() => Environment.SetEnvironmentVariable(Variable, _previous);
}

/// <summary>Headless (offscreen, WARP) renderer round trips through the managed wrapper.</summary>
[Collection("native renderer")]
public class NativeRendererTests
{
    [Fact]
    public void Headless_warp_renderer_reports_frames_and_adapter()
    {
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(new RendererConfig(320, 180, ForceWarp: true, VSync: false));
        Thread.Sleep(300);
        RenderStats stats = renderer.GetStats();
        stats.Frames.Should().BeGreaterThan(5);
        stats.Warp.Should().BeTrue();
        stats.Headless.Should().BeTrue();
        stats.DeviceLost.Should().BeFalse();
        stats.Adapter.Should().Contain("Basic Render");
        stats.FrameHistogram.Should().HaveCount(6);
        stats.FrameHistogram.Sum().Should().Be((int)stats.Frames - 1);
        RenderStats.HistogramEdgesMs.Should().HaveCount(5);
    }

    [Fact]
    public void Resize_storm_and_visibility_toggle_do_not_break_the_renderer()
    {
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(new RendererConfig(640, 360, ForceWarp: true, VSync: false));
        for (int i = 0; i < 100; i++)
        {
            renderer.Resize(320 + (i * 37) % 1600, 180 + (i * 53) % 900, i % 2 == 0 ? 1f : 1.25f, i % 2 == 0 ? 1f : 1.25f);
        }

        Thread.Sleep(150);
        renderer.SetVisible(false);
        Thread.Sleep(100);
        long paused = renderer.GetStats().Frames;
        Thread.Sleep(150);
        renderer.GetStats().Frames.Should().Be(paused);
        renderer.SetVisible(true);
        Thread.Sleep(150);
        RenderStats stats = renderer.GetStats();
        stats.Frames.Should().BeGreaterThan(paused);
        stats.DeviceLost.Should().BeFalse();
        stats.Resizes.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void Catalogue_holds_the_built_in_preset_even_with_no_preset_directory()
    {
        using var root = new PresetRootScope(PresetRootScope.Empty);
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(new RendererConfig(64, 64, ForceWarp: true, VSync: false));
        IReadOnlyList<PresetInfo> presets = renderer.EnumeratePresets();
        presets.Should().ContainSingle();
        presets[0].Id.Should().Be("builtin-bars");
        presets[0].Name.Should().NotBeEmpty();
    }

    [Fact]
    public void Presets_on_disk_join_the_catalogue_and_load()
    {
        using var root = new PresetRootScope(PresetRootScope.Fixtures);
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(new RendererConfig(64, 64, ForceWarp: true, VSync: false));
        IReadOnlyList<PresetInfo> presets = renderer.EnumeratePresets();
        presets.Select(p => p.Id).Should().Contain(["builtin-bars", "solid-green", "solid-blue", "broken-shader"]);
        presets.Select(p => p.Id).Should().NotContain("broken-json");

        renderer.SetPreset("solid-green");
        renderer.SetParameter("level", 0.25f);
        FluentActions.Invoking(() => renderer.SetParameter("no-such-parameter", 1f))
            .Should().Throw<NativeException>().WithMessage("*no-such-parameter*");
        FluentActions.Invoking(() => renderer.SetPreset("no-such-preset"))
            .Should().Throw<NativeException>().WithMessage("*no-such-preset*");
    }

    // AC-117 through the managed surface: the exception carries what the preset switcher has to show.
    [Fact]
    public void A_preset_that_does_not_compile_throws_the_compiler_message_and_keeps_rendering()
    {
        using var root = new PresetRootScope(PresetRootScope.Fixtures);
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(new RendererConfig(64, 64, ForceWarp: true, VSync: false));
        renderer.SetPreset("solid-green");
        long before = renderer.GetStats().Frames;

        PresetCompilationException thrown = FluentActions.Invoking(() => renderer.SetPreset("broken-shader"))
            .Should().Throw<PresetCompilationException>().Which;
        thrown.PresetId.Should().Be("broken-shader");
        thrown.CompilerMessage.Should().Contain("error X");
        thrown.CompilerMessage.Should().Contain("colour_that_was_never_declared");

        Thread.Sleep(120);
        RenderStats stats = renderer.GetStats();
        stats.Frames.Should().BeGreaterThan(before); // the render thread never stopped
        stats.DeviceLost.Should().BeFalse();
        renderer.SetPreset("solid-blue"); // and the failure left nothing behind
    }

    [Fact]
    public void The_quality_policy_crosses_the_boundary_and_the_tier_comes_back()
    {
        // Was Theme_and_quality_are_still_deferred_to_their_stories until E4-S7 implemented the last stub.
        // What the controller decides is mpcore.tests [quality]'s to prove; what this proves is the binding -
        // the policy goes out, and the 0.16 tail of mp_render_stats comes back into the managed struct at the
        // offsets the header put it at, which is the only thing a hand-written blittable mirror can get wrong.
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(new RendererConfig(64, 64, ForceWarp: true, VSync: false));
        FluentActions.Invoking(() => renderer.SetQuality((int)QualityPolicy.Auto)).Should().NotThrow();
        renderer.GetStats().Policy.Should().Be(QualityPolicy.Auto);
        renderer.GetStats().Tier.Should().Be(QualityTier.High); // nothing has been over budget

        renderer.SetQuality((int)QualityPolicy.Medium);
        Thread.Sleep(200);
        RenderStats stats = renderer.GetStats();
        stats.Policy.Should().Be(QualityPolicy.Medium);
        stats.Tier.Should().Be(QualityTier.Medium);
        stats.RenderScale.Should().BeApproximately(0.75f, 0.001f);
        stats.RenderWidth.Should().Be(48);
        stats.RenderHeight.Should().Be(48);
        stats.QualityChanges.Should().Be(0); // a tier the caller pinned is not the controller changing its mind
        stats.CostSource.Should().BeOneOf(RenderCostSource.GpuTimestamp, RenderCostSource.FrameInterval);

        // A value that is not a policy is refused by the core rather than turned into one.
        FluentActions.Invoking(() => renderer.SetQuality(9)).Should().Throw<NativeException>();
    }

    [Fact]
    public void Disposed_renderer_rejects_calls()
    {
        NativeRenderer renderer = NativeRenderer.CreateHeadless(new RendererConfig(64, 64, ForceWarp: true, VSync: false));
        renderer.Dispose();
        FluentActions.Invoking(renderer.GetStats).Should().Throw<ObjectDisposedException>();
        FluentActions.Invoking(renderer.Dispose).Should().NotThrow();
    }
}
