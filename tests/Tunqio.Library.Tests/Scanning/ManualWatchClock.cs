namespace Tunqio.Library.Tests.Scanning;

/// <summary>
/// A clock for <see cref="LibraryWatcher"/> that stands still until the test moves it, so an interleaving that
/// real time only produces on a busy machine can be produced on purpose (T-187). Its timers fire only when
/// <see cref="AdvanceTo"/> moves the clock past them, and <see cref="StepEachReadOnThisThread"/> makes time pass
/// between one clock read and the next on the calling thread, which is what a thread descheduled between two
/// reads looks like to the code reading it.
/// </summary>
internal sealed class ManualWatchClock : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;
    private int _stepThread = -1;
    private long _stepTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <summary>Timers the watcher has armed that have not fired or been disposed.</summary>
    public int ArmedTimers
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count(t => t.DueTicks is not null);
            }
        }
    }

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            long now = _ticks;
            if (_stepThread == Environment.CurrentManagedThreadId)
            {
                _ticks += _stepTicks;
            }

            return now;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        }
    }

    /// <summary>Until disposed, every clock read on this thread moves the clock on by <paramref name="step"/> after it is read. Reads on other threads do not.</summary>
    public IDisposable StepEachReadOnThisThread(TimeSpan step)
    {
        lock (_lock)
        {
            _stepThread = Environment.CurrentManagedThreadId;
            _stepTicks = step.Ticks;
        }

        return new StopStepping(this);
    }

    /// <summary>Sets the clock to <paramref name="sinceStart"/> after its start and fires every timer due by then, outside the lock.</summary>
    public void AdvanceTo(TimeSpan sinceStart)
    {
        List<ManualTimer> due;
        lock (_lock)
        {
            _ticks = Math.Max(_ticks, sinceStart.Ticks);
            due = [.. _timers.Where(t => t.DueTicks is { } at && at <= _ticks)];
            foreach (ManualTimer timer in due)
            {
                timer.DueTicks = null;
                _timers.Remove(timer);
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock)
        {
            timer.DueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : _ticks + dueTime.Ticks;
            _timers.Add(timer);
        }

        return timer;
    }

    private sealed class StopStepping(ManualWatchClock clock) : IDisposable
    {
        public void Dispose()
        {
            lock (clock._lock)
            {
                clock._stepThread = -1;
                clock._stepTicks = 0;
            }
        }
    }

    /// <summary>One-shot: the watcher only ever uses the clock's timers through <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>.</summary>
    private sealed class ManualTimer(ManualWatchClock clock, TimerCallback callback, object? state) : ITimer
    {
        /// <summary>Guarded by the clock's lock.</summary>
        public long? DueTicks { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._lock)
            {
                DueTicks = dueTime == Timeout.InfiniteTimeSpan ? null : clock._ticks + dueTime.Ticks;
                if (DueTicks is not null && !clock._timers.Contains(this))
                {
                    clock._timers.Add(this);
                }
            }

            return true;
        }

        public void Dispose()
        {
            lock (clock._lock)
            {
                DueTicks = null;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
