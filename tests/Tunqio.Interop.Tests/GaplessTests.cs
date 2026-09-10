using System.Diagnostics;
using Tunqio.Core.Audio;

namespace Tunqio.Interop.Tests;

/// <summary>
/// E1-S3: gapless and preload through <see cref="IAudioEngine"/>. The native <c>[gapless]</c> suite measures the join per
/// format; these tests prove the managed contract over it: the queue call, the events naming the tracks and the join
/// position, the clock saying when that position has been heard, and the queue surviving a thousand changes of mind.
/// The fixture pair is one chirp (200 Hz to 2000 Hz over 4 s, 0.25 full scale) cut at 2.0 s, so a continuous join
/// reproduces the chirp regenerated here.
/// </summary>
[Collection("native engine")]
public class GaplessTests
{
    private const int Rate = 48_000;
    private const int Channels = 2;
    private const int BytesPerFrame = Channels * sizeof(float);
    private const long JoinFrame = 2 * Rate; // a is exactly 2.0 s
    private const double Amplitude = 0.25;

    private static string Fixture(string pair, string stem)
    {
        string dir = RepoPaths.File("tests", "fixtures", "gapless", pair);
        return Directory.EnumerateFiles(dir).Single(f => Path.GetFileNameWithoutExtension(f) == stem);
    }

    private static async Task<NativeAudioEngine> CreateHeadlessAsync()
    {
        NativeAudioEngine engine = NativeAudioEngine.Create(Rate, Channels);
        await engine.InitializeAsync(new OutputConfig(DeviceIndex: OutputConfig.NoDevice));
        return engine;
    }

    /// <summary>The chirp at output frame <paramref name="frame"/> (GaplessFixtureBuilder in FixtureGen).</summary>
    private static double Reference(long frame)
    {
        double t = (double)frame / Rate;
        double phase = 2 * Math.PI * (200.0 * t + (2000.0 - 200.0) * t * t / (2.0 * 4.0));
        return Amplitude * Math.Sin(phase);
    }

    private static double Left(float[] audio, long frame) => frame < 0 || frame * Channels >= audio.Length ? 0 : audio[frame * Channels];

    /// <summary>RMS error against the chirp over [from, to) relative to the chirp's RMS there.</summary>
    private static double Residual(float[] audio, long from, long to)
    {
        double err = 0;
        double sig = 0;
        for (long k = from; k < to; k++)
        {
            double r = Reference(k);
            double d = Left(audio, k) - r;
            err += d * d;
            sig += r * r;
        }

        return Math.Sqrt(err / sig);
    }

    private static double MaxStep(Func<long, double> sample, long from, long to)
    {
        double worst = 0;
        for (long k = from + 1; k < to; k++)
        {
            worst = Math.Max(worst, Math.Abs(sample(k) - sample(k - 1)));
        }

        return worst;
    }

    private sealed class EventLog : IDisposable
    {
        private readonly List<(EngineEvent Event, long ElapsedTicks)> _events = [];
        private readonly IDisposable _subscription;
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public EventLog(IAudioEngine engine)
        {
            _subscription = engine.Events.Subscribe(e =>
            {
                lock (_events)
                {
                    _events.Add((e, _clock.ElapsedTicks));
                }
            });
        }

        public Stopwatch Clock => _clock;

        public IReadOnlyList<(EngineEvent Event, long ElapsedTicks)> Snapshot()
        {
            lock (_events)
            {
                return [.. _events];
            }
        }

        public bool WaitFor(Func<IReadOnlyList<EngineEvent>, bool> condition, TimeSpan timeout) =>
            SpinWait.SpinUntil(() => condition(Snapshot().Select(e => e.Event).ToList()), timeout);

        public void Dispose() => _subscription.Dispose();
    }

