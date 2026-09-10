using System.Globalization;
using System.Reactive.Subjects;
using Tunqio.Core.Audio;

namespace Tunqio.Core.Tests.Playback;

/// <summary>
/// An <see cref="IAudioEngine"/> that records what it was asked to do instead of making a sound, so a scripted
/// sequence of transport commands can be compared with the calls it should have produced (E1-S10, AC-63). It also
/// stands in for the parts of the engine the session has to read back: the clock, and the events the native side
/// raises at a join.
/// </summary>
internal sealed class FakeAudioEngine : IAudioEngine
{
    private readonly Subject<EngineEvent> _events = new();
    private long _nextHandleId = 1;

    /// <summary>Every call, in order, as short strings — the expectation table compares against these.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Handles opened and not yet closed.</summary>
    public HashSet<long> OpenHandles { get; } = [];

    /// <summary>What <see cref="Clock"/> reports; a test moves it to drive Previous and the join.</summary>
    public PlaybackClock Clock { get; set; } = new(TimeSpan.Zero, 0, TimeSpan.Zero, 0, 0);

    /// <summary>Paths that fail to open, so the session's skip-past-unplayable path can be exercised.</summary>
    public HashSet<string> Unopenable { get; } = [];

    /// <summary>What <see cref="EnumerateDevices"/> reports; empty is a machine the engine sees no outputs on.</summary>
    public List<OutputDevice> Devices { get; } = [];

    /// <summary>Device indices <see cref="InitializeAsync"/> refuses, so the host's output fallbacks can be exercised.</summary>
    public HashSet<int> UnopenableDevices { get; } = [];

    /// <summary>The exception a refused device throws; the host only falls back for the ones the interop raises.</summary>
    public Exception? RefuseOutput { get; set; }

    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(3);

    public EngineStats Stats { get; } = new(0, 0, TimeSpan.Zero, 48000, 2, TimeSpan.Zero, false, true, "48000/2/32");

    public IObservable<EngineEvent> Events => _events;

    public void Raise(EngineEvent e) => _events.OnNext(e);

    /// <summary>Takes and clears the calls recorded since the last take.</summary>
    public string[] Drain()
    {
        string[] calls = [.. Calls];
        Calls.Clear();
        return calls;
    }

    public Task InitializeAsync(OutputConfig config, CancellationToken ct = default)
    {
        Calls.Add("init:" + config.DeviceIndex + ":" + (config.Mode == OutputMode.Exclusive ? "exclusive" : "shared") + ":" + config.BufferMs);
        if (UnopenableDevices.Contains(config.DeviceIndex))
        {
            throw RefuseOutput ?? new InvalidOperationException("device " + config.DeviceIndex + " will not open");
        }

        return Task.CompletedTask;
    }

    public Task<TrackHandle> OpenAsync(string path, CancellationToken ct = default)
    {
        if (Unopenable.Contains(path))
        {
            Calls.Add("open-failed:" + path);
            throw new InvalidOperationException("cannot open " + path);
        }

        long id = _nextHandleId++;
        OpenHandles.Add(id);
        Calls.Add($"open:{id}:{path}");
        return Task.FromResult(new TrackHandle(id, new TrackInfo(Duration, 44100, 2, 16, "flac", 0)));
    }

    public Task CloseAsync(TrackHandle track)
    {
        OpenHandles.Remove(track.Id);
        Calls.Add("close:" + track.Id);
        return Task.CompletedTask;
    }

    public Task PlayAsync(TrackHandle track, TimeSpan? startAt = null, CancellationToken ct = default)
    {
        Calls.Add($"play:{track.Id}@{Ms(startAt ?? TimeSpan.Zero)}");
        return Task.CompletedTask;
    }

    public Task PreloadNextAsync(TrackHandle? nextTrack, JoinMode join = JoinMode.Gapless, CancellationToken ct = default)
    {
        Calls.Add(nextTrack is null ? "preload:none" : $"preload:{nextTrack.Id}:{join}".ToLowerInvariant());
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        Calls.Add("pause");
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        Calls.Add("resume");
        return Task.CompletedTask;
    }

    public Task StopAsync(FadeMode fade = FadeMode.Guard)
    {
        Calls.Add("stop");
        return Task.CompletedTask;
    }

    public Task SeekAsync(TimeSpan position)
    {
        Calls.Add("seek:" + Ms(position));
        return Task.CompletedTask;
    }

    public void SetVolume(float slider) => Calls.Add("volume:" + slider.ToString("0.##", CultureInfo.InvariantCulture));

    public void SetReplayGain(TrackHandle track, float gainDb, float peak) =>
        Calls.Add($"gain:{track.Id}:{gainDb.ToString("0.##", CultureInfo.InvariantCulture)}:{peak.ToString("0.##", CultureInfo.InvariantCulture)}");

    public void SetCrossfade(TimeSpan duration) => Calls.Add("crossfade:" + Ms(duration));

    public Task StartPreviewAsync(TrackHandle track, float gainDb) => throw new NotSupportedException();

    public Task StopPreviewAsync() => throw new NotSupportedException();

    public IReadOnlyList<OutputDevice> EnumerateDevices() => Devices;

    public ValueTask DisposeAsync()
    {
        _events.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Ms(TimeSpan value) => ((long)value.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
}
