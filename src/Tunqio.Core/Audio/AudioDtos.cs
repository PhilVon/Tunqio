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
public sealed record OutputConfig(int DeviceIndex = OutputConfig.DefaultDevice, OutputMode Mode = OutputMode.Shared, int BufferMs = 0, bool EventDriven = false)
{
    /// <summary>The system default output device (<c>MP_DEVICE_DEFAULT</c>).</summary>
    public const int DefaultDevice = -1;

    /// <summary>No device (<c>MP_DEVICE_NONE</c>): audio exists only when the caller pulls it (tests, offline rendering).</summary>
    public const int NoDevice = -2;
}

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

/// <summary>
/// A latency-compensated position reading (<c>mp_clock</c>). <paramref name="MixerBytePosition"/> counts the bytes the
/// mixer has produced; <paramref name="OutputBufferedBytes"/> of them are still in the output buffer, so what is being
/// heard is <see cref="AudibleMixerBytePosition"/>. A gapless join is reported by <see cref="EngineEventType.TrackStarted"/>
/// as soon as it is mixed, up to one output buffer before it is heard: the session changes the now-playing item when
/// <see cref="HasPlayed"/> says the join position has left the buffer (E1-S3).
/// </summary>
public readonly record struct PlaybackClock(TimeSpan Position, long MixerBytePosition, TimeSpan OutputLatency, long QpcTicks, long OutputBufferedBytes)
{
    /// <summary>The mixer byte position the listener is hearing now: produced bytes less those still buffered.</summary>
    public long AudibleMixerBytePosition => MixerBytePosition - OutputBufferedBytes;

    /// <summary>True once the audio mixed at <paramref name="mixerBytePosition"/> (an event's <see cref="EngineEvent.B"/>) has been heard.</summary>
    public bool HasPlayed(long mixerBytePosition) => AudibleMixerBytePosition >= mixerBytePosition;
}

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
