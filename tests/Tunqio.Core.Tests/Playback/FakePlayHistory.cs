using Tunqio.Core.Library;

namespace Tunqio.Core.Tests.Playback;

/// <summary>Collects the play events the session emits, and can refuse one to prove a failed write is survivable.</summary>
internal sealed class FakePlayHistory : IPlayHistoryRepository
{
    public List<PlayEvent> Events { get; } = [];

    /// <summary>When set, every write throws it.</summary>
    public Exception? Refuse { get; set; }

    public Task<bool> RecordAsync(PlayEvent playEvent, CancellationToken ct = default)
    {
        if (Refuse is not null)
        {
            throw Refuse;
        }

        Events.Add(playEvent);
        return Task.FromResult(true);
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> a test drives by hand: <see cref="Advance"/> moves both the wall clock the heard
/// time is capped against and the timestamps taken from it.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => _now += by;
}
