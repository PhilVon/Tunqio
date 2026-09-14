using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>
/// The container's <see cref="ITrackRater"/> as the views see it: the same rater, with its events re-raised on the
/// XAML thread (E6-S7). The library's rater raises them where its write finished, which is a pool thread, and
/// what the views do with them — replace an item in a bound collection, patch the Now Playing track — has to
/// happen on the thread that owns the tree. One adapter here rather than a <c>Post</c> in each view model, so
/// a view model that forgets is still safe.
/// </summary>
public sealed class UiThreadRater : ITrackRater, IDisposable
{
    private readonly ITrackRater _inner;
    private readonly SynchronizationContext? _ui;

    /// <param name="ui">The XAML thread's context. Null re-raises inline, which is what the tests want.</param>
    public UiThreadRater(ITrackRater inner, SynchronizationContext? ui)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _ui = ui;
        _inner.Changed += OnChanged;
        _inner.FileWriteCompleted += OnFileWriteCompleted;
    }

    public event EventHandler<RatingChange>? Changed;

    public event EventHandler<RatingChange>? FileWriteCompleted;

    public Task<RatingChange> RateAsync(long trackId, int stars, CancellationToken ct = default) => _inner.RateAsync(trackId, stars, ct);

    public Task<int> FlushDeferredAsync(CancellationToken ct = default) => _inner.FlushDeferredAsync(ct);

    private void OnChanged(object? sender, RatingChange change) => Post(() => Changed?.Invoke(this, change));

    private void OnFileWriteCompleted(object? sender, RatingChange change) => Post(() => FileWriteCompleted?.Invoke(this, change));

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
            return;
        }

        // No JoinableTaskFactory in this app; the context is the XAML thread's DispatcherQueue one and Post never blocks the caller.
#pragma warning disable VSTHRD001
        _ui.Post(_ => action(), null);
#pragma warning restore VSTHRD001
    }

    public void Dispose()
    {
        _inner.Changed -= OnChanged;
        _inner.FileWriteCompleted -= OnFileWriteCompleted;
    }
}
