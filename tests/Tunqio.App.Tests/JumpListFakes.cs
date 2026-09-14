using Tunqio.App.JumpLists;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

/// <summary>Playlists in memory for the jump list and the activation target (E7-S5): pinned first, then by name, as the repository lists them.</summary>
internal sealed class FakePlaylists : IPlaylistRepository
{
    private readonly List<Entry> _lists = [];
    private long _nextId = 1;

    public event EventHandler<long>? Changed;

    public long Add(string name, bool pinned, params TrackDto[] tracks)
    {
        long id = _nextId++;
        _lists.Add(new Entry(id, name, pinned, [.. tracks]));
        return id;
    }

    public Task<IReadOnlyList<PlaylistDto>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PlaylistDto>>(
            [.. _lists.OrderByDescending(l => l.Pinned).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Id).Select(Dto)]);

    public Task<PlaylistDetailDto?> GetDetailAsync(long id, CancellationToken ct = default)
    {
        Entry? found = _lists.FirstOrDefault(l => l.Id == id);
        return Task.FromResult(found is null ? null : new PlaylistDetailDto(Dto(found), [.. found.Tracks]));
    }

    public Task<bool> SetPinnedAsync(long id, bool pinned, CancellationToken ct = default)
    {
        Entry? found = _lists.FirstOrDefault(l => l.Id == id);
        if (found is null)
        {
            return Task.FromResult(false);
        }

        if (found.Pinned != pinned)
        {
            found.Pinned = pinned;
            Changed?.Invoke(this, id);
        }

        return Task.FromResult(true);
    }

    public Task DeleteAsync(long id, CancellationToken ct = default)
    {
        if (_lists.RemoveAll(l => l.Id == id) > 0)
        {
            Changed?.Invoke(this, id);
        }

        return Task.CompletedTask;
    }

    public Task RenameAsync(long id, string name, CancellationToken ct = default)
    {
        Entry found = _lists.Single(l => l.Id == id);
        found.Name = name.Trim();
        Changed?.Invoke(this, id);
        return Task.CompletedTask;
    }

    public Task<PlaylistDto> CreateAsync(string name, CancellationToken ct = default) => throw new NotSupportedException();

    public Task AddTracksAsync(long id, IReadOnlyList<long> trackIds, CancellationToken ct = default) => throw new NotSupportedException();

    public Task RemoveAtAsync(long id, IReadOnlyList<int> positions, CancellationToken ct = default) => throw new NotSupportedException();

    public Task MoveAsync(long id, int fromPosition, int toPosition, CancellationToken ct = default) => throw new NotSupportedException();

    public Task ReplaceTracksAsync(long id, IReadOnlyList<long> trackIds, CancellationToken ct = default) => throw new NotSupportedException();

    private static PlaylistDto Dto(Entry l) => new(l.Id, l.Name, 0, 0, l.Pinned, l.Tracks.Count, l.Tracks.Sum(t => (long)t.DurationMs));

    private sealed class Entry(long id, string name, bool pinned, List<TrackDto> tracks)
    {
        public long Id { get; } = id;

        public string Name { get; set; } = name;

        public bool Pinned { get; set; } = pinned;

        public List<TrackDto> Tracks { get; } = tracks;
    }
}

/// <summary>A jump list that keeps every write, and can be told to fail.</summary>
internal sealed class RecordingJumpList : IJumpList
{
    public List<IReadOnlyList<JumpListEntry>> Writes { get; } = [];

    public Exception? FailWith { get; set; }

    public IReadOnlyList<JumpListEntry> Last => Writes[^1];

    public Task WriteAsync(IReadOnlyList<JumpListEntry> entries, CancellationToken ct)
    {
        if (FailWith is { } failure)
        {
            return Task.FromException(failure);
        }

        Writes.Add([.. entries]);
        return Task.CompletedTask;
    }
}
