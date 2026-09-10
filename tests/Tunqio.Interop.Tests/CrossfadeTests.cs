using Tunqio.Core.Audio;

namespace Tunqio.Interop.Tests;

/// <summary>
/// E1-S4: the crossfade through <see cref="IAudioEngine"/>. The native <c>[crossfade]</c> suite measures the mechanism;
/// these tests prove the managed contract over it: a 5 s crossfade overlaps two tracks with the RMS held within 3 dB,
/// the events say where the overlap began, and a gapless join is left alone by the crossfade setting.
/// </summary>
[Collection("native engine")]
public class CrossfadeTests
{
    private const int Rate = 48_000;
    private const int Channels = 2;
    private const int BytesPerFrame = Channels * sizeof(float);
    private const double Amplitude = 0.5;
    private static readonly double Level = Amplitude / Math.Sqrt(2);

    private static async Task<NativeAudioEngine> CreateHeadlessAsync()
    {
        NativeAudioEngine engine = NativeAudioEngine.Create(Rate, Channels);
        await engine.InitializeAsync(new OutputConfig(DeviceIndex: OutputConfig.NoDevice));
        return engine;
    }

    private static float[] Render(NativeAudioEngine engine, int ms)
    {
        int frames = Rate * ms / 1000;
        float[] audio = new float[frames * Channels];
        const int Piece = Rate / 100;
        for (int done = 0; done < frames; done += Piece)
        {
            int n = Math.Min(Piece, frames - done);
            engine.Native.Render(audio.AsSpan(done * Channels, n * Channels), n);
        }

        return audio;
    }

    private static double Rms(float[] audio, int fromFrame, int toFrame)
    {
        double sum = 0;
        int n = 0;
        for (int i = fromFrame * Channels; i < toFrame * Channels; i++)
        {
            sum += (double)audio[i] * audio[i];
            n++;
        }

        return Math.Sqrt(sum / n);
    }

    private static double Db(double ratio) => 20 * Math.Log10(ratio);

    private sealed class EventLog : IDisposable
    {
        private readonly List<EngineEvent> _events = [];
        private readonly IDisposable _subscription;

        public EventLog(IAudioEngine engine)
        {
            _subscription = engine.Events.Subscribe(e =>
            {
                lock (_events)
                {
                    _events.Add(e);
                }
            });
        }

        public List<EngineEvent> Snapshot()
        {
            lock (_events)
            {
                return [.. _events];
            }
        }

        public bool WaitFor(Func<List<EngineEvent>, bool> condition, TimeSpan timeout) => SpinWait.SpinUntil(() => condition(Snapshot()), timeout);

        public void Dispose() => _subscription.Dispose();
    }

