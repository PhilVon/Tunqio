using System.Collections.ObjectModel;
using System.ComponentModel;

namespace Tunqio.Core.Library;

/// <summary>Loads the page that follows <paramref name="after"/> (<c>null</c> for the first page).</summary>
public delegate Task<IReadOnlyList<T>> PageLoader<T>(T? after, CancellationToken ct);

/// <summary>The row-type-independent face of <see cref="IncrementalList{T}"/>, for controls that only drive loading.</summary>
public interface IIncrementalList
{
    int Count { get; }

    int PageSize { get; }

    bool HasMore { get; }

    bool IsLoading { get; }

    Exception? LastError { get; }

    Task<int> LoadMoreAsync(CancellationToken ct = default);

    void Reset();
}

/// <summary>
/// The list behind every virtualised library view (E3-S3, docs/ui-screens-and-flows.md): rows arrive a keyset
/// page at a time through a <see cref="PageLoader{T}"/> and are appended as they come, so a 100k-row view
/// starts with one page and grows only as far as the user scrolls. One load is in flight at a time (a second
/// <see cref="LoadMoreAsync"/> while one runs waits for it and reports nothing added); a page shorter than
/// <see cref="PageSize"/>, an empty page or the <see cref="Take"/> cap ends the list. <see cref="Reset"/>
/// empties it and makes any page still in flight land nowhere.
/// <para>
/// Not thread-safe by design: the view owns it, calls it on its UI thread and receives the change
/// notifications there because the await inside <see cref="LoadMoreAsync"/> resumes on the caller's context.
/// The loader itself always runs on the thread pool, so a call never completes synchronously: Microsoft.Data.Sqlite
/// finishes its async methods inline, and a page that landed inside the ListView's measure pass re-entered the
/// list's own load request until the whole 100k table was in memory and the window had still not painted.
/// Items are appended one <see cref="NotifyCollectionChangedAction.Add"/> at a time, which is what the WinUI
/// list controls accept (they reject range notifications).
/// </para>
/// </summary>
public class IncrementalList<T> : ObservableCollection<T>, IIncrementalList
{
    private readonly PageLoader<T> _loader;
    private Task<int>? _inFlight;
    private int _generation;
    private bool _hasMore = true;
    private bool _isLoading;
    private Exception? _lastError;

    /// <param name="pageSize">The number of rows the loader returns for a full page; a shorter page ends the list.</param>
    /// <param name="take">Hard cap on rows across all pages (the Recent/Most played views use 500); <c>null</c> for none.</param>
    public IncrementalList(PageLoader<T> loader, int pageSize, int? take = null)
    {
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        if (take is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(take));
        }

