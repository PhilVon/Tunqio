using Tunqio.Core.Audio;

namespace Tunqio.Interop.Tests;

/// <summary>
/// E1-S1: <see cref="IAudioEngine"/> over the native core, driven headless (<see cref="OutputConfig.NoDevice"/>)
/// so the whole transport, the clock and the event stream run in CI without a sound device.
/// </summary>
[Collection("native engine")]
public class AudioEngineTests
{
    private const int Rate = 48_000;

    private static async Task<NativeAudioEngine> CreateHeadlessAsync()
    {
        NativeAudioEngine engine = NativeAudioEngine.Create(Rate, 2);
        await engine.InitializeAsync(new OutputConfig(DeviceIndex: OutputConfig.NoDevice));
        return engine;
    }

    /// <summary>Pulls <paramref name="ms"/> milliseconds through the engine in 10 ms pieces and returns the samples.</summary>
    private static float[] Render(NativeAudioEngine engine, int ms)
    {
        int frames = Rate * ms / 1000;
        float[] audio = new float[frames * 2];
        const int Piece = Rate / 100;
        for (int done = 0; done < frames; done += Piece)
        {
            int n = Math.Min(Piece, frames - done);
            engine.Native.Render(audio.AsSpan(done * 2, n * 2), n);
        }

        return audio;
    }

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float s in samples)
        {
            sum += (double)s * s;
        }

        return samples.Length == 0 ? 0 : Math.Sqrt(sum / samples.Length);
    }

    [Fact]
    public async Task Plays_pauses_seeks_and_stops_headless_with_a_clock_that_follows_Async()
    {
        await using NativeAudioEngine engine = await CreateHeadlessAsync();
        TrackHandle track = await engine.OpenAsync(WavFixture.WriteSine("audio-engine", seconds: 3.0));
        track.Info.Duration.Should().BeCloseTo(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(5));
        engine.Stats.OutputFormat.Should().Be("render");

        await engine.PlayAsync(track);
        float[] audio = Render(engine, 1000);
        Rms(audio.AsSpan(Rate)).Should().BeApproximately(0.1 / Math.Sqrt(2), 0.005, "the sine comes through after the fade-in");
        engine.Clock.Position.Should().BeCloseTo(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(25));

        await engine.PauseAsync();
        Render(engine, 200);
        TimeSpan held = engine.Clock.Position;
        Render(engine, 200).Should().OnlyContain(s => s == 0f, "a held engine is silent");
        engine.Clock.Position.Should().Be(held, "the position freezes while paused");

        await engine.ResumeAsync();
        Render(engine, 300);
        engine.Clock.Position.Should().BeGreaterThan(held + TimeSpan.FromMilliseconds(200));

        await engine.SeekAsync(TimeSpan.FromMilliseconds(500));
        Render(engine, 100);
        engine.Clock.Position.Should().BeCloseTo(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(20));

        await engine.StopAsync(FadeMode.Guard);
        engine.Clock.Position.Should().Be(TimeSpan.Zero);
        await engine.CloseAsync(track);
        await engine.CloseAsync(track); // idempotent
    }

    [Fact]
    public async Task Events_carry_the_track_id_and_the_natural_end_is_reported_Async()
    {
        await using NativeAudioEngine engine = await CreateHeadlessAsync();
        var received = new List<EngineEvent>();
        using IDisposable subscription = engine.Events.Subscribe(e => { lock (received) { received.Add(e); } });
        TrackHandle track = await engine.OpenAsync(WavFixture.WriteSine("audio-engine-end", seconds: 0.25));

        await engine.PlayAsync(track);
        Render(engine, 1000);
        SpinWait.SpinUntil(() => { lock (received) { return received.Count >= 2; } }, TimeSpan.FromSeconds(5));

        EngineEvent[] events;
        lock (received)
        {
            events = [.. received];
        }

        events.Should().HaveCountGreaterThanOrEqualTo(2);
        events[0].Type.Should().Be(EngineEventType.TrackStarted);
        events[0].A.Should().Be(track.Id, "the started event names the handle the track was opened as");
        events.Should().Contain(e => e.Type == EngineEventType.TrackEnded);
    }

    [Fact]
    public async Task A_track_from_elsewhere_and_calls_after_dispose_are_rejected_Async()
    {
        NativeAudioEngine engine = await CreateHeadlessAsync();
        var foreign = new TrackHandle(12345, new TrackInfo(TimeSpan.Zero, Rate, 2, 16, "wav", 0));
        await FluentActions.Awaiting(() => engine.PlayAsync(foreign)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => engine.PreloadNextAsync(foreign)).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => engine.PreloadNextAsync(null)).Should().NotThrowAsync("clearing the queue is always valid");

        await engine.DisposeAsync();
        await engine.DisposeAsync();
        FluentActions.Invoking(() => engine.SetVolume(0.5f)).Should().Throw<ObjectDisposedException>();
        await FluentActions.Awaiting(() => engine.PauseAsync()).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Volume_is_applied_at_the_output_Async()
    {
        await using NativeAudioEngine engine = await CreateHeadlessAsync();
        TrackHandle track = await engine.OpenAsync(WavFixture.WriteSine("audio-engine-volume", seconds: 2.0));
        await engine.PlayAsync(track);
        Render(engine, 300);
        double full = Rms(Render(engine, 200));

        engine.SetVolume(0.5f);
        Render(engine, 20); // the interpolation buffer and one more
        Rms(Render(engine, 200)).Should().BeApproximately(full * 0.1, full * 0.01, "-20 dB at half travel");

        engine.SetVolume(0f);
        Render(engine, 20);
        Render(engine, 100).Should().OnlyContain(s => s == 0f);
    }
}
