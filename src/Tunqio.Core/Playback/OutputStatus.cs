namespace Tunqio.Core.Playback;

/// <summary>What the output is doing, as far as the user needs to be told (E2-S7, flow 4).</summary>
public enum OutputHealth
{
    /// <summary>Sound is going somewhere, and nothing needs saying.</summary>
    Ok,

    /// <summary>
    /// The open device went and playback is parked where it stood (E1-S7). Sticky: it stays until the user picks
    /// another output or the device comes back, because there is nothing the app can do about it on its own and
    /// silence with no explanation is the failure this exists to prevent.
    /// </summary>
    Lost,

    /// <summary>
    /// The device that went is back and is being offered. The engine never switches on its own — "no silent
    /// continuation on the wrong device without notice" cuts both ways — so this is a question, not a report.
    /// </summary>
    Returned,
}

/// <summary>
/// The output's health and, when there is one to name, the device it is about. A value on
/// <see cref="PlaybackSnapshot"/> rather than an event, because a disconnected device is a state the user is still
/// in a minute later and a sticky bar is how that state is drawn; the transient failures are
/// <see cref="PlaybackSession.Errors"/>.
/// </summary>
/// <param name="Health">What the output is doing.</param>
/// <param name="DeviceId">The WASAPI endpoint id the event carried, or null when the engine did not name one.</param>
/// <param name="DeviceName">The device's name where it could still be enumerated; a device that has gone has none.</param>
public readonly record struct OutputStatus(OutputHealth Health, string? DeviceId, string? DeviceName)
{
    /// <summary>Nothing to say about the output.</summary>
    public static OutputStatus Ok { get; } = new(OutputHealth.Ok, null, null);
}
