using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Data;
using Tunqio.Core.Library;
using Windows.Foundation;

namespace Tunqio.App.Controls;

/// <summary>
/// <see cref="IncrementalList{T}"/> as a WinUI items source: <see cref="ListView"/> and <see cref="GridView"/>
/// call <see cref="LoadMoreItemsAsync"/> on the UI thread as the scroll edge nears (E3-S3). The count the view
/// asks for is a hint; pages are the query's size.
/// </summary>
public sealed class IncrementalItemsSource<T> : IncrementalList<T>, ISupportIncrementalLoading
{
    public IncrementalItemsSource(PageLoader<T> loader, int pageSize, int? take = null)
        : base(loader, pageSize, take)
    {
    }

    public bool HasMoreItems => HasMore;

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count) =>
        AsyncInfo.Run(async ct => new LoadMoreItemsResult { Count = (uint)await LoadMoreAsync(ct) });
}

/// <summary>Sources over the repository contracts; page size and cap come from the query.</summary>
public static class IncrementalItemsSource
{
    public static IncrementalItemsSource<TrackDto> Tracks(ITrackRepository tracks, TrackQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new IncrementalItemsSource<TrackDto>(PageLoaders.Tracks(tracks, query), query.PageSize, query.Take);
    }

    public static IncrementalItemsSource<AlbumDto> Albums(IAlbumRepository albums, AlbumQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new IncrementalItemsSource<AlbumDto>(PageLoaders.Albums(albums, query), query.PageSize);
    }

    public static IncrementalItemsSource<ArtistDto> Artists(IArtistRepository artists, ArtistQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return new IncrementalItemsSource<ArtistDto>(PageLoaders.Artists(artists, query), query.PageSize);
    }
}
