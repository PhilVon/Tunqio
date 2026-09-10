using Tunqio.Core.Playback;

namespace Tunqio.Core.Tests.Playback;

/// <summary>The saved queue, in memory. Can refuse a write, and can drop items to stand in for a purge.</summary>
internal sealed class FakeQueueStore : IQueueStateRepository
{
    public QueueState? Saved { get; set; }

    public int Writes { get; private set; }

    /// <summary>When set, every read and write throws it.</summary>
    public Exception? Refuse { get; set; }

    public Task<QueueState?> LoadAsync(CancellationToken ct = default) =>
        Refuse is not null ? throw Refuse : Task.FromResult(Saved);

    public Task SaveAsync(QueueState state, CancellationToken ct = default)
    {
        if (Refuse is not null)
        {
            throw Refuse;
        }

        Saved = state;
        Writes++;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        Saved = null;
        return Task.CompletedTask;
    }
}
