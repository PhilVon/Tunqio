using Tunqio.Core.Library;
using Tunqio.Library.Repositories;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>E3-S2: albums, artists, genres and folders over the seeded library, including their keyset paging.</summary>
public sealed class CatalogueRepositoryTests : IAsyncLifetime
{
    private LibrarySeed _seed = null!;
    private IReadOnlyList<TrackDto> _tracks = null!;

    public async Task InitializeAsync()
    {
        _seed = await LibrarySeed.CreateAsync();
        var all = new List<TrackDto>();
        await foreach (TrackDto t in _seed.Tracks.StreamAsync(new TrackQuery(IncludeMissing: true)))
        {
            all.Add(t);
        }

        _tracks = all;
    }

    public Task DisposeAsync()
    {
        _seed.Dispose();
        return Task.CompletedTask;
    }

    public static TheoryData<AlbumSort, bool> AlbumSorts()
    {
        var data = new TheoryData<AlbumSort, bool>();
        foreach (AlbumSort sort in Enum.GetValues<AlbumSort>())
        {
            data.Add(sort, false);
            data.Add(sort, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AlbumSorts))]
    public async Task Albums_page_each_row_exactly_once_in_sort_order_Async(AlbumSort sort, bool descending)
    {
        var query = new AlbumQuery(sort, descending, PageSize: 3);
        var paged = new List<AlbumDto>();
        while (true)
        {
            IReadOnlyList<AlbumDto> page = await _seed.Service.Albums.ListAsync(query);
            paged.AddRange(page);
            if (page.Count < query.PageSize)
            {
                break;
            }

            query = query with { After = query.CursorAfter(page[^1]) };
        }

        paged.Select(a => a.Id).Should().OnlyHaveUniqueItems();
        paged.Count.Should().Be(await _seed.Service.Albums.CountAsync(new AlbumQuery()));
        IEnumerable<AlbumDto> expected = paged.OrderBy(a => a, new AlbumComparer(new AlbumQuery(sort)));
        if (descending)
        {
            expected = expected.Reverse();
        }

        paged.Select(a => a.Id).Should().Equal(expected.Select(a => a.Id));
        HashSet<long?> presentAlbums = _tracks.Where(t => !t.Missing).Select(t => t.AlbumId).ToHashSet();
        paged.Select(a => (long?)a.Id).Should().BeEquivalentTo(presentAlbums.Where(id => id is not null), "an album with no present track is hidden");
    }

    [Fact]
    public async Task Album_aggregates_and_filters_describe_present_tracks_Async()
    {
        TrackDto sample = _tracks.First(t => t.AlbumId is not null && !t.Missing);
        AlbumDto album = (await _seed.Service.Albums.GetAsync(sample.AlbumId!.Value))!;
        List<TrackDto> members = _tracks.Where(t => t.AlbumId == album.Id && !t.Missing).ToList();
        album.TrackCount.Should().Be(members.Count);
        album.TotalDurationMs.Should().Be(members.Sum(t => (long)t.DurationMs));
        album.AddedAt.Should().Be(members.Max(t => t.AddedAt));
        album.LastPlayedAt.Should().Be(members.Max(t => t.LastPlayedAt));
        album.Title.Should().Be(sample.AlbumTitle);
        album.AlbumArtist.Should().Be(sample.AlbumArtist);

        (await _seed.Service.Albums.ListAsync(new AlbumQuery(ArtistId: album.AlbumArtistId))).Should().Contain(a => a.Id == album.Id);
        (await _seed.Service.Albums.ListAsync(new AlbumQuery(Codec: members[0].Codec.ToUpperInvariant()))).Should().Contain(a => a.Id == album.Id, "format chips are case-insensitive");
        (await _seed.Service.Albums.ListAsync(new AlbumQuery(Codec: "nope"))).Should().BeEmpty();
        if (album.Year is { } year)
        {
            (await _seed.Service.Albums.ListAsync(new AlbumQuery(Decade: year))).Should().Contain(a => a.Id == album.Id, "the decade filter accepts any year in the decade");
            (await _seed.Service.Albums.ListAsync(new AlbumQuery(Decade: year - year % 10 + 10))).Should().NotContain(a => a.Id == album.Id);
        }

        (await _seed.Service.Albums.ListAsync(new AlbumQuery(Text: album.Title[..3].ToLowerInvariant()))).Should().Contain(a => a.Id == album.Id);
        GenreDto genre = (await _seed.Service.Genres.ListAsync()).First(g => g.TrackCount > 0);
        (await _seed.Service.Albums.CountAsync(new AlbumQuery(GenreId: genre.Id))).Should().BeGreaterThan(0);
        (await _seed.Service.Albums.GetAsync(999_999)).Should().BeNull();
    }

