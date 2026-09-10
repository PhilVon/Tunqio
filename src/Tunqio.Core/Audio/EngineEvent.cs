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
/// </summary>
public sealed record EngineEvent(EngineEventType Type, long A, long B, string? Message);
