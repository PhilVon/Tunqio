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

/// <summary>
/// Which analysis frame the renderer draws (<c>mp_av_sync_mode</c>, E4-S8). The analysis runs at MIX time and
/// the mixer is ahead of the loudspeaker by the output buffer, so a renderer drawing the newest frame is
/// showing audio that has not been heard yet. Compensation is therefore a deliberate delay, paid for out of
/// that buffer.
/// </summary>
public enum AvSyncMode
{
    /// <summary>The newest frame published. Right where there is no output buffer to compensate against.</summary>
    Newest = 0,

    /// <summary>The frame whose hop the listener is hearing. The core's default.</summary>
    Audible = 1,
}

/// <summary>
/// One presented frame's audio-to-picture accounting (<c>mp_latency_sample</c>, E4-S8). The story of this
/// record is <see cref="ErrorMs"/>; the rest is there so a number that surprises somebody can be taken apart
/// into the edge that produced it.
/// </summary>
/// <param name="FrameIndex">The renderer's own frame counter, so a gap in the drain is visible.</param>
/// <param name="AnalysisSequence">Which analysis frame this picture was drawn from.</param>
/// <param name="Redrawn">The same analysis frame as the picture before it: no new hop was chosen.</param>
/// <param name="Mode">The mode in force for this frame.</param>
/// <param name="ErrorMs">
/// How far BEHIND the listener the picture was, in milliseconds of audio. Positive is a late picture, showing
/// audio already heard; negative is an early one, showing audio mixed but not yet played. The picture's own
/// instant is the MIDDLE of the hop it was drawn from, which is 5.33 ms after the byte the frame is labelled
/// with - a third of a 60 Hz refresh interval, so it is arithmetic and not a rounding.
/// </param>
/// <param name="OutputBufferMs">
/// Mixed but not yet heard, in milliseconds: the compensation budget. Zero offline and on a device-less
/// engine, which is exactly why <see cref="AvSyncMode.Audible"/> costs nothing there.
/// </param>
/// <param name="AnalysisToSeenMs">
/// From the mixer finishing the drawn frame's hop to the render thread first seeing it: the analysis thread's
/// turnaround plus the beat between a 93.75 Hz publisher and a slower poller.
/// </param>
/// <param name="FrameAgeAtPresentMs">
/// From that first sight to the present that carried it. Deliberately NOT called the draw cost, because in
/// <see cref="AvSyncMode.Audible"/> it is mostly the compensation: a frame chosen for its age has been sitting
/// in the renderer's history since it arrived, and this reads as tens of milliseconds while the drawing itself
/// takes a fraction of one. In <see cref="AvSyncMode.Newest"/> it IS about the draw, except on a picture where
/// <see cref="Redrawn"/> is set - a repeated frame is at least a render tick old before it is drawn again.
/// </param>
public sealed record LatencySample(
    long FrameIndex,
    long AnalysisSequence,
    bool Redrawn,
    AvSyncMode Mode,
    double ErrorMs,
    double OutputBufferMs,
    double AnalysisToSeenMs,
    double FrameAgeAtPresentMs);