    [Fact]
    public async Task Album_detail_lists_tracks_in_disc_and_track_order_with_the_albums_genres_Async()
    {
        long multiDisc = _tracks.Where(t => t.DiscNo == 2).Select(t => t.AlbumId!.Value).First();
        AlbumDetailDto detail = (await _seed.Service.Albums.GetDetailAsync(multiDisc))!;
        detail.Album.DiscCount.Should().Be(2);
        detail.Tracks.Should().HaveCount(detail.Album.TrackCount);
        detail.Tracks.Select(t => (t.DiscNo ?? 0, t.TrackNo ?? 0)).Should().BeInAscendingOrder();
        detail.Tracks.Should().OnlyContain(t => !t.Missing);
        detail.Genres.Should().NotBeEmpty().And.BeInAscendingOrder(SortKeys.NoCase);
        (await _seed.Service.Albums.GetDetailAsync(999_999)).Should().BeNull();
    }

    [Fact]
    public async Task Artists_list_pages_by_sort_name_and_counts_present_work_Async()
    {
        var query = new ArtistQuery(PageSize: 4);
        var paged = new List<ArtistDto>();
        while (true)
        {
            IReadOnlyList<ArtistDto> page = await _seed.Service.Artists.ListAsync(query);
            paged.AddRange(page);
            if (page.Count < query.PageSize)
            {
                break;
            }

            query = query with { After = ArtistQuery.CursorAfter(page[^1]) };
        }

        paged.Select(a => a.Id).Should().OnlyHaveUniqueItems();
        paged.Count.Should().Be(await _seed.Service.Artists.CountAsync(new ArtistQuery()));
        paged.Select(a => a.SortName).Should().BeInAscendingOrder(SortKeys.NoCase);
        paged.Should().Contain(a => a.Name == "The Field Notes" && a.SortName == "Field Notes, The");
        paged.Should().Contain(a => a.Name == "Various Artists" && a.TrackCount == 0 && a.AlbumCount >= 1, "an album-only artist is listed for its albums");

        ArtistDto artist = paged.First(a => a.TrackCount > 0);
        artist.TrackCount.Should().Be(_tracks.Count(t => !t.Missing && t.Artists.Any(x => x.Id == artist.Id)));
        (await _seed.Service.Artists.ListAsync(new ArtistQuery(Text: artist.Name[1..4]))).Should().Contain(a => a.Id == artist.Id);
        (await _seed.Service.Artists.CountAsync(new ArtistQuery(Text: "zzzz-nobody"))).Should().Be(0);
    }

    [Fact]
    public async Task Artist_detail_separates_own_albums_from_appearances_Async()
    {
        ArtistDto guest = (await _seed.Service.Artists.ListAsync(new ArtistQuery(Text: "Guest Star"))).Single();
        ArtistDetailDto detail = (await _seed.Service.Artists.GetDetailAsync(guest.Id))!;
        detail.Albums.Should().BeEmpty("Guest Star fronts no album");
        detail.AppearsOn.Select(a => a.Title).Should().BeEquivalentTo(["odd years"], "it is credited on Odd Years tracks");

        ArtistDto fieldNotes = (await _seed.Service.Artists.ListAsync(new ArtistQuery(Text: "Field Notes"))).Single(a => a.Name == "The Field Notes");
        ArtistDetailDto own = (await _seed.Service.Artists.GetDetailAsync(fieldNotes.Id))!;
        own.Albums.Should().Contain(a => a.Title == "odd years");
        own.AppearsOn.Should().NotContain(a => a.Title == "odd years");
        own.Albums.Count.Should().Be(fieldNotes.AlbumCount);
        (await _seed.Service.Artists.GetDetailAsync(999_999)).Should().BeNull();
    }

