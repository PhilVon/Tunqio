namespace Tunqio.Core.Visualization;

/// <summary>Renderer creation options (<c>mp_renderer_config</c>).</summary>
/// <param name="Width">Initial back-buffer width in physical pixels.</param>
/// <param name="Height">Initial back-buffer height in physical pixels.</param>
/// <param name="ScaleX">SwapChainPanel composition scale.</param>
/// <param name="ScaleY">SwapChainPanel composition scale.</param>
/// <param name="ForceWarp">Use the software rasteriser even when a GPU exists.</param>
/// <param name="VSync">Present once per refresh.</param>
/// <param name="Headless">Render into an offscreen texture instead of a swap chain (tests, benchmarks).</param>
public sealed record RendererConfig(
    int Width,
    int Height,
    float ScaleX = 1f,
    float ScaleY = 1f,
    bool ForceWarp = false,
    bool VSync = true,
    bool Headless = false);

/// <summary>Render thread statistics (<c>mp_render_stats</c>).</summary>
public sealed record RenderStats(
    long Frames,
    long Resizes,
    double Fps,
    TimeSpan FrameLast,
    TimeSpan FrameMax,
    TimeSpan FrameAverage,
    IReadOnlyList<int> FrameHistogram,
    long DxgiPresentCount,
    long DxgiMissedRefreshes,
    int Width,
    int Height,
    bool Warp,
    bool Headless,
    bool DeviceLost,
    bool Visible,
    string Adapter)
{
    /// <summary>Upper edges of <see cref="FrameHistogram"/> buckets in milliseconds; the last bucket is open-ended.</summary>
    public static IReadOnlyList<double> HistogramEdgesMs { get; } = [8.4, 16.7, 20.0, 33.4, 50.0];
}
