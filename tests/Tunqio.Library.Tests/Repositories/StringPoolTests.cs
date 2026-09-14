using Tunqio.Core.Library;
using Tunqio.Library.Repositories;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>E3-S3 memory: rows read through the repository share their repeated strings and artist credits.</summary>
public class StringPoolTests
{
    [Fact]
    public async Task Rows_from_different_pages_share_album_codec_and_artist_instances_Async()
    {
        using LibrarySeed seed = await LibrarySeed.CreateAsync();
        var query = new TrackQuery(TrackSort.Album, PageSize: 3);
        IReadOnlyList<TrackDto> first = await seed.Tracks.ListAsync(query);
        IReadOnlyList<TrackDto> second = await seed.Tracks.ListAsync(query with { After = query.CursorAfter(first[^1]) });

        TrackDto a = first[0];
        TrackDto b = second.First(t => t.AlbumId == a.AlbumId);
        ReferenceEquals(a.AlbumTitle, b.AlbumTitle).Should().BeTrue("the album title is one instance across pages");
        ReferenceEquals(a.AlbumArtist, b.AlbumArtist).Should().BeTrue();
        ReferenceEquals(a.Codec, b.Codec).Should().BeTrue();
        ReferenceEquals(a.Artists[0], b.Artists[0]).Should().BeTrue("the credit object itself is shared, not just its name");
        ReferenceEquals(a.Path, b.Path).Should().BeFalse("paths are unique and not pooled");
    }

    [Fact]
    public void The_pool_is_bounded()
    {
        var pool = new StringPool();
        string first = pool.Share(new string("first".ToCharArray()));
        ReferenceEquals(pool.Share(new string("first".ToCharArray())), first).Should().BeTrue("a second equal value gets the pooled instance");
        for (int i = 0; i < StringPool.Capacity; i++)
        {
            pool.Share("s" + i);
        }

        ReferenceEquals(pool.Share(new string("first".ToCharArray())), first).Should().BeFalse("the pool restarted once full");
        pool.ShareOrNull(null).Should().BeNull();
        pool.Share(string.Empty).Should().BeSameAs(string.Empty);
    }
}
