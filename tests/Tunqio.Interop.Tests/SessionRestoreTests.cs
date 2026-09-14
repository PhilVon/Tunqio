using FluentAssertions;
using Tunqio.Core;
using Tunqio.Core.Audio;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.Interop.Tests;

/// <summary>
/// E1-S10 (AC-65): the queue and the position round-trip through a real engine. Headless
/// (<see cref="OutputConfig.NoDevice"/>), so it runs in CI: the audio is pulled by hand and the clock follows what
/// was pulled, which is exactly what a device would have consumed.
/// </summary>
[Collection("native engine")]
public class SessionRestoreTests
{
    private const int Rate = 48_000;

    private static string Fixture(string stem)
    {
        string dir = RepoPaths.File("tests", "fixtures", "gapless", "flac");
        return Directory.EnumerateFiles(dir).Single(f => Path.GetFileNameWithoutExtension(f) == stem);
    }

    private static async Task<NativeAudioEngine> CreateHeadlessAsync()
    {
        NativeAudioEngine engine = NativeAudioEngine.Create(Rate, 2);
        await engine.InitializeAsync(new OutputConfig(DeviceIndex: OutputConfig.NoDevice));
        return engine;
    }

    /// <summary>Pulls <paramref name="ms"/> milliseconds through the engine, polling the session as a device would.</summary>
    private static async Task RenderAsync(NativeAudioEngine engine, PlaybackSession session, int ms)
    {
        int frames = Rate * ms / 1000;
        float[] audio = new float[Rate / 100 * 2];
        const int Piece = Rate / 100;
        for (int done = 0; done < frames; done += Piece)
        {
            int n = Math.Min(Piece, frames - done);
            engine.Native.Render(audio.AsSpan(0, n * 2), n);
            await session.PollAsync();
        }
    }

    [Fact]
    public async Task The_queue_and_the_position_survive_a_restart_against_the_real_engine_Async()
    {
        var tracks = new StubTracks { [1] = Fixture("a"), [2] = Fixture("b") };
        var queues = new StubQueueStore();
        var history = new StubHistory();
        var settings = new StubSettings();

        await using (NativeAudioEngine engine = await CreateHeadlessAsync())
        {
            var session = new PlaybackSession(engine, tracks, history, queues, settings, autoPoll: false);
            await using (session)
            {
                await session.PlayNowAsync([1, 2], startIndex: 1);
                await RenderAsync(engine, session, 900);

                session.Current.State.Should().Be(PlaybackState.Playing);
                session.Capture().Position.Should().BeCloseTo(TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(120));
            }
        }

        queues.Saved.Should().NotBeNull("disposing the session saves the queue");
        TimeSpan savedAt = queues.Saved!.Position!.Value;

        await using NativeAudioEngine restarted = await CreateHeadlessAsync();
        var second = new PlaybackSession(restarted, tracks, history, queues, settings, autoPoll: false);
        await using (second)
        {
            bool restored = await second.RestoreAsync();

            restored.Should().BeTrue();
            second.Queue.Items.Select(i => i.TrackId).Should().Equal(1, 2);
            second.Queue.CurrentIndex.Should().Be(1, "the track that was playing is current again");
            second.Current.State.Should().Be(PlaybackState.Paused, "restoring opens the track, it does not start it");
            second.Current.Track!.Id.Should().Be(2);

            restarted.Clock.Position.Should().BeCloseTo(savedAt, TimeSpan.FromMilliseconds(60),
                "the real engine is holding the real file at the position it was left");

            // A paused engine renders silence, and resuming carries on from there rather than from the top.
            await second.TogglePlayPauseAsync();
            await RenderAsync(restarted, second, 300);
            restarted.Clock.Position.Should().BeGreaterThan(savedAt);
        }
    }

    private sealed class StubTracks : ITrackRepository
    {
        private readonly Dictionary<long, string> _paths = [];

        public string this[long id] { set => _paths[id] = value; }

        public Task<IReadOnlyList<TrackDto>> GetByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TrackDto>>([.. ids.Where(_paths.ContainsKey).Select(Dto)]);

        public Task<TrackDto?> GetAsync(long id, CancellationToken ct = default) =>
            Task.FromResult(_paths.ContainsKey(id) ? Dto(id) : null);

        public Task<TrackDto?> GetByPathAsync(string path, CancellationToken ct = default)
        {
            foreach ((long id, string known) in _paths)
            {
                if (string.Equals(known, path, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult<TrackDto?>(Dto(id));
                }
            }

            return Task.FromResult<TrackDto?>(null);
        }

        private TrackDto Dto(long id) => new(
            id, 1, _paths[id], "Track " + id, [], 1, "Album", "Artist", (int)id, 1, 2020, 2000,
            "flac", null, Rate, 2, 16, 1, 0, null, null, null, null, null, 0, null, 0, null, false);

        public Task<IReadOnlyList<TrackDto>> ListAsync(TrackQuery query, CancellationToken ct = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TrackDto> StreamAsync(TrackQuery query, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> CountAsync(TrackQuery query, CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpsertBatchAsync(IReadOnlyList<ScannedTrack> tracks, CancellationToken ct = default) => throw new NotSupportedException();

        public Task MarkMissingAsync(IReadOnlyList<long> ids, bool missing, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> CountMissingAsync(long missingBefore, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<int> PurgeMissingAsync(long missingBefore, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<TrackFileStamp>> SnapshotAsync(long folderId, CancellationToken ct = default) => throw new NotSupportedException();

        public Task UpdateTagsAsync(long id, TagEdit edit, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> SetRatingAsync(long id, int? rating, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubQueueStore : IQueueStateRepository
    {
        public QueueState? Saved { get; private set; }

        public Task<QueueState?> LoadAsync(CancellationToken ct = default) => Task.FromResult(Saved);

        public Task SaveAsync(QueueState state, CancellationToken ct = default)
        {
            Saved = state;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            Saved = null;
            return Task.CompletedTask;
        }
    }

    private sealed class StubHistory : IPlayHistoryRepository
    {
        public Task<bool> RecordAsync(PlayEvent playEvent, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class StubSettings : ISettingsStore
    {
        public event EventHandler<string>? Changed;

        public T GetValue<T>(string key, T defaultValue) => defaultValue;

        public void SetValue<T>(string key, T value) => Changed?.Invoke(this, key);

        public bool Contains(string key) => false;

        public IReadOnlyList<string> KeysStartingWith(string prefix) => [];

        public void Flush()
        {
        }

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
