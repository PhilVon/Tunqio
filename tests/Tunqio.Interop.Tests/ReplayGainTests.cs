using Tunqio.Core.Audio;

namespace Tunqio.Interop.Tests;

/// <summary>
/// E1-S5: ReplayGain through <see cref="IAudioEngine"/>. The native <c>[replaygain]</c> suite measures the mechanism; these
/// tests prove the managed contract over it: a track set to -6 dB is heard 6 dB quieter, a gain that would clip is held at
/// full scale, and the gain of a preloaded track is in force from the first frame after the gapless join.
/// </summary>
[Collection("native engine")]
public class ReplayGainTests
{
    private const int Rate = 48_000;
    private const int Channels = 2;

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

    private static double Rms(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (float s in samples)
        {
            sum += (double)s * s;
        }

        return samples.Length == 0 ? 0 : Math.Sqrt(sum / samples.Length);
    }

    private static double Db(double ratio) => 20 * Math.Log10(ratio);

    /// <summary>Plays the file from the start with the gain and returns one second of output; the second half has settled.</summary>
    private static async Task<float[]> PlayWithGainAsync(NativeAudioEngine engine, string path, float gainDb, float peak)
    {
        TrackHandle track = await engine.OpenAsync(path);
        engine.SetReplayGain(track, gainDb, peak);
        await engine.PlayAsync(track);
        float[] audio = Render(engine, 1000);
        await engine.StopAsync(FadeMode.None);
        await engine.CloseAsync(track);
        return audio;
    }

    [Fact]
    public async Task A_track_set_to_minus_6_dB_is_heard_6_dB_quieter_Async()
    {
        await using NativeAudioEngine engine = await CreateHeadlessAsync();
        string path = WavFixture.WriteSine("replaygain-6db", seconds: 1.5, amplitude: 0.1);

        float[] plain = await PlayWithGainAsync(engine, path, 0f, 0f);
        float[] quieter = await PlayWithGainAsync(engine, path, -6f, 0.1f);
        double plainRms = Rms(plain.AsSpan(Rate)); // second half: past the fade-in
        double quieterRms = Rms(quieter.AsSpan(Rate));
        plainRms.Should().BeApproximately(0.1 / Math.Sqrt(2), 0.002);
        Db(quieterRms / plainRms).Should().BeApproximately(-6, 0.1);
    }

    [Fact]
    public async Task A_gain_that_would_clip_is_held_at_full_scale_Async()
    {
        await using NativeAudioEngine engine = await CreateHeadlessAsync();
        string path = WavFixture.WriteSine("replaygain-peak", seconds: 1.5, amplitude: 0.5);

        float[] boosted = await PlayWithGainAsync(engine, path, 12f, 0.5f); // +12 dB would peak at 1.99
        float[] settled = boosted[Rate..]; // second half: past the fade-in
        float loudest = settled.Max(Math.Abs);
        loudest.Should().BeLessThanOrEqualTo(1.0001f).And.BeGreaterThan(0.99f, "the gain is reduced to exactly 1/peak");
        Rms(settled).Should().BeApproximately(1 / Math.Sqrt(2), 0.01, "a full-scale sine, not a clipped one");

        float[] unknownPeak = await PlayWithGainAsync(engine, path, 12f, 0f);
        unknownPeak[Rate..].Max().Should().BeGreaterThan(1.5f, "no peak information means no limiting");
    }

    [Fact]
    public async Task The_next_tracks_gain_is_in_force_from_the_first_frame_after_the_join_Async()
    {
        await using NativeAudioEngine engine = await CreateHeadlessAsync();
        TrackHandle a = await engine.OpenAsync(WavFixture.WriteSine("replaygain-join-a", seconds: 1.0, frequencyHz: 10, amplitude: 0.5));
        TrackHandle b = await engine.OpenAsync(WavFixture.WriteSine("replaygain-join-b", seconds: 1.0, frequencyHz: 10, amplitude: 0.5));
        engine.SetReplayGain(b, -12f, 0.5f);

        await engine.PlayAsync(a);
        await engine.PreloadNextAsync(b);
        float[] audio = Render(engine, 2000);

        const int Join = Rate; // a is exactly 1.0 s
        double before = Rms(audio.AsSpan((Join - Rate / 5) * Channels, Rate / 5 * Channels)); // two whole 10 Hz cycles
        double after = Rms(audio.AsSpan(Join * Channels, Rate / 5 * Channels));
        before.Should().BeApproximately(0.5 / Math.Sqrt(2), 0.005);
        Db(after / before).Should().BeApproximately(-12, 0.15);

        // The first 10 ms of b are already quiet: the gain did not arrive a buffer late.
        Rms(audio.AsSpan(Join * Channels, Rate / 100 * Channels)).Should().BeLessThan(Rms(audio.AsSpan((Join - Rate / 100) * Channels, Rate / 100 * Channels)) * 0.5);

        await engine.CloseAsync(a);
        await engine.CloseAsync(b);
    }

    [Fact]
    public async Task A_closed_or_foreign_handle_is_rejected_Async()
    {
        await using NativeAudioEngine engine = await CreateHeadlessAsync();
        TrackHandle track = await engine.OpenAsync(WavFixture.WriteSine("replaygain-closed"));
        await engine.CloseAsync(track);
        FluentActions.Invoking(() => engine.SetReplayGain(track, 0f, 1f)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => engine.SetReplayGain(new TrackHandle(12345, track.Info), 0f, 1f)).Should().Throw<ArgumentException>();
    }
}