        _loader = loader;
        PageSize = pageSize;
        Take = take;
    }

    public int PageSize { get; }

    public int? Take { get; }

    /// <summary>False once the loader has shown there is nothing after the last row (or the cap was reached).</summary>
    public bool HasMore
    {
        get => _hasMore;
        private set => Set(ref _hasMore, value, nameof(HasMore));
    }

    /// <summary>True while a page is being loaded.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value, nameof(IsLoading));
    }

    /// <summary>The exception of the last failed load; cleared by the next successful one. The list stays loadable after a failure.</summary>
    public Exception? LastError
    {
        get => _lastError;
        private set => Set(ref _lastError, value, nameof(LastError));
    }

    /// <summary>
    /// Appends the next page and returns how many rows were added: 0 when the list is complete, when another
    /// load was already running (that load's rows count for its own caller), or when <see cref="Reset"/> ran
    /// meanwhile. A loader failure propagates after the list is put back in a loadable state.
    /// </summary>
    public Task<int> LoadMoreAsync(CancellationToken ct = default)
    {
        if (!HasMore)
        {
            return Task.FromResult(0);
        }

        if (_inFlight is { IsCompleted: false } running)
        {
            return WaitForAsync(running, ct);
        }

        // Without a synchronization context (tests) the load can finish on a pool thread before this
        // assignment runs, so a completed task is left in place and treated as "nothing in flight" above.
        Task<int> load = LoadPageAsync(ct);
        _inFlight = load;
        return load;
    }

    /// <summary>Loads every remaining page (tests, exports, the fixed-size Recent views).</summary>
    public async Task LoadAllAsync(CancellationToken ct = default)
    {
        while (HasMore)
        {
            await LoadMoreAsync(ct);
        }
    }

    /// <summary>Empties the list and forgets any page still loading; the next <see cref="LoadMoreAsync"/> starts from the top.</summary>
    public void Reset()
    {
        _generation++;
        _inFlight = null;
        IsLoading = false;
        LastError = null;
        HasMore = true;
        Clear();
    }

    private static async Task<int> WaitForAsync(Task<int> running, CancellationToken ct)
    {
        await running.WaitAsync(ct);
        return 0;
    }

    private async Task<int> LoadPageAsync(CancellationToken ct)
    {
        int generation = _generation;
        IsLoading = true;
        T? after = Count > 0 ? this[Count - 1] : default;
        IReadOnlyList<T> page;
        try
        {
            page = await Task.Run(() => _loader(after, ct), ct);
        }
        catch (Exception e)
        {
            if (generation == _generation)
            {
                IsLoading = false;
                LastError = e;
            }

            throw;
        }

        if (generation != _generation)
        {
            return 0; // Reset ran while the page was loading; it belongs to a query that no longer exists.
        }

        int room = Take is { } cap ? Math.Max(0, cap - Count) : int.MaxValue;
        int added = Math.Min(page.Count, room);
        for (int i = 0; i < added; i++)
        {
            Add(page[i]); // a view reacting to the add may ask for more; _inFlight is still set, so it waits on this load
        }

        HasMore = page.Count >= PageSize && (Take is not { } limit || Count < limit);
        LastError = null;
        IsLoading = false;
        return added;
    }

    private void Set<TValue>(ref TValue field, TValue value, string propertyName)
    {
        if (!EqualityComparer<TValue>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(new PropertyChangedEventArgs(propertyName));
        }
    }
}

/// <summary>Page loaders over the repository contracts, one per keyset-paged list method.</summary>
public static class PageLoaders
{
    /// <summary>Pages of <paramref name="query"/>; the cap in <see cref="TrackQuery.Take"/> is applied by the list, not here.</summary>
    public static PageLoader<TrackDto> Tracks(ITrackRepository tracks, TrackQuery query)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(query);
        return (after, ct) => tracks.ListAsync(query with { After = after is null ? null : query.CursorAfter(after) }, ct);
    }

    public static PageLoader<AlbumDto> Albums(IAlbumRepository albums, AlbumQuery query)
    {
        ArgumentNullException.ThrowIfNull(albums);
        ArgumentNullException.ThrowIfNull(query);
        return (after, ct) => albums.ListAsync(query with { After = after is null ? null : query.CursorAfter(after) }, ct);
    }

    public static PageLoader<ArtistDto> Artists(IArtistRepository artists, ArtistQuery query)
    {
        ArgumentNullException.ThrowIfNull(artists);
        ArgumentNullException.ThrowIfNull(query);
        return (after, ct) => artists.ListAsync(query with { After = after is null ? null : ArtistQuery.CursorAfter(after) }, ct);
    }
}

/// <summary>Convenience constructors that take the page size and cap from the query.</summary>
public static class IncrementalList
{
    public static IncrementalList<TrackDto> Tracks(ITrackRepository tracks, TrackQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new IncrementalList<TrackDto>(PageLoaders.Tracks(tracks, query), query.PageSize, query.Take);
    }

    public static IncrementalList<AlbumDto> Albums(IAlbumRepository albums, AlbumQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new IncrementalList<AlbumDto>(PageLoaders.Albums(albums, query), query.PageSize);
    }

    public static IncrementalList<ArtistDto> Artists(IArtistRepository artists, ArtistQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new IncrementalList<ArtistDto>(PageLoaders.Artists(artists, query), query.PageSize);
    }
}
