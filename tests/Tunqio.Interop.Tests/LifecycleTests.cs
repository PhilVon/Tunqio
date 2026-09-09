using System.Diagnostics;
using Tunqio.Core.Audio;

namespace Tunqio.Interop.Tests;

/// <summary>AC-30: 1000 create/destroy cycles leave handle counts stable and no event arrives after dispose.</summary>
[Collection("native engine")]
public class LifecycleTests
{
    [Fact]
    public void A_thousand_create_destroy_cycles_leave_handle_counts_stable()
    {
        // Warm up: first-time DLL loads, thread pool, JIT.
        for (int i = 0; i < 20; i++)
        {
            using NativeEngine warm = NativeEngine.Create();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        Process process = Process.GetCurrentProcess();
        process.Refresh();
        int handlesBefore = process.HandleCount;
        long privateBefore = process.PrivateMemorySize64;
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < 1000; i++)
        {
            using NativeEngine engine = NativeEngine.Create();
            using NativeTrack track = engine.OpenTrack(WavFixture.WriteSine("cycle", seconds: 0.05));
            _ = engine.GetClock();
        }

        sw.Stop();
        // Managed Thread objects (one pump per engine) hold their OS thread handle until finalised.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        int handlesAfter = process.HandleCount;
        long privateAfter = process.PrivateMemorySize64;

        (handlesAfter - handlesBefore).Should().BeLessThan(100, $"handles before {handlesBefore}, after {handlesAfter}; {sw.ElapsedMilliseconds} ms for 1000 cycles");
        (privateAfter - privateBefore).Should().BeLessThan(64L * 1024 * 1024, "private bytes must not grow by tens of MB over 1000 cycles");
    }

    [Fact]
    public void No_event_arrives_after_dispose_returns()
    {
        // Events queued before Dispose are delivered while Dispose joins the pump; nothing may arrive afterwards.
        int delivered = 0;
        NativeEngine engine = NativeEngine.Create();
        using IDisposable subscription = engine.Events.Subscribe(_ => Interlocked.Increment(ref delivered));

        bool hasOutput;
        try
        {
            engine.SetOutput(new OutputConfig(BufferMs: 20));
            hasOutput = true;
        }
        catch (NativeException ex) when (ex.Result is MpResult.Device or MpResult.Bass)
        {
            hasOutput = false;
        }

        if (hasOutput)
        {
            NativeTrack track = engine.OpenTrack(WavFixture.WriteSine("after-dispose", seconds: 0.2));
            engine.SetVolume(0f);
            engine.Play(track); // the end sync would fire in ~200 ms; dispose first
        }

        bool completed = false;
        using IDisposable completion = engine.Events.Subscribe(_ => { }, () => completed = true);
        engine.Dispose();
        int atReturn = Volatile.Read(ref delivered);
        Thread.Sleep(400);

        Volatile.Read(ref delivered).Should().Be(atReturn, "mp_engine_destroy drains callbacks before returning and Dispose joins the pump");
        completed.Should().BeTrue("the event stream completes on dispose");
    }
}
