namespace Tunqio.SoakRunner;

/// <summary>
/// Holds the machine-wide WASAPI output-device lock for the life of the object, on a thread of its own.
/// </summary>
/// <remarks>
/// THE NAME IS THE POINT. It is the same named mutex <c>mpcore.tests</c> takes around its own device cases
/// (native/mpcore.tests/src/machine_lock.h), and a copy of tools/LatencyRunner/DeviceLease.cs (T-173). The
/// output device is exclusive machine-wide, and a harness that
/// ignored the lock would either lose the device to a concurrent native suite or take it from one - and the
/// error either produced would describe the audio engine rather than two processes wanting one device. That is
/// exactly the family of failure T-143, T-164 and T-167 spent a day tracing.
///
/// THE THREAD IS ALSO THE POINT, and cost this harness a run before it was here. A Win32 mutex has thread
/// affinity: whoever waits on it must be the one to release it. The harness is async, so its continuations run
/// wherever the thread pool puts them, and Dispose ran on a thread that had never taken the mutex -
/// ReleaseMutex threw ApplicationException and took the process down after the measurement had finished. So
/// one thread does nothing but hold this and wait to be told to let go.
/// </remarks>
internal sealed class DeviceLease : IDisposable
{
    private const string MutexName = "Tunqio.mpcore.tests.the-WASAPI-output-device";

    private readonly Thread _holder;
    private readonly ManualResetEventSlim _acquired = new(false);
    private readonly ManualResetEventSlim _release = new(false);

    private DeviceLease(TimeSpan timeout)
    {
        _holder = new Thread(() => Hold(timeout)) { IsBackground = true, Name = "Tunqio device lease" };
        _holder.Start();
        _acquired.Wait();
    }

    /// <summary>True when the lock was actually taken. False is not fatal - the run goes ahead and says so.</summary>
    public bool Held { get; private set; }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the device. Long by default, because the point is to take
    /// turns rather than to give up: a native suite's turn at the device legitimately runs to a minute or more.
    /// </summary>
    public static DeviceLease Take(TimeSpan timeout) => new(timeout);

    private void Hold(TimeSpan timeout)
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, MutexName);
            try
            {
                Held = mutex.WaitOne(timeout);
            }
            catch (AbandonedMutexException)
            {
                // A previous holder died with it taken. Ownership is ours and the device is free; what the
                // mutex protects is a sound card, which the dead process did not leave half-written.
                Held = true;
            }
        }
        catch (Exception)
        {
            Held = false; // nothing to serialise on is a reason to say so, not a reason to fail
        }
        finally
        {
            _acquired.Set();
        }

        _release.Wait();
        if (mutex is not null)
        {
            if (Held)
            {
                mutex.ReleaseMutex();
            }

            mutex.Dispose();
        }
    }

    public void Dispose()
    {
        _release.Set();
        _holder.Join(TimeSpan.FromSeconds(5));
        _acquired.Dispose();
        _release.Dispose();
    }
}
