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

/// <summary>
/// The tier the renderer is drawing at (E4-S7). <see cref="QualityPolicy.Auto"/> has no place here: auto is a
/// policy - "you choose" - and something is always being drawn at one of these three.
/// </summary>
public enum QualityTier
{
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>
/// Which measurement the quality controller is deciding on (<c>mp_render_cost_source</c>). The frame-to-frame
/// interval is not a measurement of how much work a frame is: with vsync it is pinned to the refresh whether
/// the GPU is idle or drowning. A timestamp pair around the draw is the work itself; the interval is the
/// fallback for a device that will not make the queries.
/// </summary>
public enum RenderCostSource
{
    GpuTimestamp = 0,
    FrameInterval = 1,
}

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
    string Adapter,
    QualityPolicy Policy = QualityPolicy.Auto,
    QualityTier Tier = QualityTier.High,
    int QualityChanges = 0,
    int RenderWidth = 0,
    int RenderHeight = 0,
    float RenderScale = 1f,
    TimeSpan FrameCost = default,
    RenderCostSource CostSource = RenderCostSource.GpuTimestamp)
{
    /// <summary>Upper edges of <see cref="FrameHistogram"/> buckets in milliseconds; the last bucket is open-ended.</summary>
    public static IReadOnlyList<double> HistogramEdgesMs { get; } = [8.4, 16.7, 20.0, 33.4, 50.0];
}
