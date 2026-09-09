using Tunqio.Core.Library;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>
/// E3-S3: an <see cref="IncrementalList{T}"/> over the real repositories walks every keyset page and sees each
/// row exactly once, in the order <c>StreamAsync</c> produces, for every sort and direction.
/// </summary>
public class IncrementalListRepositoryTests : IAsyncLifetime
{
    private LibrarySeed _seed = null!;

    public async Task InitializeAsync() => _seed = await LibrarySeed.CreateAsync();

    public Task DisposeAsync()
    {
        _seed.Dispose();
        return Task.CompletedTask;
    }

    public static TheoryData<TrackSort, bool> Sorts()
    {
        var data = new TheoryData<TrackSort, bool>();
        foreach (TrackSort sort in Enum.GetValues<TrackSort>())
        {
            data.Add(sort, false);
            data.Add(sort, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Sorts))]
    public async Task Tracks_arrive_page_by_page_in_stream_order(TrackSort sort, bool descending)
    {
        var query = new TrackQuery(sort, descending, PageSize: 7, IncludeMissing: true);
        IncrementalList<TrackDto> list = IncrementalList.Tracks(_seed.Tracks, query);
        List<long> streamed = await StreamIdsAsync(query);

        int pages = 0;
        while (list.HasMore)
        {
            int added = await list.LoadMoreAsync();
            pages++;
            if (list.HasMore)
            {
                added.Should().Be(7, "every page but the last is full");
            }
        }

        list.Select(t => t.Id).Should().Equal(streamed);
        list.Select(t => t.Id).Should().OnlyHaveUniqueItems();
        pages.Should().Be((streamed.Count + 7) / 7 + (streamed.Count % 7 == 0 ? 1 : 0), "an exact multiple needs one empty page to prove the end");
        list.PageSize.Should().Be(7);
    }

    [Fact]
    public async Task The_query_cap_limits_the_list_like_the_stream()
    {
        var query = new TrackQuery(TrackSort.PlayCount, Descending: true, PageSize: 4, Take: 10);
        IncrementalList<TrackDto> list = IncrementalList.Tracks(_seed.Tracks, query);
        List<long> streamed = await StreamIdsAsync(query);

        await list.LoadAllAsync();

        list.Count.Should().Be(10);
        list.Select(t => t.Id).Should().Equal(streamed);
        list.Take.Should().Be(10);
    }

    [Fact]
    public async Task Albums_and_artists_page_the_same_way()
    {
        IncrementalList<AlbumDto> albums = IncrementalList.Albums(_seed.Service.Albums, new AlbumQuery(AlbumSort.Artist, PageSize: 3));
        IncrementalList<ArtistDto> artists = IncrementalList.Artists(_seed.Service.Artists, new ArtistQuery(PageSize: 5));

        await albums.LoadAllAsync();
        await artists.LoadAllAsync();

        albums.Select(a => a.Id).Should().Equal((await _seed.Service.Albums.ListAsync(new AlbumQuery(AlbumSort.Artist, PageSize: 1000))).Select(a => a.Id));
        artists.Select(a => a.Id).Should().Equal((await _seed.Service.Artists.ListAsync(new ArtistQuery(PageSize: 1000))).Select(a => a.Id));
        albums.Count.Should().Be(await _seed.Service.Albums.CountAsync(new AlbumQuery()));
        artists.Count.Should().Be(await _seed.Service.Artists.CountAsync(new ArtistQuery()));
    }

    private async Task<List<long>> StreamIdsAsync(TrackQuery query)
    {
        var ids = new List<long>();
        await foreach (TrackDto track in _seed.Tracks.StreamAsync(query))
        {
            ids.Add(track.Id);
        }

        return ids;
    }

    [Fact]
    public async Task Reset_then_reload_starts_from_the_top()
    {
        IncrementalList<TrackDto> list = IncrementalList.Tracks(_seed.Tracks, new TrackQuery(PageSize: 5));
        await list.LoadMoreAsync();
        await list.LoadMoreAsync();
        long first = list[0].Id;

        list.Reset();
        await list.LoadMoreAsync();

        list.Count.Should().Be(5);
        list[0].Id.Should().Be(first);
    }
}