    [Fact]
    public async Task Two_fixture_tracks_join_into_one_continuous_chirp_with_events_naming_the_tracks_and_the_join()
    {
        await using NativeAudioEngine engine = await CreateHeadlessAsync();
        using var log = new EventLog(engine);
        TrackHandle a = await engine.OpenAsync(Fixture("wav", "a"));
        TrackHandle b = await engine.OpenAsync(Fixture("wav", "b"));
        a.Info.TotalFrames.Should().Be(JoinFrame);

        await engine.PlayAsync(a);
        await engine.PreloadNextAsync(b);

        // 479-frame pulls: 96 000 is not a multiple, so the seam falls inside a buffer (as the native measurement does).
        const int Pull = 479;
        long total = (long)(4.5 * Rate);
        float[] audio = new float[total * Channels];
        long rendered = 0;
        while (rendered < total)
        {
            int n = (int)Math.Min(Pull, total - rendered);
            engine.Native.Render(audio.AsSpan((int)(rendered * Channels), n * Channels), n);
            rendered += n;
        }

        log.WaitFor(events => events.Count(e => e.Type == EngineEventType.TrackEnded) >= 2, TimeSpan.FromSeconds(5)).Should().BeTrue();
        List<EngineEvent> events = log.Snapshot().Select(e => e.Event).Where(e => e.Type is EngineEventType.TrackStarted or EngineEventType.TrackEnded).ToList();
        long joinBytes = JoinFrame * BytesPerFrame;
        events.Should().Equal(
            new EngineEvent(EngineEventType.TrackStarted, a.Id, 0, null),
            new EngineEvent(EngineEventType.TrackEnded, a.Id, joinBytes, null),
            new EngineEvent(EngineEventType.TrackStarted, b.Id, joinBytes, null),
            new EngineEvent(EngineEventType.TrackEnded, b.Id, 0, null));

        // AC-42: one continuous tone. Across the seam the output is the chirp itself (no gap, no overlap) and the
        // largest step is the chirp's own (no click); the fade-in of a is long over by then.
        long from = (long)(1.98 * Rate);
        long to = (long)(2.02 * Rate);
        Residual(audio, from, to).Should().BeLessThan(0.001, "the seam reproduces the chirp sample for sample");
        Residual(audio, (long)(2.1 * Rate), (long)(3.0 * Rate)).Should().BeLessThan(0.001, "b continues at lag 0");
        double stepOut = MaxStep(k => Left(audio, k), (long)(1.95 * Rate), (long)(2.05 * Rate));
        double stepRef = MaxStep(Reference, (long)(1.95 * Rate), (long)(2.05 * Rate));
        (stepOut / stepRef).Should().BeLessThan(1.6, "no click at the join");

        // The clock crossed over with the join: b's position from its origin, and the join position has been heard
        // (headless, nothing is buffered).
        PlaybackClock clock = engine.Clock;
        clock.HasPlayed(joinBytes).Should().BeTrue();
        clock.MixerBytePosition.Should().Be(2 * JoinFrame * BytesPerFrame, "the mixer position counts the audio produced (a then b), not the silence pulled after b ended");
        engine.Stats.Underruns.Should().Be(0, "the short read at the seam is the join, not an underrun");
    }

    [Fact]
    public async Task The_join_event_arrives_within_100_ms_of_the_boundary_when_rendering_at_real_time()
    {
        await using NativeAudioEngine engine = await CreateHeadlessAsync();
        using var log = new EventLog(engine);
        TrackHandle a = await engine.OpenAsync(Fixture("wav", "a"));
        TrackHandle b = await engine.OpenAsync(Fixture("wav", "b"));

        // 0.5 s of a, then b: 480-frame pulls paced at 10 ms, as a shared-mode device would take them.
        await engine.PlayAsync(a, TimeSpan.FromSeconds(1.5));
        await engine.PreloadNextAsync(b);
        const int Pull = Rate / 100;
        float[] buffer = new float[Pull * Channels];
        var pulls = new List<(long ElapsedTicks, long MixerBytesAfter)>();
        Stopwatch clock = log.Clock;
        long start = clock.ElapsedTicks;
        for (int i = 0; i < 100; i++)
        {
            long due = start + i * Stopwatch.Frequency / 100;
            while (clock.ElapsedTicks < due)
            {
                Thread.SpinWait(200);
            }

            engine.Native.Render(buffer, Pull);
            pulls.Add((clock.ElapsedTicks, engine.Clock.MixerBytePosition));
        }

        log.WaitFor(events => events.Any(e => e.Type == EngineEventType.TrackStarted && e.A == b.Id), TimeSpan.FromSeconds(5)).Should().BeTrue();
        (EngineEvent Event, long ElapsedTicks) started = log.Snapshot().Single(e => e.Event.Type == EngineEventType.TrackStarted && e.Event.A == b.Id);
        long joinBytes = started.Event.B;
        joinBytes.Should().Be(Rate / 2 * BytesPerFrame, "the join is 0.5 s of mixer output in");

        // AC-43: the boundary is the pull that carried the join out; the event, delivered on the pump thread, must
        // reach a subscriber within 100 ms of it (the session then flips now-playing when Clock.HasPlayed(B)).
        (long ElapsedTicks, long MixerBytesAfter) boundary = pulls.First(p => p.MixerBytesAfter >= joinBytes);
        double lagMs = (started.ElapsedTicks - boundary.ElapsedTicks) * 1000.0 / Stopwatch.Frequency;
        lagMs.Should().BeInRange(-10, 100, "the now-playing change follows the audible boundary within 100 ms");
        engine.Clock.HasPlayed(joinBytes).Should().BeTrue();
    }

