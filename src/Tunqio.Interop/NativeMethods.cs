using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.SafeDirectories)]

namespace Tunqio.Interop;

/// <summary>
/// Source-generated bindings for every export in <c>mpcore.h</c>; one entry per export, all <c>__cdecl</c>,
/// pointers to blittable structs, no marshalling. Nothing outside this class names the native library.
/// <c>Tunqio.Interop.Tests</c> parses the header and fails when an export has no binding here.
/// </summary>
internal static unsafe partial class NativeMethods
{
    internal const string LibraryName = "mpcore";

    // Runs before the first P/Invoke on this class: the resolver refuses an mpcore.dll with the wrong ABI major.
    static NativeMethods() => NativeLibraryLoader.Install();

    // ---- version and errors ----

    [LibraryImport(LibraryName, EntryPoint = "mpcore_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint AbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "mp_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte* Version();

    [LibraryImport(LibraryName, EntryPoint = "mp_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult LastError(byte* buffer, nuint length);

    // ---- logging ----

    [LibraryImport(LibraryName, EntryPoint = "mp_log_set_sink")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult LogSetSink(delegate* unmanaged[Cdecl]<MpLogLevel, byte*, void*, void> sink, void* user, MpLogLevel minLevel);

    // ---- engine ----

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EngineCreate(MpEngineConfig* config, nint* outEngine);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EngineDestroy(nint engine);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_set_output")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EngineSetOutput(nint engine, MpOutputConfig* config);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_enum_devices")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EngineEnumDevices(nint engine, MpDeviceInfo* devices, uint* count);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_set_event_callback")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EngineSetEventCallback(nint engine, delegate* unmanaged[Cdecl]<MpEvent*, void*, void> callback, void* user);

    // ---- tracks ----

    [LibraryImport(LibraryName, EntryPoint = "mp_track_open")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult TrackOpen(nint engine, byte* utf8Path, nint* outTrack);

    [LibraryImport(LibraryName, EntryPoint = "mp_track_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult TrackClose(nint track);

    [LibraryImport(LibraryName, EntryPoint = "mp_track_get_info")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult TrackGetInfo(nint track, MpTrackInfo* info);

    // ---- transport ----

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_play")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EnginePlay(nint engine, nint track, long startMs);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_preload_next")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EnginePreloadNext(nint engine, nint next);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_pause")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EnginePause(nint engine);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_resume")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EngineResume(nint engine);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_stop")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EngineStop(nint engine, MpFadeMode fade);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_seek")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EngineSeek(nint engine, long positionMs);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_set_volume")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EngineSetVolume(nint engine, float linear);

    [LibraryImport(LibraryName, EntryPoint = "mp_track_set_replaygain")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult TrackSetReplayGain(nint track, float gainDb, float peak);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_set_crossfade")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EngineSetCrossfade(nint engine, uint ms);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_get_clock")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EngineGetClock(nint engine, MpClock* clock);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_get_stats")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EngineGetStats(nint engine, MpEngineStats* stats);

    [LibraryImport(LibraryName, EntryPoint = "mp_engine_render")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult EngineRender(nint engine, float* outInterleaved, uint frames);

    [LibraryImport(LibraryName, EntryPoint = "mp_preview_start")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult PreviewStart(nint engine, nint track, float gainDb);

    [LibraryImport(LibraryName, EntryPoint = "mp_preview_stop")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult PreviewStop(nint engine);

    // ---- analysis ----

    [LibraryImport(LibraryName, EntryPoint = "mp_analysis_try_get_latest")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult AnalysisTryGetLatest(nint engine, MpAnalysisFrame* frame);

    // ---- renderer ----

    [LibraryImport(LibraryName, EntryPoint = "mp_renderer_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult RendererCreate(nint engine, void* swapChainPanelNative, MpRendererConfig* config, nint* outRenderer);

    [LibraryImport(LibraryName, EntryPoint = "mp_renderer_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult RendererDestroy(nint renderer);

    [LibraryImport(LibraryName, EntryPoint = "mp_renderer_resize")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult RendererResize(nint renderer, uint width, uint height, float scaleX, float scaleY);

    [LibraryImport(LibraryName, EntryPoint = "mp_renderer_set_visible")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult RendererSetVisible(nint renderer, byte visible);

    [LibraryImport(LibraryName, EntryPoint = "mp_renderer_get_stats")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult RendererGetStats(nint renderer, MpRenderStats* stats);

    [LibraryImport(LibraryName, EntryPoint = "mp_renderer_enum_presets")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult RendererEnumPresets(nint renderer, MpPresetInfo* presets, uint* count);

    [LibraryImport(LibraryName, EntryPoint = "mp_renderer_set_preset")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult RendererSetPreset(nint renderer, byte* utf8Id);

    [LibraryImport(LibraryName, EntryPoint = "mp_renderer_set_param")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult RendererSetParam(nint renderer, byte* utf8Name, float value);

    [LibraryImport(LibraryName, EntryPoint = "mp_renderer_set_theme")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult RendererSetTheme(nint renderer, MpThemeColors* colors);

    [LibraryImport(LibraryName, EntryPoint = "mp_renderer_set_quality")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult RendererSetQuality(nint renderer, MpQualityPolicy policy);
}