    [Fact]
    public async Task Genres_count_present_tracks_and_merge_case_variants_Async()
    {
        IReadOnlyList<GenreDto> genres = await _seed.Service.Genres.ListAsync();
        genres.Select(g => g.Name).Should().BeInAscendingOrder(SortKeys.NoCase);
        genres.Select(g => g.Name.ToUpperInvariant()).Should().OnlyHaveUniqueItems("'Folk' and 'folk' are one genre");
        GenreDto folk = genres.Single(g => g.Name.Equals("Folk", StringComparison.OrdinalIgnoreCase));
        folk.TrackCount.Should().Be(await _seed.Tracks.CountAsync(new TrackQuery(GenreId: folk.Id)));
        GenreDto ambient = genres.Single(g => g.Name.Equals("ambient", StringComparison.OrdinalIgnoreCase));
        ambient.TrackCount.Should().Be(await _seed.Tracks.CountAsync(new TrackQuery(GenreId: ambient.Id)));
    }

    [Fact]
    public async Task Folders_are_normalised_deduplicated_and_cascade_on_removal_Async()
    {
        ILibraryFolderRepository folders = _seed.Service.Folders;
        IReadOnlyList<LibraryFolderDto> before = await folders.ListAsync();
        before.Select(f => f.Path).Should().Equal(@"D:\Music\Extras\", @"D:\Music\Fixtures\");

        LibraryFolderDto same = await folders.AddAsync(@"D:\Music\Fixtures");
        same.Id.Should().Be(LibrarySeed.FixtureFolderId, "the same path (trailing separator or not) is one folder");
        LibraryFolderDto added = await folders.AddAsync(@"D:\Music\New\");
        added.Path.Should().Be(@"D:\Music\New\");
        added.Enabled.Should().BeTrue();

        await folders.SetEnabledAsync(added.Id, false);
        await folders.RecordScanAsync(added.Id, LibrarySeed.Now, "partial");
        LibraryFolderDto updated = (await folders.ListAsync()).Single(f => f.Id == added.Id);
        updated.Enabled.Should().BeFalse();
        updated.LastScanAt.Should().Be(LibrarySeed.Now);
        updated.LastScanStatus.Should().Be("partial");

        int extras = await _seed.Tracks.CountAsync(new TrackQuery(FolderId: LibrarySeed.ExtraFolderId, IncludeMissing: true));
        extras.Should().BeGreaterThan(0);
        await folders.RemoveAsync(LibrarySeed.ExtraFolderId);
        (await _seed.Tracks.CountAsync(new TrackQuery(FolderId: LibrarySeed.ExtraFolderId, IncludeMissing: true))).Should().Be(0, "tracks cascade with their folder");
        (await _seed.Tracks.CountAsync(new TrackQuery(Text: "Untitled Sketch"))).Should().Be(0);
        (await folders.ListAsync()).Should().NotContain(f => f.Id == LibrarySeed.ExtraFolderId);
    }

    private sealed class AlbumComparer(AlbumQuery query) : IComparer<AlbumDto>
    {
        public int Compare(AlbumDto? x, AlbumDto? y)
        {
            object[] a = query.SortKeysOf(x!);
            object[] b = query.SortKeysOf(y!);
            for (int i = 0; i < a.Length; i++)
            {
                int c = a[i] is string s ? SortKeys.NoCase.Compare(s, (string)b[i]) : ((long)a[i]).CompareTo((long)b[i]);
                if (c != 0)
                {
                    return c;
                }
            }

            return x!.Id.CompareTo(y!.Id);
        }
    }
}
