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

        string adapter = Utf8(s.Adapter, 128);
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

    /// <summary>
    /// The preset catalogue: the core's built-in preset first, then every well-formed <c>preset.json</c> under
    /// the preset directory, by id. Two calls, as the ABI asks - one for the count, one for the entries.
    /// </summary>
    public IReadOnlyList<PresetInfo> EnumeratePresets()
    {
        nint handle = RequireHandle();
        uint count = 0;
        NativeException.ThrowIfFailed(NativeMethods.RendererEnumPresets(handle, null, &count), "mp_renderer_enum_presets");
        if (count == 0)
        {
            return [];
        }

        var native = new MpPresetInfo[count];
        fixed (MpPresetInfo* first = native)
        {
            for (uint i = 0; i < count; i++)
            {
                first[i].StructSize = (uint)sizeof(MpPresetInfo);
            }

            NativeException.ThrowIfFailed(NativeMethods.RendererEnumPresets(handle, first, &count), "mp_renderer_enum_presets");
            var presets = new PresetInfo[count];
            for (uint i = 0; i < count; i++)
            {
                presets[i] = new PresetInfo(Utf8(first[i].Id, 64), Utf8(first[i].Name, 128));
            }

            return presets;
        }
    }

    /// <summary>
    /// What one preset declares (<c>mp_renderer_enum_preset_params</c>, T-142). Two calls, the same protocol as
    /// <see cref="EnumeratePresets"/>, and about any preset in the catalogue rather than the active one.
    /// </summary>
    public IReadOnlyList<PresetParameter> EnumerateParameters(string presetId)
    {
        ArgumentException.ThrowIfNullOrEmpty(presetId);
        nint handle = RequireHandle();
        byte[] utf8 = Encoding.UTF8.GetBytes(presetId + "\0");
        fixed (byte* id = utf8)
        {
            uint count = 0;
            NativeException.ThrowIfFailed(NativeMethods.RendererEnumPresetParams(handle, id, null, &count), "mp_renderer_enum_preset_params");
            if (count == 0)
            {
                return [];
            }

            var native = new MpPresetParamInfo[count];
            fixed (MpPresetParamInfo* first = native)
            {
                for (uint i = 0; i < count; i++)
                {
                    first[i].StructSize = (uint)sizeof(MpPresetParamInfo);
                }

                NativeException.ThrowIfFailed(NativeMethods.RendererEnumPresetParams(handle, id, first, &count), "mp_renderer_enum_preset_params");
                var parameters = new PresetParameter[count];
                for (uint i = 0; i < count; i++)
                {
                    string choices = Utf8(first[i].Choices, 256);
                    parameters[i] = new PresetParameter(
                        Utf8(first[i].Name, 64),
                        Utf8(first[i].Label, 64),
                        Utf8(first[i].Unit, 16),
                        first[i].MinValue,
                        first[i].MaxValue,
                        first[i].DefaultValue,
                        first[i].Step,
                        (first[i].Flags & (uint)MpPresetParamFlags.Hidden) != 0,
                        choices.Length == 0 ? [] : choices.Split('|'));
                }

                return parameters;
            }
        }
    }

    /// <summary>The second preset root (<c>mp_renderer_set_user_preset_root</c>); null or empty removes it.</summary>
    public void SetUserPresetRoot(string? path)
    {
        nint handle = RequireHandle();
        byte[] utf8 = Encoding.UTF8.GetBytes((path ?? string.Empty) + "\0");
        fixed (byte* p = utf8)
        {
            NativeException.ThrowIfFailed(NativeMethods.RendererSetUserPresetRoot(handle, p), "mp_renderer_set_user_preset_root");
        }
    }

    /// <summary>Rereads both preset roots (<c>mp_renderer_rescan_presets</c>) and returns the new count.</summary>
    public int RescanPresets()
    {
        uint count = 0;
        NativeException.ThrowIfFailed(NativeMethods.RendererRescanPresets(RequireHandle(), &count), "mp_renderer_rescan_presets");
        return (int)count;
    }

    /// <summary>
    /// Switches preset. The core compiles the HLSL on this thread and only swaps it in if the device accepted
    /// it, so a <see cref="PresetCompilationException"/> from here means nothing changed - the preset that was
    /// drawing is still drawing - and <see cref="PresetCompilationException.CompilerMessage"/> is the shader
    /// compiler's own diagnostic, which is the only thing that says where the mistake is.
    /// </summary>
    public void SetPreset(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        byte[] utf8 = Encoding.UTF8.GetBytes(id + "\0");
        fixed (byte* p = utf8)
        {
            MpResult result = NativeMethods.RendererSetPreset(RequireHandle(), p);
            if (result == MpResult.D3D)
            {
                throw new PresetCompilationException(id, NativeEngineInfo.LastError());
            }

            NativeException.ThrowIfFailed(result, "mp_renderer_set_preset");
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

    /// <summary>A fixed-size UTF-8 field out of a native struct, up to its NUL.</summary>
    private static string Utf8(byte* field, int capacity)
    {
        int length = new ReadOnlySpan<byte>(field, capacity).IndexOf((byte)0);
        return Encoding.UTF8.GetString(field, length < 0 ? capacity : length);
    }

    private nint RequireHandle()
    {
        nint handle = _handle;
        ObjectDisposedException.ThrowIf(handle == nint.Zero, this);
        return handle;
    }
}
