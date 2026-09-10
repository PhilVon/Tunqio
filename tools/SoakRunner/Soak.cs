using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Tunqio.Core.Audio;
using Tunqio.Interop;

namespace Tunqio.SoakRunner;

/// <summary>
/// E1-S11: keeps a real output device fed from the fixture library for as long as it is asked to, and reports what
/// <c>mp_engine_stats</c> saw. It drives <see cref="NativeAudioEngine"/> directly rather than through
/// <c>PlaybackSession</c>, because what is under test is the engine, the mixer and the WASAPI proc — a soak that
/// went through the session would also be soaking the session's bookkeeping, and a failure would name the wrong
/// thing.
/// </summary>
/// <remarks>
/// Events arrive on the interop pump thread and only ever queue here; the loop below is the only thing that opens,
/// closes or plays, which is the same rule the session keeps and for the same reason. The stall watchdog matters as
/// much as the underrun count: an engine that stops pulling produces a beautifully clean stats line, so a run that
/// stopped making sound has to fail rather than pass quietly.
/// </remarks>
internal sealed class Soak : IAsyncDisposable
{
    /// <summary>How long the clock may stand still, while something is supposed to be playing, before it is a stall.</summary>
    private static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(10);

    private readonly SoakOptions _options;
    private readonly TextWriter _out;
    private readonly ConcurrentQueue<EngineEvent> _events = new();
    private readonly List<string> _errors = [];
    private readonly List<SoakSample> _samples = [];
    private readonly Random _rng = new(20260910);
    private readonly string[] _tracks;

    private NativeAudioEngine? _engine;
    private IDisposable? _subscription;
    private TrackHandle? _current;
    private TrackHandle? _next;
    private int _cursor;
    private int _played;
    private int _joins;
    private int _seeks;
    private int _stalls;
    private int _deviceEvents;
    private int _underrunEvents;

    public Soak(SoakOptions options, TextWriter output)
    {
        _options = options;
        _out = output;
        _tracks = Discover(options.LibraryDirectory);
        if (_tracks.Length < 2)
        {
            throw new SoakUsageException(
                $"{options.LibraryDirectory} holds {_tracks.Length} playable file(s); a soak needs at least two so there is a boundary to cross. " +
                "Generate the fixtures first: dotnet run -c Release -p:Platform=x64 --project tools/FixtureGen -- files -out tests/fixtures/library");
        }
    }

