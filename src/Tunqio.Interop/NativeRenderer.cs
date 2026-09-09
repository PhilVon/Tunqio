using System.Text;
using Tunqio.Core.Visualization;

namespace Tunqio.Interop;

/// <summary>
/// Typed wrapper over the <c>mp_renderer_*</c> exports. Create on the UI thread with the SwapChainPanel's
/// IUnknown pointer (the shell obtains it; Interop never references WinUI), or headless for tests.
/// </summary>
public sealed unsafe class NativeRenderer : IDisposable
{
    private nint _handle;

    private NativeRenderer(nint handle) => _handle = handle;

    public nint Handle => _handle;

    /// <summary>Creates a renderer bound to a SwapChainPanel (<paramref name="swapChainPanelNative"/> is its IUnknown).</summary>
    public static NativeRenderer Create(nint swapChainPanelNative, RendererConfig config, NativeEngine? engine = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        var native = new MpRendererConfig
        {
            StructSize = (uint)sizeof(MpRendererConfig),
            Width = (uint)Math.Max(0, config.Width),
            Height = (uint)Math.Max(0, config.Height),
            ScaleX = config.ScaleX,
            ScaleY = config.ScaleY,
            ForceWarp = config.ForceWarp ? (byte)1 : (byte)0,
            VSync = config.VSync ? (byte)1 : (byte)0,
            Headless = config.Headless ? (byte)1 : (byte)0,
        };
        nint handle;
        NativeException.ThrowIfFailed(
            NativeMethods.RendererCreate(engine?.Handle ?? nint.Zero, (void*)swapChainPanelNative, &native, &handle),
            "mp_renderer_create");
        return new NativeRenderer(handle);
    }

    /// <summary>Creates an offscreen renderer (no panel), for tests and benchmarks.</summary>
    public static NativeRenderer CreateHeadless(RendererConfig config) =>
        Create(nint.Zero, config with { Headless = true });

    public void Resize(int width, int height, float scaleX, float scaleY) =>
        NativeException.ThrowIfFailed(NativeMethods.RendererResize(RequireHandle(), (uint)Math.Max(1, width), (uint)Math.Max(1, height), scaleX, scaleY), "mp_renderer_resize");

    public void SetVisible(bool visible) =>
        NativeException.ThrowIfFailed(NativeMethods.RendererSetVisible(RequireHandle(), visible ? (byte)1 : (byte)0), "mp_renderer_set_visible");

    public RenderStats GetStats()
    {
        var s = new MpRenderStats { StructSize = (uint)sizeof(MpRenderStats) };
        NativeException.ThrowIfFailed(NativeMethods.RendererGetStats(RequireHandle(), &s), "mp_renderer_get_stats");
        var histogram = new int[MpRenderStats.HistogramBuckets];
        for (int i = 0; i < histogram.Length; i++)
        {
            histogram[i] = (int)s.FrameMsHistogram[i];
        }

        int adapterLength = new ReadOnlySpan<byte>(s.Adapter, 128).IndexOf((byte)0);
        string adapter = Encoding.UTF8.GetString(s.Adapter, adapterLength < 0 ? 128 : adapterLength);
        return new RenderStats(
            (long)s.Frames,
            (long)s.Resizes,
            s.Fps,
            TimeSpan.FromMilliseconds(s.FrameMsLast),
            TimeSpan.FromMilliseconds(s.FrameMsMax),
            TimeSpan.FromMilliseconds(s.FrameMsAvg),
            histogram,
            (long)s.DxgiPresentCount,
            (long)s.DxgiMissedRefreshes,
            (int)s.Width,
            (int)s.Height,
            s.Warp != 0,
            s.Headless != 0,
            s.DeviceLost != 0,
            s.Visible != 0,
            adapter);
    }

    /// <summary>Presets are E4-S3; until then this reports an empty list rather than throwing.</summary>
    public IReadOnlyList<string> EnumeratePresets()
    {
        uint count = 0;
        MpResult result = NativeMethods.RendererEnumPresets(RequireHandle(), null, &count);
        if (result == MpResult.State)
        {
            return [];
        }

        NativeException.ThrowIfFailed(result, "mp_renderer_enum_presets");
        return [];
    }

    public void SetPreset(string id)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(id + "\0");
        fixed (byte* p = utf8)
        {
            NativeException.ThrowIfFailed(NativeMethods.RendererSetPreset(RequireHandle(), p), "mp_renderer_set_preset");
        }
    }

    public void SetParameter(string name, float value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = utf8)
        {
            NativeException.ThrowIfFailed(NativeMethods.RendererSetParam(RequireHandle(), p, value), "mp_renderer_set_param");
        }
    }

    public void SetThemeColors(ReadOnlySpan<float> primary, ReadOnlySpan<float> secondary, ReadOnlySpan<float> accent, ReadOnlySpan<float> background)
    {
        var colors = new MpThemeColors { StructSize = (uint)sizeof(MpThemeColors) };
        primary[..4].CopyTo(new Span<float>(colors.Primary, 4));
        secondary[..4].CopyTo(new Span<float>(colors.Secondary, 4));
        accent[..4].CopyTo(new Span<float>(colors.Accent, 4));
        background[..4].CopyTo(new Span<float>(colors.Background, 4));
        NativeException.ThrowIfFailed(NativeMethods.RendererSetTheme(RequireHandle(), &colors), "mp_renderer_set_theme");
    }

    public void SetQuality(int policy) =>
        NativeException.ThrowIfFailed(NativeMethods.RendererSetQuality(RequireHandle(), (MpQualityPolicy)policy), "mp_renderer_set_quality");

    public void Dispose()
    {
        nint handle = Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle != nint.Zero)
        {
            _ = NativeMethods.RendererDestroy(handle);
        }
    }

    private nint RequireHandle()
    {
        nint handle = _handle;
        ObjectDisposedException.ThrowIf(handle == nint.Zero, this);
        return handle;
    }
}
