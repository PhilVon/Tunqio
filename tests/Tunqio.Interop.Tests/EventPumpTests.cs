using System.Reactive.Linq;
using Tunqio.Core.Audio;

namespace Tunqio.Interop.Tests;

/// <summary>
/// AC-29: an event raised on a foreign thread reaches the observable on the pump thread and nothing else runs on
/// the raising thread. The trampoline is invoked directly through its unmanaged function pointer from a worker
/// thread, which is exactly what the native core does; a second test uses the real core when a device exists.
/// </summary>
[Collection("native engine")]
public unsafe class EventPumpTests
{
    [Fact]
    public void Event_from_a_foreign_thread_arrives_on_the_pump_thread()
    {
        using NativeEngine engine = NativeEngine.Create();
        var received = new List<(EngineEvent Event, int ThreadId)>();
        using var gate = new ManualResetEventSlim();
        using IDisposable subscription = engine.Events.Subscribe(e =>
        {
            lock (received)
            {
                received.Add((e, Environment.CurrentManagedThreadId));
            }

            gate.Set();
        });

        delegate* unmanaged[Cdecl]<MpEvent*, void*, void> trampoline = &NativeEngine.EventTrampoline;
        System.Runtime.InteropServices.GCHandle self = SelfHandle(engine);
        void* user = (void*)System.Runtime.InteropServices.GCHandle.ToIntPtr(self);
        int raisingThread = 0;
        var raiser = new Thread(() =>
        {
            raisingThread = Environment.CurrentManagedThreadId;
            byte* message = stackalloc byte[] { (byte)'h', (byte)'i', 0 };
            var ev = new MpEvent { StructSize = (uint)sizeof(MpEvent), Type = MpEventType.Error, A = 7, B = 9, Message = message };
            trampoline(&ev, user);
        });
        raiser.Start();
        raiser.Join();
        self.Free();

        gate.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the pump must deliver the event");
        received.Should().ContainSingle();
        received[0].Event.Should().Be(new EngineEvent(EngineEventType.Error, 7, 9, "hi"));
        received[0].ThreadId.Should().Be(engine.PumpThreadId, "subscribers run on the pump thread");
        received[0].ThreadId.Should().NotBe(raisingThread, "no managed subscriber code runs on the native thread");
        engine.LastCallbackThreadId.Should().Be(raisingThread, "the trampoline itself ran on the raising thread");
    }

    [Fact]
    public void Real_core_events_arrive_on_the_pump_thread_when_a_device_exists()
    {
        using NativeEngine engine = NativeEngine.Create();
        try
        {
            engine.SetOutput(new OutputConfig(BufferMs: 20));
        }
        catch (NativeException ex) when (ex.Result is MpResult.Device or MpResult.Bass)
        {
            return; // no output device
        }

        var received = new List<(EngineEvent Event, int ThreadId)>();
        using var ended = new ManualResetEventSlim();
        using IDisposable subscription = engine.Events.Subscribe(e =>
        {
            lock (received)
            {
                received.Add((e, Environment.CurrentManagedThreadId));
            }

            if (e.Type == EngineEventType.TrackEnded)
            {
                ended.Set();
            }
        });

        using NativeTrack track = engine.OpenTrack(WavFixture.WriteSine("events", seconds: 0.3));
        engine.SetVolume(0f);
        engine.Play(track);

        ended.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("the mix-time end sync fires from the WASAPI thread");
        lock (received)
        {
            received.Select(r => r.Event.Type).Should().ContainInOrder(EngineEventType.TrackStarted, EngineEventType.TrackEnded);
            received.Should().OnlyContain(r => r.ThreadId == engine.PumpThreadId);
            received.Should().Contain(r => r.Event.Type == EngineEventType.TrackStarted && r.Event.A == track.Handle);
        }

        engine.LastCallbackThreadId.Should().NotBe(engine.PumpThreadId);
    }

    private static System.Runtime.InteropServices.GCHandle SelfHandle(NativeEngine engine)
    {
        // The engine registers a GCHandle to itself as the callback's user pointer; recreate an equivalent one.
        return System.Runtime.InteropServices.GCHandle.Alloc(engine, System.Runtime.InteropServices.GCHandleType.Normal);
    }
}
