using Tunqio.Core;
using Tunqio.Core.Library;

namespace Tunqio.Core.Tests.Playback;

/// <summary>Track rows for the session tests: ids 1..n, one album, a path per id.</summary>
internal sealed class FakeTrackRepository : ITrackRepository
{
    private readonly Dictionary<long, TrackDto> _tracks = [];

    public static FakeTrackRepository With(params long[] ids)
    {
        var repository = new FakeTrackRepository();
        foreach (long id in ids)
        {
            repository.Add(id);
        }

        return repository;
    }

    public TrackDto Add(long id, long? albumId = 1, bool missing = false, ReplayGainTags? gain = null) =>
        _tracks[id] = Track(id, albumId, missing, gain);

    public void Remove(long id) => _tracks.Remove(id);

    public static TrackDto Track(long id, long? albumId = 1, bool missing = false, ReplayGainTags? gain = null) => new(
        Id: id,
        FolderId: 1,
        Path: $@"D:\Music\{id}.flac",
        Title: $"Track {id}",
        Artists: [new ArtistRef(1, "Artist")],
        AlbumId: albumId,
        AlbumTitle: "Album",
        AlbumArtist: "Artist",
        TrackNo: (int)id,
        DiscNo: 1,
        Year: 2020,
        DurationMs: 180_000,
        Codec: "flac",
        BitrateKbps: null,
        SampleRate: 44100,
        Channels: 2,
        BitDepth: 16,
        FileSize: 1,
        FileMtime: 0,
        Composer: null,
        Comment: null,
        ReplayGain: gain,
        ArtHash: null,
        Mbid: null,
        AddedAt: 0,
        Rating: null,
        PlayCount: 0,
        LastPlayedAt: null,
        Missing: missing);

    public Task<IReadOnlyList<TrackDto>> GetByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TrackDto>>([.. ids.Where(_tracks.ContainsKey).Select(id => _tracks[id])]);

    public Task<TrackDto?> GetAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(_tracks.TryGetValue(id, out TrackDto? track) ? track : null);

    public Task<TrackDto?> GetByPathAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(_tracks.Values.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)));

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

/// <summary>An in-memory <see cref="ISettingsStore"/>; the session reads gapless, crossfade and ReplayGain from it.</summary>
internal sealed class FakeSettingsStore : ISettingsStore
{
    private readonly Dictionary<string, object?> _values = [];

    public event EventHandler<string>? Changed;

    public T GetValue<T>(string key, T defaultValue) =>
        _values.TryGetValue(key, out object? value) && value is T typed ? typed : defaultValue;

    public void SetValue<T>(string key, T value)
    {
        _values[key] = value;
        Changed?.Invoke(this, key);
    }

    public bool Contains(string key) => _values.ContainsKey(key);

    public IReadOnlyList<string> KeysStartingWith(string prefix) =>
        [.. _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))];

    public void Flush()
    {
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
