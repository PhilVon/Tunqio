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
/// event-specific payload (for <see cref="EngineEventType.TrackStarted"/>: the track handle and start position).
/// </summary>
public sealed record EngineEvent(EngineEventType Type, long A, long B, string? Message);
