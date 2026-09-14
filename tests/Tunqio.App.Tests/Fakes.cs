using Tunqio.App.Library;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Tests;

/// <summary>Row builders with the many-argument DTOs' noise defaulted away.</summary>
internal static class Rows
{
    public static TrackDto Track(
        long id,
        string title,
        long? albumId = 1,
        string? albumTitle = "Album",
        string? albumArtist = "Artist",
        ArtistRef[]? credits = null,
        int? disc = 1,
        int? trackNo = null,
        string codec = "flac",
        int? bitDepth = 16,
        int? sampleRate = 44100,
        int? bitrateKbps = null,
        int durationMs = 240_000,
        long addedAt = 0,
        long? lastPlayedAt = null,
        int playCount = 0,
        long folderId = 1,
        bool missing = false,
        int? year = 2001,
        string? artHash = null,
        string? path = null) =>
        new(
            Id: id,
            FolderId: folderId,
            Path: path ?? $@"D:\Music\{id}.{codec}",
            Title: title,
            Artists: credits ?? [new ArtistRef(10, albumArtist ?? "Artist")],
            AlbumId: albumId,
            AlbumTitle: albumTitle,
            AlbumArtist: albumArtist,
            TrackNo: trackNo ?? (int)id,
            DiscNo: disc,
            Year: year,
            DurationMs: durationMs,
            Codec: codec,
            BitrateKbps: bitrateKbps,
            SampleRate: sampleRate,
            Channels: 2,
            BitDepth: bitDepth,
            FileSize: 1000,
            FileMtime: 0,
            Composer: null,
            Comment: null,
            ReplayGain: null,
            ArtHash: artHash,
            Mbid: null,
            AddedAt: addedAt,
            Rating: null,
            PlayCount: playCount,
            LastPlayedAt: lastPlayedAt,
            Missing: missing);

    public static AlbumDto Album(long id, string title, string? artist = "Artist", long? artistId = 10, int? year = 2001, int trackCount = 10, long addedAt = 0, long? lastPlayedAt = null) =>
        new(id, title, artistId, artist, year, null, null, trackCount, 0, addedAt, lastPlayedAt);
}

/// <summary>Orders rows the way the SQL does: by the query's sort keys (text NOCASE, numbers by value), then id.</summary>
internal sealed class KeyComparer<T> : IComparer<T>
{
    private readonly Func<T, object[]> _keys;
    private readonly Func<T, long> _id;

    public KeyComparer(Func<T, object[]> keys, Func<T, long> id)
    {
        _keys = keys;
        _id = id;
    }

    public int Compare(T? x, T? y)
    {
        object[] a = _keys(x!);
        object[] b = _keys(y!);
        for (int i = 0; i < a.Length; i++)
        {
            int c = a[i] is string sa ? SortKeys.NoCase.Compare(sa, (string)b[i]) : ((long)a[i]).CompareTo((long)b[i]);
            if (c != 0)
            {
                return c;
            }
        }

        return _id(x!).CompareTo(_id(y!));
    }
}

internal sealed class FakeTrackRepository : ITrackRepository
{
    public List<TrackDto> Rows { get; } = [];

    public List<TrackQuery> Queries { get; } = [];