    [Fact]
    public async Task A_5_s_crossfade_overlaps_two_tracks_with_the_level_held_within_3_dB()
    {
        await using NativeAudioEngine engine = await CreateHeadlessAsync();
        using var log = new EventLog(engine);
        engine.SetCrossfade(TimeSpan.FromSeconds(5));
        TrackHandle a = await engine.OpenAsync(WavFixture.WriteSine("crossfade-a", seconds: 6, frequencyHz: 440, amplitude: Amplitude));
        TrackHandle b = await engine.OpenAsync(WavFixture.WriteSine("crossfade-b", seconds: 6, frequencyHz: 660, amplitude: Amplitude));

        await engine.PlayAsync(a);
        await engine.PreloadNextAsync(b, JoinMode.Crossfade);
        float[] audio = Render(engine, 7500);

        // a runs 0-6 s, so the overlap is 1-6 s; b then plays alone to 7 s.
        const int FadeStart = Rate;
        const int FadeEnd = 6 * Rate;
        double worst = 0;
        for (int w = FadeStart; w + Rate / 4 <= FadeEnd; w += Rate / 4)
        {
            worst = Math.Max(worst, Math.Abs(Db(Rms(audio, w, w + Rate / 4) / Level)));
        }

        worst.Should().BeLessThan(3, "equal power keeps the RMS of two uncorrelated sines flat across the overlap");
        Rms(audio, Rate / 2, FadeStart).Should().BeApproximately(Level, 0.005, "a alone before the overlap");
        Rms(audio, FadeEnd + Rate / 4, 7 * Rate).Should().BeApproximately(Level, 0.005, "b alone after it");

        log.WaitFor(events => events.Any(e => e.Type == EngineEventType.TrackEnded && e.A == b.Id), TimeSpan.FromSeconds(5)).Should().BeTrue();
        List<EngineEvent> events = log.Snapshot().Where(e => e.Type is EngineEventType.TrackStarted or EngineEventType.TrackEnded).ToList();
        events.Select(e => (e.Type, e.A)).Should().Equal(
            (EngineEventType.TrackStarted, a.Id),
            (EngineEventType.TrackStarted, b.Id),
            (EngineEventType.TrackEnded, a.Id),
            (EngineEventType.TrackEnded, b.Id));
        long overlapStart = events[1].B;
        overlapStart.Should().BeInRange((FadeStart - Rate / 100) * BytesPerFrame, FadeStart * BytesPerFrame, "b starts at the fade point, within one 10 ms buffer");
        events[2].B.Should().Be(0, "no successor took over at a's end: it faded out under b");

        await engine.CloseAsync(a);
        await engine.CloseAsync(b);
    }

    [Fact]
    public async Task A_gapless_join_ignores_the_crossfade_setting()
    {
        await using NativeAudioEngine engine = await CreateHeadlessAsync();
        using var log = new EventLog(engine);
        engine.SetCrossfade(TimeSpan.FromSeconds(5));
        // 10 Hz: a ends on a zero crossing and b starts on one, so a continuous join has no step.
        TrackHandle a = await engine.OpenAsync(WavFixture.WriteSine("crossfade-gapless-a", seconds: 1, frequencyHz: 10, amplitude: Amplitude));
        TrackHandle b = await engine.OpenAsync(WavFixture.WriteSine("crossfade-gapless-b", seconds: 1, frequencyHz: 10, amplitude: Amplitude));

        await engine.PlayAsync(a);
        await engine.PreloadNextAsync(b, JoinMode.Gapless);
        float[] audio = Render(engine, 2000);

        const int Join = Rate;
        Rms(audio, Join - Rate / 4, Join).Should().BeApproximately(Level, 0.005, "no fade-out");
        Rms(audio, Join, Join + Rate / 4).Should().BeApproximately(Level, 0.005, "no fade-in");
        Math.Abs(audio[Join * Channels] - audio[(Join - 1) * Channels]).Should().BeLessThan(0.002f);
        log.WaitFor(events => events.Any(e => e.Type == EngineEventType.TrackStarted && e.A == b.Id), TimeSpan.FromSeconds(5)).Should().BeTrue();
        List<EngineEvent> events = log.Snapshot().Where(e => e.Type is EngineEventType.TrackStarted or EngineEventType.TrackEnded).ToList();
        events.Take(3).Should().Equal(
            new EngineEvent(EngineEventType.TrackStarted, a.Id, 0, null),
            new EngineEvent(EngineEventType.TrackEnded, a.Id, Join * BytesPerFrame, null),
            new EngineEvent(EngineEventType.TrackStarted, b.Id, Join * BytesPerFrame, null));
    }

    [Fact]
    public async Task The_crossfade_setting_is_clamped_not_refused()
    {
        await using NativeAudioEngine engine = await CreateHeadlessAsync();
        FluentActions.Invoking(() => engine.SetCrossfade(TimeSpan.FromMinutes(1))).Should().NotThrow();
        FluentActions.Invoking(() => engine.SetCrossfade(TimeSpan.FromSeconds(-1))).Should().NotThrow();
        FluentActions.Invoking(() => engine.SetCrossfade(TimeSpan.Zero)).Should().NotThrow();
    }
}
