using Tunqio.Core.Visualization;

namespace Tunqio.Interop.Tests;

/// <summary>Headless (offscreen, WARP) renderer round trips through the managed wrapper.</summary>
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
    public void Preset_surface_is_deferred_gracefully()
    {
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(new RendererConfig(64, 64, ForceWarp: true, VSync: false));
        renderer.EnumeratePresets().Should().BeEmpty();
        FluentActions.Invoking(() => renderer.SetPreset("spectrum-bars")).Should().Throw<NativeException>().WithMessage("*E4-S3*");
        FluentActions.Invoking(() => renderer.SetQuality(0)).Should().Throw<NativeException>().WithMessage("*E4-S7*");
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