    public Task<TrackDto?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Rows.FirstOrDefault(t => t.Id == id));

    /// <summary>In the order asked for, unknown ids skipped — the contract, and what a purged track looks like.</summary>
    public Task<IReadOnlyList<TrackDto>> GetByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TrackDto>>(
            [.. ids.Select(id => Rows.FirstOrDefault(t => t.Id == id)).OfType<TrackDto>()]);

    public Task<TrackDto?> GetByPathAsync(string path, CancellationToken ct = default) =>
        Task.FromResult(Rows.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<TrackDto>> ListAsync(TrackQuery query, CancellationToken ct = default)
    {
        Queries.Add(query);
        IEnumerable<TrackDto> rows = Rows.Where(t => query.IncludeMissing || !t.Missing);
        if (query.PlayedOnly)
        {
            rows = rows.Where(t => t.LastPlayedAt is not null);
        }

        if (query.AlbumId is { } album)
        {
            rows = rows.Where(t => t.AlbumId == album);
        }

        if (query.ArtistId is { } artist)
        {
            rows = rows.Where(t => t.Artists.Any(a => a.Id == artist));
        }

        if (query.FolderId is { } folder)
        {
            rows = rows.Where(t => t.FolderId == folder);
        }

        if (query.Text is { } text)
        {
            rows = rows.Where(t => t.Title.Contains(text, StringComparison.OrdinalIgnoreCase)
                || (t.AlbumTitle ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase)
                || t.Artists.Any(a => a.Name.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }

        List<TrackDto> ordered = rows.OrderBy(t => t, new KeyComparer<TrackDto>(query.SortKeysOf, t => t.Id)).ToList();
        if (query.Descending)
        {
            ordered.Reverse();
        }

        if (query.After is { } after)
        {
            ordered = ordered.SkipWhile(t => t.Id != after.Id).Skip(1).ToList();
        }

        return Task.FromResult<IReadOnlyList<TrackDto>>(ordered.Take(query.PageSize).ToList());
    }

    public async IAsyncEnumerable<TrackDto> StreamAsync(TrackQuery query, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        TrackDto? last = null;
        while (true)
        {
            IReadOnlyList<TrackDto> page = await ListAsync(query with { After = last is null ? null : query.CursorAfter(last) }, ct);
            foreach (TrackDto t in page)
            {
                yield return t;
            }

            if (page.Count < query.PageSize)
            {
                yield break;
            }

            last = page[^1];
        }
    }

    public Task<int> CountAsync(TrackQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task UpsertBatchAsync(IReadOnlyList<ScannedTrack> tracks, CancellationToken ct = default) => throw new NotSupportedException();

    public Task MarkMissingAsync(IReadOnlyList<long> ids, bool missing, CancellationToken ct = default) => throw new NotSupportedException();

    /// <summary>What <see cref="PurgeMissingAsync"/> reports (and <see cref="CountMissingAsync"/> before it); the settings view model shows both.</summary>
    public int MissingOlderThanCutoff { get; set; }

    public List<long> PurgeCutoffs { get; } = [];

    public Task<int> CountMissingAsync(long missingBefore, CancellationToken ct = default) => Task.FromResult(MissingOlderThanCutoff);

    public Task<int> PurgeMissingAsync(long missingBefore, CancellationToken ct = default)
    {
        PurgeCutoffs.Add(missingBefore);
        int purged = MissingOlderThanCutoff;
        MissingOlderThanCutoff = 0;
        return Task.FromResult(purged);
    }

    public Task<IReadOnlyList<TrackFileStamp>> SnapshotAsync(long folderId, CancellationToken ct = default) => throw new NotSupportedException();

    /// <summary>Every rating written, in order; the row is patched so a later read sees it, as the real repository's would.</summary>
    public List<(long Id, int? Rating)> Ratings { get; } = [];

    public Task<bool> SetRatingAsync(long id, int? rating, CancellationToken ct = default)
    {
        Ratings.Add((id, rating));
        int index = Rows.FindIndex(t => t.Id == id);
        if (index < 0)
        {
            return Task.FromResult(false);
        }

        Rows[index] = Rows[index] with { Rating = rating };
        return Task.FromResult(true);
    }
}

/// <summary>An <see cref="ITrackRater"/> that records what it was asked and lets a test raise its events by hand.</summary>
internal sealed class FakeRater : ITrackRater
{
    public List<(long Id, int Stars)> Requests { get; } = [];

    /// <summary>What <see cref="RateAsync"/> answers with for the file half; null means writing to files is off.</summary>
    public TagWriteOutcome? FileWrite { get; set; }

    public event EventHandler<RatingChange>? Changed;

    public event EventHandler<RatingChange>? FileWriteCompleted;

    public Task<RatingChange> RateAsync(long trackId, int stars, CancellationToken ct = default)
    {
        Requests.Add((trackId, stars));
        var change = new RatingChange(trackId, $@"D:\Music\{trackId}.flac", Tunqio.Core.Library.Ratings.FromStars(stars), FileWrite);
        Changed?.Invoke(this, change);
        return Task.FromResult(change);
    }

    public Task<int> FlushDeferredAsync(CancellationToken ct = default) => Task.FromResult(0);

    /// <summary>What another surface (a shortcut, another page) did: the event without a request here.</summary>
    public void RaiseChanged(long trackId, int? rating) => Changed?.Invoke(this, new RatingChange(trackId, $@"D:\Music\{trackId}.flac", rating));

    public void RaiseFileWrite(RatingChange change) => FileWriteCompleted?.Invoke(this, change);
}

internal sealed class FakeAlbumRepository : IAlbumRepository
{
    public List<AlbumDto> Rows { get; } = [];

    /// <summary>Tracks per album id, in disc/track order, for <see cref="GetDetailAsync"/>.</summary>
    public Dictionary<long, List<TrackDto>> Tracks { get; } = [];

    public Dictionary<long, List<string>> Genres { get; } = [];

    public AlbumFacets Facets { get; set; } = AlbumFacets.Empty;

    public List<AlbumQuery> Queries { get; } = [];

    public Task<AlbumDto?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Rows.FirstOrDefault(a => a.Id == id));

    public Task<AlbumDetailDto?> GetDetailAsync(long id, CancellationToken ct = default)
    {
        AlbumDto? album = Rows.FirstOrDefault(a => a.Id == id);
        return Task.FromResult(album is null ? null : new AlbumDetailDto(album, Tracks.GetValueOrDefault(id) ?? [], Genres.GetValueOrDefault(id) ?? []));
    }

    public Task<IReadOnlyList<AlbumDto>> ListAsync(AlbumQuery query, CancellationToken ct = default)
    {
        Queries.Add(query);
        IEnumerable<AlbumDto> rows = Rows;
        if (query.ArtistId is { } artist)
        {
            rows = rows.Where(a => a.AlbumArtistId == artist);
        }

        if (query.Decade is { } decade)
        {
            rows = rows.Where(a => a.Year is { } y && y / 10 * 10 == decade);
        }

        List<AlbumDto> ordered = rows.OrderBy(a => a, new KeyComparer<AlbumDto>(query.SortKeysOf, a => a.Id)).ToList();
        if (query.Descending)
        {
            ordered.Reverse();
        }

        if (query.After is { } after)
        {
            ordered = ordered.SkipWhile(a => a.Id != after.Id).Skip(1).ToList();
        }

        return Task.FromResult<IReadOnlyList<AlbumDto>>(ordered.Take(query.PageSize).ToList());
    }

    public Task<int> CountAsync(AlbumQuery query, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<AlbumFacets> ListFacetsAsync(CancellationToken ct = default) => Task.FromResult(Facets);
}

internal sealed class FakeArtistRepository : IArtistRepository
{
    public List<ArtistDto> Rows { get; } = [];

    public Dictionary<long, ArtistDetailDto> Details { get; } = [];

    public int Pages { get; private set; }

    public Task<ArtistDto?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Rows.FirstOrDefault(a => a.Id == id));

    public Task<ArtistDetailDto?> GetDetailAsync(long id, CancellationToken ct = default) => Task.FromResult(Details.GetValueOrDefault(id));

    public Task<IReadOnlyList<ArtistDto>> ListAsync(ArtistQuery query, CancellationToken ct = default)
    {
        Pages++;
        List<ArtistDto> ordered = Rows.OrderBy(a => a.SortName, SortKeys.NoCase).ThenBy(a => a.Id).ToList();
        if (query.After is { } after)
        {
            ordered = ordered.SkipWhile(a => a.Id != after.Id).Skip(1).ToList();
        }

        return Task.FromResult<IReadOnlyList<ArtistDto>>(ordered.Take(query.PageSize).ToList());
    }

    public Task<int> CountAsync(ArtistQuery query, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class FakeGenreRepository : IGenreRepository
{
    public List<GenreDto> Rows { get; } = [];

    public Task<IReadOnlyList<GenreDto>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GenreDto>>(Rows);
}

internal sealed class FakeFolderRepository : ILibraryFolderRepository
{
    public List<LibraryFolderDto> Rows { get; } = [];

    public Task<IReadOnlyList<LibraryFolderDto>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LibraryFolderDto>>(Rows.ToList());

    /// <summary>Idempotent by path, like the real one; the path is stored as given (no normalisation).</summary>
    public Task<LibraryFolderDto> AddAsync(string path, CancellationToken ct = default)
    {
        LibraryFolderDto? existing = Rows.FirstOrDefault(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            existing = new LibraryFolderDto(Rows.Count == 0 ? 1 : Rows.Max(r => r.Id) + 1, path, true, null, null);
            Rows.Add(existing);
        }

        return Task.FromResult(existing);
    }

    public Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        int i = Rows.FindIndex(r => r.Id == id);
        Rows[i] = Rows[i] with { Enabled = enabled };
        return Task.CompletedTask;
    }

    public Task RemoveAsync(long id, CancellationToken ct = default)
    {
        Rows.RemoveAll(r => r.Id == id);
        return Task.CompletedTask;
    }

    public Task RecordScanAsync(long id, long scannedAt, string status, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>
/// A scanner the test drives by hand: <see cref="ScanAsync"/> records the request and waits until the test
/// <see cref="Finish"/>es it (or the token cancels), reporting progress on demand; <see cref="Complete"/> raises
/// <see cref="ScanCompleted"/> for a scan of any origin (the watcher's, say) without going through ScanAsync.
/// </summary>
internal sealed class FakeScanner : ILibraryScanner
{
    private TaskCompletionSource<ScanReport>? _running;
    private IProgress<ScanProgress>? _progress;

    public List<ScanRequest> Requests { get; } = [];

    public bool IsScanning { get; set; }

    /// <summary>Thrown by the next ScanAsync call, once (the scanner's one-at-a-time refusal).</summary>
    public bool RefuseNext { get; set; }

    public event EventHandler<ScanReport>? ScanCompleted;

    public Task<ScanReport> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        if (RefuseNext)
        {
            RefuseNext = false;
            throw new InvalidOperationException("A library scan is already running.");
        }

        Requests.Add(request);
        IsScanning = true;
        _progress = progress;
        _running = new TaskCompletionSource<ScanReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => Finish(Report(ScanOutcome.Cancelled)));
#pragma warning disable VSTHRD003 // the test hands the report over; nothing here blocks on it
        return _running.Task;
#pragma warning restore VSTHRD003
    }

    public void ReportProgress(ScanProgress sample) => _progress?.Report(sample);

    /// <summary>Ends the running scan with <paramref name="report"/> and raises ScanCompleted, as the real scanner does.</summary>
    public void Finish(ScanReport report)
    {
        TaskCompletionSource<ScanReport>? running = Interlocked.Exchange(ref _running, null);
        if (running is null)
        {
            return;
        }

        IsScanning = false;
        running.TrySetResult(report);
        ScanCompleted?.Invoke(this, report);
    }

    /// <summary>A scan that did not go through this fake's ScanAsync (the watcher's) ended.</summary>
    public void Complete(ScanReport report) => ScanCompleted?.Invoke(this, report);

    public static ScanReport Report(ScanOutcome outcome = ScanOutcome.Completed, int added = 0, int updated = 0, int unchanged = 0, int failed = 0, int missing = 0, int restored = 0, IReadOnlyList<ScanFailure>? failures = null, string? error = null) =>
        new(outcome, TimeSpan.FromSeconds(1.5), added + updated + unchanged + failed, added + updated + failed, added, updated, unchanged, failed, missing, restored, 0, failures ?? [], [], error);
}

internal sealed class FakeWatcher : ILibraryWatcher
{
    public int Refreshes { get; private set; }

    public bool IsWatching { get; private set; }

    public LibraryWatcherStats Stats => LibraryWatcherStats.Empty;

    public Task StartAsync(CancellationToken ct = default)
    {
        IsWatching = true;
        return Task.CompletedTask;
    }

    public Task RefreshAsync(CancellationToken ct = default)
    {
        Refreshes++;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsWatching = false;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}

internal sealed class FakeArtCache : IArtCache
{
    public int Clears { get; private set; }

    public Task<ArtHashes> StoreAsync(EmbeddedPicture? picture, string audioPath, CancellationToken ct = default) => Task.FromResult(ArtHashes.None);

    public string? PathFor(string? hash, ArtSize size) => null;

    public Task<ArtPalette?> LoadPaletteAsync(string? hash, CancellationToken ct = default) => Task.FromResult<ArtPalette?>(null);

    public Task ClearAsync(CancellationToken ct = default)
    {
        Clears++;
        return Task.CompletedTask;
    }
}

internal sealed class FakeFolderPicker : ILibraryFolderPicker
{
    /// <summary>What the next pick returns; <c>null</c> is the user cancelling.</summary>
    public string? NextPick { get; set; }

    public int Picks { get; private set; }

    public Task<string?> PickFolderAsync(CancellationToken ct = default)
    {
        Picks++;
        return Task.FromResult(NextPick);
    }
}

/// <summary>Records every request as the session would receive it.</summary>
internal sealed class FakePlayback : IPlaybackCommands
{
    /// <summary>Value equality over the id sequence too (a record compares an array by reference).</summary>
    public sealed record Request(string Kind, long[] Ids, int StartIndex, bool Shuffle)
    {
        public bool Equals(Request? other) =>
            other is not null && Kind == other.Kind && StartIndex == other.StartIndex && Shuffle == other.Shuffle && Ids.SequenceEqual(other.Ids);

        public override int GetHashCode() => HashCode.Combine(Kind, StartIndex, Shuffle, Ids.Length);

        public override string ToString() => $"{Kind}[{string.Join(",", Ids)}] from {StartIndex}{(Shuffle ? " shuffled" : string.Empty)}";
    }

    public List<Request> Requests { get; } = [];

    public Request Last => Requests[^1];

    public Task PlayNowAsync(IReadOnlyList<long> trackIds, int startIndex = 0, bool shuffle = false, CancellationToken ct = default)
    {
        Requests.Add(new Request("play", [.. trackIds], startIndex, shuffle));
        return Task.CompletedTask;
    }

    public Task PlayNextAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default)
    {
        Requests.Add(new Request("next", [.. trackIds], 0, false));
        return Task.CompletedTask;
    }

    public Task EnqueueAsync(IReadOnlyList<long> trackIds, CancellationToken ct = default)
    {
        Requests.Add(new Request("enqueue", [.. trackIds], 0, false));
        return Task.CompletedTask;
    }
}

internal sealed class FakeNavigator : ILibraryNavigator
{
    public List<object> Opened { get; } = [];

    public void OpenAlbum(long albumId) => Opened.Add(("album", albumId));

    public void OpenArtist(long artistId) => Opened.Add(("artist", artistId));

    public void OpenTracks(TracksSpec spec) => Opened.Add(spec);

    public void OpenPlaylist(long playlistId) => Opened.Add(("playlist", playlistId));

    public void OpenPlaylists() => Opened.Add("playlists");
}

internal sealed class FakeRevealer : IFileRevealer
{
    public List<string> Revealed { get; } = [];

    public void Reveal(string path) => Revealed.Add(path);
}

internal sealed class FakeSettings : ISettingsStore
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

    public event EventHandler<string>? Changed;

    public T GetValue<T>(string key, T defaultValue) => _values.TryGetValue(key, out object? value) && value is T typed ? typed : defaultValue;

    public void SetValue<T>(string key, T value)
    {
        // Null removes the key, as ISettingsStore says and JsonSettingsStore does (T-157's Reset depends on it).
        if (value is null)
        {
            _values.Remove(key);
        }
        else
        {
            _values[key] = value;
        }

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

/// <summary>
/// Records every query with its token and lets the test answer it whenever it likes, cancelled or not: a slow
/// backend that never observes cancellation, which is what the view model must cope with.
/// </summary>
internal sealed class FakeSearchService : ISearchService
{
    public sealed class Call
    {
        public Call(string text, SearchLimits limits, CancellationToken token)
        {
            Text = text;
            Limits = limits;
            Token = token;
        }

        public string Text { get; }

        public SearchLimits Limits { get; }

        public CancellationToken Token { get; }

        /// <summary>Continuations run inline, so the view model has dealt with an answer by the time <see cref="Answer"/> returns.</summary>
        public TaskCompletionSource<SearchResults> Completion { get; } = new();

        public void Answer(SearchResults results) => Completion.SetResult(results);
    }

    public List<Call> Calls { get; } = [];

    /// <summary>When set, every query is answered synchronously from this.</summary>
    public Func<string, SearchLimits, SearchResults>? AnswerImmediately { get; set; }

    public int Rebuilds { get; private set; }

    public Task<SearchResults> SearchAsync(string text, SearchLimits limits, CancellationToken ct = default)
    {
        var call = new Call(text, limits, ct);
        Calls.Add(call);
        if (AnswerImmediately is { } answer)
        {
            call.Answer(answer(text, limits));
        }

#pragma warning disable VSTHRD003 // the test hands the answer over; nothing here blocks on it
        return call.Completion.Task;
#pragma warning restore VSTHRD003
    }

    public Task<int> RebuildIndexAsync(CancellationToken ct = default)
    {
        Rebuilds++;
        return Task.FromResult(0);
    }
}

/// <summary>What <see cref="Tunqio.App.Playback.AudioStartup"/> is to a shell panel, without an engine a test host can create.</summary>
/// <summary>
/// A clock the test moves by hand, whose timers fire when it is moved past their due time. There is no
/// FakeTimeProvider package here, and the alternative — waiting eight real seconds to watch a transient notice go —
/// is not a test, it is a delay.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state, dueTime == Timeout.InfiniteTimeSpan ? null : _now + dueTime);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Moves the clock, firing anything that fell due on the way. One-shot: a fired timer does not repeat.</summary>
    public void Advance(TimeSpan by)
    {
        _now += by;
        ManualTimer[] due;
        lock (_timers)
        {
            due = [.. _timers.Where(t => t.Due is { } at && at <= _now)];
            foreach (ManualTimer timer in due)
            {
                _timers.Remove(timer);
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset? due) : ITimer
    {
        public DateTimeOffset? Due { get; private set; } = due;

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = null;
            return true;
        }

        public void Dispose() => Due = null;

        public ValueTask DisposeAsync()
        {
            Due = null;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class StubSessionSource : Tunqio.App.Playback.IPlaybackSessionSource
{
    private PlaybackSession? _session;

    public PlaybackSession? Session
    {
        get => _session;
        set
        {
            _session = value;
            Started = true;
            if (value is not null)
            {
                SessionReady?.Invoke(this, value);
            }
        }
    }

    public bool Started { get; set; }

    public event EventHandler<PlaybackSession>? SessionReady;
}
