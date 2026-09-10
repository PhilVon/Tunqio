namespace Tunqio.Core.Audio;

/// <summary>Kinds of event the native engine raises (<c>mp_event_type</c> in <c>mpcore.h</c>).</summary>
public enum EngineEventType
{
    TrackStarted = 1,
    TrackEnded = 2,
    DeviceLost = 3,
    DeviceChanged = 4,
    Underrun = 5,
    Error = 6,
}

/// <summary>
/// An engine event as delivered on the interop pump thread. <paramref name="A"/> and <paramref name="B"/> carry
/// event-specific payload. <see cref="EngineEventType.TrackStarted"/> and <see cref="EngineEventType.TrackEnded"/>
/// carry the track's <see cref="TrackHandle.Id"/> in <paramref name="A"/>; at a gapless join both carry the mixer byte
/// position of the join in <paramref name="B"/> (compare with <see cref="PlaybackClock.HasPlayed"/>), a plain play
/// carries its start in milliseconds and a natural end 0.
/// <para>
/// The device events (E1-S7) both carry a device index in <paramref name="A"/> and that device's WASAPI endpoint id in
/// <paramref name="Message"/>, which is what <see cref="OutputDevice.Id"/> matches and what the settings remember.
/// <see cref="EngineEventType.DeviceLost"/> means the open device went and playback is parked where it stood: the
/// output is closed, nothing is unloaded, and reopening with <see cref="IAudioEngine.InitializeAsync"/> then resuming
/// carries on from the same position. <see cref="EngineEventType.DeviceChanged"/> carries 1 in <paramref name="B"/>
/// when playback has already moved to that device (only when the caller asked for
/// <see cref="OutputConfig.DefaultDevice"/> and Windows moved the default) and 0 when the device is merely being
/// offered — the lost device has come back, and whether to switch to it is the user's call, not the engine's.
/// </para>
/// </summary>
public sealed record EngineEvent(EngineEventType Type, long A, long B, string? Message);