    [Fact]
    public async Task On_a_live_device_the_clock_says_when_the_join_has_been_heard_within_100_ms()
    {
        await using NativeAudioEngine engine = NativeAudioEngine.Create();
        try
        {
            await engine.InitializeAsync(new OutputConfig(BufferMs: 200));
        }
        catch (NativeException ex) when (ex.Result is MpResult.Device or MpResult.Bass)
        {
            return; // no output device
        }

        using var log = new EventLog(engine);
        TrackHandle a = await engine.OpenAsync(Fixture("wav", "a"));
        TrackHandle b = await engine.OpenAsync(Fixture("wav", "b"));
        engine.SetVolume(0f);
        await engine.PlayAsync(a, TimeSpan.FromSeconds(1.5));
        await engine.PreloadNextAsync(b);

        log.WaitFor(events => events.Any(e => e.Type == EngineEventType.TrackStarted && e.A == b.Id), TimeSpan.FromSeconds(5)).Should().BeTrue();
        (EngineEvent Event, long ElapsedTicks) started = log.Snapshot().Single(e => e.Event.Type == EngineEventType.TrackStarted && e.Event.A == b.Id);
        PlaybackClock atEvent = engine.Clock;
        long joinBytes = started.Event.B;
        int bytesPerSecond = engine.Stats.OutputSampleRate * engine.Stats.OutputChannels * sizeof(float);

        // The event fires when the join is mixed; the join is heard once those bytes have left the output buffer.
        // The clock read at the event predicts when, and polling the clock must agree with that prediction.
        double predictedMs = started.ElapsedTicks * 1000.0 / Stopwatch.Frequency
            + Math.Max(0, joinBytes - atEvent.AudibleMixerBytePosition) * 1000.0 / bytesPerSecond;
        SpinWait.SpinUntil(() => engine.Clock.HasPlayed(joinBytes), TimeSpan.FromSeconds(2)).Should().BeTrue();
        double heardMs = log.Clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency;
        heardMs.Should().BeApproximately(predictedMs, 100, "the join is heard when the clock says it has left the buffer");
        (heardMs - started.ElapsedTicks * 1000.0 / Stopwatch.Frequency).Should()
            .BeLessThanOrEqualTo(engine.Stats.OutputBuffer.TotalMilliseconds + 100, "at most one output buffer after the event");
        await engine.StopAsync(FadeMode.None);
    }

    [Fact]
    public async Task Changing_the_next_track_a_thousand_times_leaves_handles_stable_and_only_the_last_one_queued()
    {
        await using NativeAudioEngine engine = await CreateHeadlessAsync();
        using var log = new EventLog(engine);
        TrackHandle a = await engine.OpenAsync(Fixture("wav", "a"));
        await engine.PlayAsync(a);

        // The engine only points at the queued track; a replaced one keeps its stream until it is closed, so the
        // caller closes what it replaces (as the session will). Warm up first so pools and caches do not count.
        TrackHandle next = await engine.OpenAsync(Fixture("wav", "b"));
        await engine.PreloadNextAsync(next);
        for (int i = 0; i < 20; i++)
        {
            next = await ReplaceAsync(engine, next);
        }

        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        int handlesBefore = process.HandleCount;
        long memoryBefore = process.PrivateMemorySize64;
        for (int i = 0; i < 1000; i++)
        {
            next = await ReplaceAsync(engine, next);
        }

        process.Refresh();
        (process.HandleCount - handlesBefore).Should().BeLessThanOrEqualTo(8, "AC-44: a discarded preload leaks no handle");
        (process.PrivateMemorySize64 - memoryBefore).Should().BeLessThan(8 << 20, "nor memory");

        // Only the survivor joins.
        await engine.SeekAsync(TimeSpan.FromSeconds(1.9));
        float[] buffer = new float[Rate / 2 * Channels];
        engine.Native.Render(buffer, Rate / 2);
        log.WaitFor(events => events.Any(e => e.Type == EngineEventType.TrackStarted && e.B > 0), TimeSpan.FromSeconds(5)).Should().BeTrue();
        IReadOnlyList<EngineEvent> events = log.Snapshot().Select(e => e.Event).ToList();
        events.Where(e => e.Type == EngineEventType.TrackStarted).Should().HaveCount(2);
        events.Last(e => e.Type == EngineEventType.TrackStarted).A.Should().Be(next.Id);
        events.Single(e => e.Type == EngineEventType.TrackEnded).A.Should().Be(a.Id);
    }

    private static async Task<TrackHandle> ReplaceAsync(NativeAudioEngine engine, TrackHandle old)
    {
        TrackHandle fresh = await engine.OpenAsync(Fixture("wav", "b"));
        await engine.PreloadNextAsync(fresh);
        await engine.CloseAsync(old);
        return fresh;
    }
}