    public async Task<SoakReport> RunAsync(CancellationToken ct)
    {
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        long workingSetStart = WorkingSetMb();
        NativeAudioEngine engine = NativeAudioEngine.Create();
        _engine = engine;
        _subscription = engine.Events.Subscribe(_events.Enqueue);

        OutputDevice? device = _options.DeviceIndex == OutputConfig.DefaultDevice
            ? null
            : engine.EnumerateDevices().FirstOrDefault(d => d.Index == _options.DeviceIndex);
        await engine.InitializeAsync(new OutputConfig(_options.DeviceIndex, _options.Mode, _options.BufferMs), ct).ConfigureAwait(false);
        engine.SetVolume(_options.Volume);

        EngineStats opened = engine.Stats;
        await _out.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"soaking {_tracks.Length} track(s) for {_options.Duration.TotalMinutes:F0} min on {device?.Name ?? "the system default"} " +
            $"({(opened.Exclusive ? "exclusive" : "shared")}, {opened.OutputFormat}, volume {_options.Volume:F2})"));

        await StartAsync(Take(), ct).ConfigureAwait(false);
        await QueueNextAsync(ct).ConfigureAwait(false);

        var clock = Stopwatch.StartNew();
        TimeSpan lastSample = TimeSpan.Zero;
        TimeSpan lastSeek = TimeSpan.Zero;
        TimeSpan movedAt = TimeSpan.Zero;
        TimeSpan lastPosition = TimeSpan.MinValue;

        while (clock.Elapsed < _options.Duration && !ct.IsCancellationRequested)
        {
            await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            await DrainEventsAsync(ct).ConfigureAwait(false);

            TimeSpan position = engine.Clock.Position;
            if (position != lastPosition)
            {
                lastPosition = position;
                movedAt = clock.Elapsed;
            }
            else if (clock.Elapsed - movedAt > StallAfter)
            {
                _stalls++;
                _errors.Add(string.Create(CultureInfo.InvariantCulture, $"the clock stood still at {position} for {StallAfter.TotalSeconds:F0} s; restarting"));
                movedAt = clock.Elapsed;
                await RestartAsync(ct).ConfigureAwait(false);
            }

            if (_options.SeekEvery > TimeSpan.Zero && clock.Elapsed - lastSeek >= _options.SeekEvery)
            {
                lastSeek = clock.Elapsed;
                await SeekSomewhereAsync().ConfigureAwait(false);
            }

            if (clock.Elapsed - lastSample >= _options.SampleEvery)
            {
                lastSample = clock.Elapsed;
                Sample(clock.Elapsed, engine.Stats, position);
            }
        }

        clock.Stop();
        Sample(clock.Elapsed, engine.Stats, engine.Clock.Position);
        await DrainEventsAsync(CancellationToken.None).ConfigureAwait(false);
        EngineStats final = engine.Stats;

        return new SoakReport(
            StartedUtc: startedUtc.ToString("O", CultureInfo.InvariantCulture),
            RequestedSeconds: _options.Duration.TotalSeconds,
            ElapsedSeconds: clock.Elapsed.TotalSeconds,
            RanToCompletion: clock.Elapsed >= _options.Duration,
            Device: device?.Name ?? "system default",
            OutputFormat: final.OutputFormat,
            Exclusive: final.Exclusive,
            BufferMs: _options.BufferMs,
            Join: _options.Join.ToString(),
            Volume: _options.Volume,
            LibraryTracks: _tracks.Length,
            TracksPlayed: _played,
            Joins: _joins,
            Seeks: _seeks,
            Callbacks: final.Callbacks,
            Underruns: final.Underruns,
            UnderrunEvents: _underrunEvents,
            CallbackMaxMs: final.CallbackMax.TotalMilliseconds,
            Stalls: _stalls,
            DeviceEvents: _deviceEvents,
            Errors: _errors,
            WorkingSetStartMb: workingSetStart,
            WorkingSetEndMb: WorkingSetMb(),
            Samples: _samples);
    }

    /// <summary>
    /// The engine talks, the loop acts. A join is announced as <see cref="EngineEventType.TrackStarted"/> for the
    /// handle that was preloaded: the predecessor is then finished with, and the track after it is queued.
    /// </summary>
    private async Task DrainEventsAsync(CancellationToken ct)
    {
        while (_events.TryDequeue(out EngineEvent? e))
        {
            switch (e.Type)
            {
                case EngineEventType.TrackStarted when _next is { } queued && e.A == queued.Id:
                    _joins++;
                    _played++;
                    await CloseAsync(_current).ConfigureAwait(false);
                    _current = queued;
                    _next = null;
                    await QueueNextAsync(ct).ConfigureAwait(false);
                    break;

                case EngineEventType.TrackEnded when _current is { } playing && e.A == playing.Id && _next is null:
                    // Nothing was behind it: restart the loop from the next file rather than letting the device idle.
                    await RestartAsync(ct).ConfigureAwait(false);
                    break;

                case EngineEventType.Underrun:
                    _underrunEvents++;
                    _errors.Add(string.Create(CultureInfo.InvariantCulture, $"underrun event after {_played} track(s): {e.Message ?? "no detail"}"));
                    break;

                case EngineEventType.Error:
                    _errors.Add(e.Message ?? "unnamed engine error");
                    break;

                case EngineEventType.DeviceLost:
                case EngineEventType.DeviceChanged:
                    _deviceEvents++;
                    await _out.WriteLineAsync($"device event: {e.Type} on device {e.A} ({e.Message})").ConfigureAwait(false);
                    break;

                default:
                    break;
            }
        }
    }

    private async Task StartAsync(string path, CancellationToken ct)
    {
        _current = await _engine!.OpenAsync(path, ct).ConfigureAwait(false);
        await _engine.PlayAsync(_current, null, ct).ConfigureAwait(false);
        _played++;
    }

    private async Task QueueNextAsync(CancellationToken ct)
    {
        _next = await _engine!.OpenAsync(Take(), ct).ConfigureAwait(false);
        await _engine.PreloadNextAsync(_next, _options.Join, ct).ConfigureAwait(false);
    }

    /// <summary>Back to a known state after a stall or a queue that ran dry: everything closed, the next file playing.</summary>
    private async Task RestartAsync(CancellationToken ct)
    {
        await _engine!.StopAsync(FadeMode.None).ConfigureAwait(false);
        await _engine.PreloadNextAsync(null, _options.Join, ct).ConfigureAwait(false);
        await CloseAsync(_next).ConfigureAwait(false);
        await CloseAsync(_current).ConfigureAwait(false);
        _next = null;
        _current = null;
        await StartAsync(Take(), ct).ConfigureAwait(false);
        await QueueNextAsync(ct).ConfigureAwait(false);
    }

    private async Task SeekSomewhereAsync()
    {
        if (_current is not { } playing || playing.Info.Duration <= TimeSpan.FromSeconds(2))
        {
            return;
        }

        double seconds = _rng.NextDouble() * (playing.Info.Duration.TotalSeconds - 1);
        await _engine!.SeekAsync(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
        _seeks++;
    }

    private async Task CloseAsync(TrackHandle? handle)
    {
        if (handle is not null)
        {
            await _engine!.CloseAsync(handle).ConfigureAwait(false);
        }
    }

    private void Sample(TimeSpan elapsed, EngineStats stats, TimeSpan position)
    {
        var sample = new SoakSample(
            Math.Round(elapsed.TotalSeconds, 1),
            stats.Callbacks,
            stats.Underruns,
            Math.Round(stats.CallbackMax.TotalMilliseconds, 3),
            Math.Round(position.TotalSeconds, 1),
            WorkingSetMb());
        _samples.Add(sample);
        _out.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{elapsed.TotalMinutes,6:F1} min · {_played,4} tracks · {_joins,4} joins · {stats.Callbacks,9} callbacks · " +
            $"{stats.Underruns,3} underruns · callback max {stats.CallbackMax.TotalMilliseconds,6:F2} ms · {sample.WorkingSetMb,5} MB"));
    }

    private string Take() => _tracks[_cursor++ % _tracks.Length];

    private static long WorkingSetMb() => Environment.WorkingSet / (1024 * 1024);

    /// <summary>Every playable file under the library, in a stable order so two runs loop the same way.</summary>
    private static string[] Discover(string directory) =>
        Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(f => SoakOptions.AudioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)]
            : [];

    public async ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        if (_engine is { } engine)
        {
            try
            {
                await engine.StopAsync(FadeMode.None).ConfigureAwait(false);
                await CloseAsync(_next).ConfigureAwait(false);
                await CloseAsync(_current).ConfigureAwait(false);
            }
            catch (NativeException)
            {
                // The device may already be gone; the report is what matters by this point.
            }

            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }
}
