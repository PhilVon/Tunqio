using System.Runtime.InteropServices;

namespace Tunqio.Interop;

/// <summary>Result codes of the mpcore C ABI (<c>mp_result</c> in <c>mpcore.h</c>).</summary>
public enum MpResult
{
    Ok = 0,
    InvalidArgument = 1,
    Bass = 2,
    Device = 3,
    D3D = 4,
    State = 5,
    Internal = 6,
}

internal enum MpOutputMode
{
    Shared = 0,
    Exclusive = 1,
}

internal enum MpFadeMode
{
    None = 0,
    Guard = 1,
}

internal enum MpJoinMode
{
    Gapless = 0,
    Crossfade = 1,
}

/// <summary>Native log levels (<c>mp_log_level</c>).</summary>
public enum MpLogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
}

internal enum MpEventType
{
    TrackStarted = 1,
    TrackEnded = 2,
    DeviceLost = 3,
    DeviceChanged = 4,
    Underrun = 5,
    Error = 6,
}

/*
 * Blittable mirrors of the mpcore.h structs. LayoutKind.Sequential with default packing follows the MSVC
 * natural-alignment rules, so field order and types must match the header exactly. Every struct starts with
 * StructSize, which the callee validates: a layout drift shows up as MP_E_INVALID_ARG in the round-trip tests.
 */

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpEngineConfig
{
    public uint StructSize;
    public uint SampleRate;
    public uint Channels;
    public byte* PluginDir;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpOutputConfig
{
    public uint StructSize;
    public int DeviceIndex;
    public MpOutputMode Mode;
    public uint BufferMs;
    public byte EventDriven;
    public byte Reserved0;
    public byte Reserved1;
    public byte Reserved2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpDeviceInfo
{
    public uint StructSize;
    public int Index;
    public fixed byte Name[256];
    public fixed byte Id[256];
    public uint MixSampleRate;
    public uint MixChannels;
    public uint MinPeriodUs;
    public uint DefaultPeriodUs;
    public byte IsDefault;
    public byte IsEnabled;
    public byte Reserved0;
    public byte Reserved1;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpTrackInfo
{
    public uint StructSize;
    public long DurationMs;
    public uint SampleRate;
    public uint Channels;
    public uint BitsPerSample;
    public fixed byte Codec[32];
    public long TotalFrames;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpClock
{
    public uint StructSize;
    public long PositionMs;
    public long MixerBytePos;
    public uint OutputLatencyMs;
    public long QpcTicks;
    public long OutputBufferedBytes;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpEngineStats
{
    public uint StructSize;
    public ulong Callbacks;
    public ulong Underruns;
    public uint CallbackMaxUs;
    public uint OutputSampleRate;
    public uint OutputChannels;
    public uint OutputBufferMs;
    public byte Exclusive;
    public byte OutputStarted;
    public byte Reserved0;
    public byte Reserved1;
    public fixed byte OutputFormat[16];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpEvent
{
    public uint StructSize;
    public MpEventType Type;
    public long A;
    public long B;
    public byte* Message;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpRendererConfig
{
    public uint StructSize;
    public uint Width;
    public uint Height;
    public float ScaleX;
    public float ScaleY;
    public byte ForceWarp;
    public byte VSync;
    public byte Headless;
    public byte Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpRenderStats
{
    public const int HistogramBuckets = 6;

    public uint StructSize;
    public ulong Frames;
    public ulong Resizes;
    public double Fps;
    public float FrameMsLast;
    public float FrameMsMax;
    public float FrameMsAvg;
    public fixed uint FrameMsHistogram[HistogramBuckets];
    public ulong DxgiPresentCount;
    public ulong DxgiMissedRefreshes;
    public uint Width;
    public uint Height;
    public byte Warp;
    public byte Headless;
    public byte DeviceLost;
    public byte Visible;
    public fixed byte Adapter[128];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpPresetInfo
{
    public uint StructSize;
    public fixed byte Id[64];
    public fixed byte Name[128];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpPresetParamInfo
{
    public uint StructSize;
    public fixed byte Name[64];
    public fixed byte Label[64];
    public fixed byte Unit[16];
    public fixed byte Choices[256];
    public float MinValue;
    public float MaxValue;
    public float DefaultValue;
    public float Step;
    public uint Flags;
}

/// <summary><c>mp_preset_param_flags</c>.</summary>
[Flags]
internal enum MpPresetParamFlags : uint
{
    None = 0,
    Hidden = 1 << 0,
    Choice = 1 << 1,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpThemeColors
{
    public uint StructSize;
    public fixed float Primary[4];
    public fixed float Secondary[4];
    public fixed float Accent[4];
    public fixed float Background[4];
}

internal enum MpQualityPolicy
{
    Auto = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct MpAnalysisFrame
{
    public const int SpectrumBins = 1024;
    public const int WaveformSamples = 512;
    public const int OctaveBands = 10;

    public uint StructSize;
    public uint Sequence;
    public long MixerBytePos;
    public long QpcTicks;
    public fixed float Spectrum[SpectrumBins];
    public fixed float Waveform[WaveformSamples];
    public fixed float Bands[OctaveBands];
    public float Rms;
    public float Peak;
    public float SpectralCentroidHz;
    public float HarmonicRatio;
    public byte Onset;
    public byte Discontinuities;
    public byte Reserved0;
    public byte Reserved1;
}
