namespace Tunqio.Core.Audio;

/// <summary>Output device mode (<c>mp_output_mode</c>).</summary>
public enum OutputMode
{
    Shared = 0,
    Exclusive = 1,
}

/// <summary>How a stop should end the audio (<c>mp_fade_mode</c>).</summary>
public enum FadeMode
{
    None = 0,
    Guard = 1,
}

/// <summary>Output configuration handed to the engine (<c>mp_output_config</c>).</summary>
/// <param name="DeviceIndex">Index from <see cref="OutputDevice.Index"/>, or -1 for the default device.</param>
/// <param name="BufferMs">Requested buffer in milliseconds; 0 for the device default.</param>
/// <param name="EventDriven">WASAPI event-driven buffering (lower latency, one-period buffer).</param>
public sealed record OutputConfig(int DeviceIndex = -1, OutputMode Mode = OutputMode.Shared, int BufferMs = 0, bool EventDriven = false);

/// <summary>An output device as enumerated by the engine (<c>mp_device_info</c>).</summary>
public sealed record OutputDevice(
    int Index,
    string Name,
    string Id,
    int MixSampleRate,
    int MixChannels,
    TimeSpan MinPeriod,
    TimeSpan DefaultPeriod,
    bool IsDefault);

/// <summary>Decoded stream facts for an opened track (<c>mp_track_info</c>).</summary>
public sealed record TrackInfo(TimeSpan Duration, int SampleRate, int Channels, int BitsPerSample, string Codec, long TotalFrames);

/// <summary>A latency-compensated position reading (<c>mp_clock</c>).</summary>
public readonly record struct PlaybackClock(TimeSpan Position, long MixerBytePosition, TimeSpan OutputLatency, long QpcTicks, long OutputBufferedBytes);

/// <summary>Output thread statistics (<c>mp_engine_stats</c>).</summary>
public sealed record EngineStats(
    long Callbacks,
    long Underruns,
    TimeSpan CallbackMax,
    int OutputSampleRate,
    int OutputChannels,
    TimeSpan OutputBuffer,
    bool Exclusive,
    bool OutputStarted,
    string OutputFormat);
