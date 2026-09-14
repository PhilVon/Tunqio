using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.Library.Repositories;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>
/// E3-S2: every <see cref="TrackQuery"/> sort and filter combination is paged through the repository and
/// compared with a LINQ oracle over the same rows, so each row appears exactly once and in the documented order;
/// plus the write paths (upsert identity, tag edits, missing flags) and the search index they maintain.
/// </summary>
public sealed class TrackRepositoryTests : IAsyncLifetime
{
    private LibrarySeed _seed = null!;
    private IReadOnlyList<TrackDto> _all = null!;
    private ILookup<long, long> _genresByTrack = null!;

    public async Task InitializeAsync()
    {
        _seed = await LibrarySeed.CreateAsync();
        _all = await AllTracksAsync(_seed);
        _genresByTrack = (await PairsAsync(_seed, "SELECT track_id, genre_id FROM track_genre")).ToLookup(p => p.Item1, p => p.Item2);
    }

    public Task DisposeAsync()
    {
        _seed.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Filters worth combining with every sort: none, each single filter, and two together.</summary>
    private static readonly string[] FilterNames =
        ["none", "album", "artist", "artist-cocredit", "genre", "folder", "text-title", "text-artist", "text-album", "missing", "played", "genre+text", "folder+album"];

    public static TheoryData<TrackSort, bool, string> SortsByFilters()
    {
        var data = new TheoryData<TrackSort, bool, string>();
        foreach (TrackSort sort in Enum.GetValues<TrackSort>())
        {
            foreach (bool descending in new[] { false, true })
            {
                foreach (string filter in FilterNames)
                {
                    data.Add(sort, descending, filter);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SortsByFilters))]
    public async Task Every_sort_and_filter_pages_each_row_exactly_once_in_oracle_order_Async(TrackSort sort, bool descending, string filter)
    {
        TrackQuery query = ApplyFilter(new TrackQuery(sort, descending, PageSize: 7), filter);
        List<long> expected = Oracle(query).Select(t => t.Id).ToList();
        expected.Should().NotBeEmpty("a filter that matches nothing proves nothing");

        var paged = new List<long>();
        TrackQuery page = query;
        int pages = 0;
        while (true)
        {
            IReadOnlyList<TrackDto> rows = await _seed.Tracks.ListAsync(page);
            rows.Count.Should().BeLessThanOrEqualTo(query.PageSize);
            paged.AddRange(rows.Select(r => r.Id));
            pages++;
            if (rows.Count < query.PageSize)
            {
                break;
            }

            page = page with { After = query.CursorAfter(rows[^1]) };
        }

        paged.Should().Equal(expected, $"pages of {query.PageSize} ({pages} pages) must reproduce the oracle order for {sort}{(descending ? " desc" : string.Empty)} / {filter}");
        paged.Should().OnlyHaveUniqueItems();
        (await _seed.Tracks.CountAsync(query)).Should().Be(expected.Count);

        var streamed = new List<long>();
        await foreach (TrackDto row in _seed.Tracks.StreamAsync(query))
        {
            streamed.Add(row.Id);
        }

        streamed.Should().Equal(expected);
    }

    [Fact]
    public async Task Stream_honours_Take_and_page_size_Async()
    {
        var query = new TrackQuery(TrackSort.Added, Descending: true, PageSize: 5, Take: 12);
        var rows = new List<TrackDto>();
        await foreach (TrackDto row in _seed.Tracks.StreamAsync(query))
        {
            rows.Add(row);
        }

        rows.Select(r => r.Id).Should().Equal(Oracle(query).Take(12).Select(t => t.Id));
    }

    [Fact]
    public async Task Sort_keys_agree_with_the_SQL_for_every_row_Async()
    {
        // The oracle only proves anything if TrackQuery.SortKeysOf reproduces the SQL key expressions: cursoring
        // from row N must land exactly on row N+1 for a page size of 1, for every sort, over the whole set.
        foreach (TrackSort sort in Enum.GetValues<TrackSort>())
        {
            var query = new TrackQuery(sort, IncludeMissing: true, PageSize: 1);
            List<TrackDto> expected = Oracle(query);
            for (int i = 0; i < expected.Count - 1; i++)
            {
                IReadOnlyList<TrackDto> next = await _seed.Tracks.ListAsync(query with { After = query.CursorAfter(expected[i]) });
                next.Should().ContainSingle().Which.Id.Should().Be(expected[i + 1].Id, $"{sort}: the row after {expected[i].Id} ({expected[i].Title})");
            }
        }
    }

    [Fact]
    public async Task A_cursor_from_another_sort_is_rejected_Async()
    {
        var byTitle = new TrackQuery(TrackSort.Title);
        PageCursor cursor = byTitle.CursorAfter(_all[0]);
        Func<Task> list = () => _seed.Tracks.ListAsync(new TrackQuery(TrackSort.Album, After: cursor));
        await list.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Get_and_GetByIds_return_detached_rows_in_request_order_Async()
    {
        TrackDto? one = await _seed.Tracks.GetAsync(1);
        one.Should().NotBeNull();
        one!.Artists.Should().NotBeEmpty();
        (await _seed.Tracks.GetAsync(999_999)).Should().BeNull();

        IReadOnlyList<TrackDto> some = await _seed.Tracks.GetByIdsAsync([3, 1, 999_999, 2, 1]);
        some.Select(t => t.Id).Should().Equal(3L, 1L, 2L, 1L); // unknown ids are skipped; duplicates and order are the caller's
        (await _seed.Tracks.GetByIdsAsync([])).Should().BeEmpty();
    }

    [Fact]
    public void Upsert_resolves_album_identity_and_artists_case_insensitively()
    {
        // "odd years"/"Odd Years" by "the field notes"/"The Field Notes" with no year are one album; the album
        // artist named in different cases is one artist (schema: UNIQUE name COLLATE NOCASE).
        TrackDto first = _all.Single(t => t.Path.EndsWith("01 first.flac", StringComparison.Ordinal));
        TrackDto second = _all.Single(t => t.Path.EndsWith("02 second.flac", StringComparison.Ordinal));
        first.AlbumId.Should().Be(second.AlbumId);
        first.AlbumArtist.Should().Be("The Field Notes");
        first.Artists.Single().Id.Should().Be(second.Artists[0].Id);
        first.Artists.Single().Name.Should().Be("The Field Notes", "the first spelling seen wins");
        second.Artists.Select(a => a.Name).Should().Equal("The Field Notes", "Guest Star");

        // A track with no album tag has no album; one with no artists has none.
        _all.Single(t => t.Title == "Untitled Sketch").AlbumId.Should().BeNull();
        _all.Single(t => t.Title == "another sketch").Artists.Should().BeEmpty();

        // The compilation with no album-artist tag was attributed to "Various Artists" by the reader rule.
        _all.Where(t => t.AlbumArtist == "Various Artists").Select(t => t.AlbumTitle).Distinct().Should().ContainSingle();
    }

    /// <summary>
    /// E6-S7 (AC-451): a rating is one column of one row, set and cleared through the repository, and the scanner's
    /// upsert leaves it alone — the rescan here changes the file's tags and the rating stays. The star scale is the
    /// caller's (<see cref="Ratings"/>); the repository takes 0..100 and refuses anything outside it.
    /// </summary>
    [Fact]
    public async Task A_rating_is_set_cleared_and_kept_across_a_rescan_Async()
    {
        TrackDto track = _all.First(t => t.Rating is null && !t.Missing);

        (await _seed.Tracks.SetRatingAsync(track.Id, 60)).Should().BeTrue();
        (await _seed.Tracks.GetAsync(track.Id))!.Rating.Should().Be(60, "three stars are 60 on the 0..100 scale");

        // The file comes back from a scan with new tags: the row takes them and keeps the rating.
        ScannedTrack rescanned = new(track.Path, track.FolderId, track.FileSize + 1, track.FileMtime + 1, track.Codec, track.DurationMs,
            "Retitled After Rating", track.Artists.Select(a => a.Name).ToArray(), AlbumTitle: track.AlbumTitle, AlbumArtist: track.AlbumArtist, Year: track.Year, TrackNo: track.TrackNo);
        await _seed.Tracks.UpsertBatchAsync([rescanned]);
        TrackDto after = (await _seed.Tracks.GetAsync(track.Id))!;
        after.Title.Should().Be("Retitled After Rating");
        after.Rating.Should().Be(60, "a rescan re-reads the file, and the rating is user data the file does not carry");

        (await _seed.Tracks.SetRatingAsync(track.Id, null)).Should().BeTrue();
        (await _seed.Tracks.GetAsync(track.Id))!.Rating.Should().BeNull("clearing is NULL, not zero: an unrated track sorts after every rated one");

        (await _seed.Tracks.SetRatingAsync(long.MaxValue, 40)).Should().BeFalse("a track the library does not have is reported, not invented");
        Func<Task> tooHigh = () => _seed.Tracks.SetRatingAsync(track.Id, 101);
        await tooHigh.Should().ThrowAsync<ArgumentOutOfRangeException>();
        Func<Task> negative = () => _seed.Tracks.SetRatingAsync(track.Id, -1);
        await negative.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Re_upserting_a_path_keeps_its_id_rating_and_play_history_but_takes_the_new_tags_Async()
    {
        TrackDto before = _all.First(t => t.Rating is not null && t.PlayCount > 0 && t.AlbumId is not null);
        ScannedTrack rescanned = new(before.Path, before.FolderId, before.FileSize + 1, before.FileMtime + 1, before.Codec, before.DurationMs + 5,
            "Retitled", ["Someone New", before.Artists[0].Name], AlbumTitle: "Reissue", AlbumArtist: "Someone New", Year: 2030, TrackNo: 9, Genres: ["Reissue Genre"]);

        await _seed.Tracks.UpsertBatchAsync([rescanned]);

        TrackDto after = (await _seed.Tracks.GetAsync(before.Id))!;
        after.Title.Should().Be("Retitled");
        after.Rating.Should().Be(before.Rating);
        after.PlayCount.Should().Be(before.PlayCount);
        after.LastPlayedAt.Should().Be(before.LastPlayedAt);
        after.AddedAt.Should().Be(before.AddedAt, "added_at is when the file first entered the library");
        after.AlbumTitle.Should().Be("Reissue");
        after.AlbumId.Should().NotBe(before.AlbumId);
        after.Artists.Select(a => a.Name).Should().Equal("Someone New", before.Artists[0].Name);
        after.FileSize.Should().Be(before.FileSize + 1);
        (await _seed.Tracks.CountAsync(new TrackQuery(IncludeMissing: true))).Should().Be(_all.Count, "an upsert of an existing path adds no row");

        (await FtsMatchesAsync("Retitled")).Should().Equal(before.Id);
        (await FtsMatchesAsync(before.Title)).Should().NotContain(before.Id, "the old title left the search index in the same transaction");
    }

    [Fact]
    public async Task Upsert_of_a_missing_track_clears_the_missing_flag_Async()
    {
        TrackDto missing = _all.Single(t => t.Id == 5);
        missing.Missing.Should().BeTrue();
        (await _seed.Tracks.GetByIdsAsync([5])).Single().Missing.Should().BeTrue("GetByIds does not hide missing rows");
        (await _seed.Tracks.CountAsync(new TrackQuery(FolderId: missing.FolderId))).Should().Be(_all.Count(t => t.FolderId == missing.FolderId && !t.Missing));

        await _seed.Tracks.UpsertBatchAsync([new ScannedTrack(missing.Path, missing.FolderId, 1, 1, missing.Codec, missing.DurationMs, missing.Title, missing.Artists.Select(a => a.Name).ToArray(), missing.AlbumTitle, missing.AlbumArtist, missing.Year, missing.TrackNo, missing.DiscNo)]);

        (await _seed.Tracks.GetAsync(5))!.Missing.Should().BeFalse();
        await _seed.Tracks.MarkMissingAsync([5], missing: true);
        (await _seed.Tracks.GetAsync(5))!.Missing.Should().BeTrue();
        await _seed.Tracks.MarkMissingAsync([], missing: false);
    }

    [Fact]
    public async Task Purge_missing_deletes_only_tracks_flagged_since_before_the_cutoff_and_their_index_rows_Async()
    {
        // The seed flagged 5, 6 and 61 at Now (the fixed clock). Purge counts from the first marking, which a
        // later scan that still misses the file does not move.
        (await _seed.Tracks.CountMissingAsync(LibrarySeed.Now)).Should().Be(0, "flagged at Now is not before Now");
        (await _seed.Tracks.CountMissingAsync(LibrarySeed.Now + 1)).Should().Be(3);
        await _seed.Tracks.MarkMissingAsync([5, 6], missing: true);
        (await ScalarAsync(_seed, "SELECT COUNT(*) FROM track WHERE missing = 1 AND missing_since = " + LibrarySeed.Now)).Should().Be(3L, "re-marking keeps the first stamp");

        // A file that returns (restored by the scanner or re-read) leaves the purge set.
        await _seed.Tracks.MarkMissingAsync([5], missing: false);
        (await ScalarAsync(_seed, "SELECT missing_since FROM track WHERE id = 5")).Should().Be(DBNull.Value);
        TrackDto six = (await _seed.Tracks.GetByIdsAsync([6])).Single();
        await _seed.Tracks.UpsertBatchAsync([new ScannedTrack(six.Path, six.FolderId, 1, 1, six.Codec, six.DurationMs, six.Title, six.Artists.Select(a => a.Name).ToArray(), six.AlbumTitle, six.AlbumArtist, six.Year, six.TrackNo, six.DiscNo)]);
        (await ScalarAsync(_seed, "SELECT missing_since FROM track WHERE id = 6")).Should().Be(DBNull.Value, "an upsert clears the stamp with the flag");
        (await _seed.Tracks.CountMissingAsync(LibrarySeed.Now + 1)).Should().Be(1);

        TrackDto gone = (await _seed.Tracks.GetByIdsAsync([61])).Single();
        (await _seed.Tracks.PurgeMissingAsync(LibrarySeed.Now)).Should().Be(0);
        (await _seed.Tracks.PurgeMissingAsync(LibrarySeed.Now + 1)).Should().Be(1);

        (await _seed.Tracks.GetAsync(61)).Should().BeNull();
        (await _seed.Tracks.GetAsync(5)).Should().NotBeNull();
        (await _seed.Tracks.GetAsync(6)).Should().NotBeNull();
        (await FtsMatchesAsync(gone.Title)).Should().NotContain(61, "the purged row left the search index");
        (await ScalarAsync(_seed, "SELECT COUNT(*) FROM track_artist WHERE track_id = 61")).Should().Be(0L, "credits cascade");
        (await ScalarAsync(_seed, "PRAGMA foreign_key_check")).Should().BeNull();
        (await ScalarAsync(_seed, "SELECT COUNT(*) FROM track_fts")).Should().Be((long)_all.Count - 1);
    }

    [Fact]
    public async Task Search_index_holds_exactly_the_present_rows_with_their_current_values_Async()
    {
        // Every non-corrupt fixture title is findable by a 3-gram of itself; the index has one row per track.
        (await ScalarAsync(_seed, "SELECT COUNT(*) FROM track_fts")).Should().Be((long)_all.Count);
        foreach (TrackDto track in _all.Where(t => t.Title.Length >= 3).Take(20))
        {
            (await FtsMatchesAsync(track.Title[..3])).Should().Contain(track.Id, track.Title);
        }
    }

    [Fact]
    public async Task Text_filter_escapes_like_wildcards_Async()
    {
        (await _seed.Tracks.CountAsync(new TrackQuery(Text: "%"))).Should().Be(0);
        (await _seed.Tracks.CountAsync(new TrackQuery(Text: "_"))).Should().Be(0);
        (await _seed.Tracks.CountAsync(new TrackQuery(Text: "morning", IncludeMissing: true))).Should().Be(_all.Count(t => t.Title.Contains("morning", StringComparison.OrdinalIgnoreCase)));
    }

    private TrackQuery ApplyFilter(TrackQuery query, string filter)
    {
        long album = _all.Where(t => t.AlbumId is not null).GroupBy(t => t.AlbumId).OrderByDescending(g => g.Count()).First().Key!.Value;
        long artist = _all.SelectMany(t => t.Artists).GroupBy(a => a.Id).OrderByDescending(g => g.Count()).First().Key;
        long coCredit = _all.Where(t => t.Artists.Count > 1).SelectMany(t => t.Artists.Skip(1)).First().Id;
        long genre = _genresByTrack.SelectMany(g => g).GroupBy(g => g).OrderByDescending(g => g.Count()).First().Key;
        return filter switch
        {
            "none" => query,
            "album" => query with { AlbumId = album },
            "artist" => query with { ArtistId = artist },
            "artist-cocredit" => query with { ArtistId = coCredit },
            "genre" => query with { GenreId = genre },
            "folder" => query with { FolderId = LibrarySeed.ExtraFolderId },
            "text-title" => query with { Text = "ing" },
            "text-artist" => query with { Text = _all[0].Artists[0].Name[1..4] },
            "text-album" => query with { Text = "tape" },
            "missing" => query with { IncludeMissing = true },
            "played" => query with { PlayedOnly = true },
            "genre+text" => query with { GenreId = genre, Text = "e" },
            "folder+album" => query with { FolderId = LibrarySeed.FixtureFolderId, AlbumId = album },
            _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "unknown filter"),
        };
    }

    /// <summary>The expected result computed independently of SQL from the same rows.</summary>
    private List<TrackDto> Oracle(TrackQuery q)
    {
        IEnumerable<TrackDto> rows = _all;
        if (!q.IncludeMissing)
        {
            rows = rows.Where(t => !t.Missing);
        }

        if (q.PlayedOnly)
        {
            rows = rows.Where(t => t.LastPlayedAt is not null);
        }

        if (q.AlbumId is { } album)
        {
            rows = rows.Where(t => t.AlbumId == album);
        }

        if (q.ArtistId is { } artist)
        {
            rows = rows.Where(t => t.Artists.Any(a => a.Id == artist));
        }

        if (q.GenreId is { } genre)
        {
            rows = rows.Where(t => _genresByTrack[t.Id].Contains(genre));
        }

        if (q.FolderId is { } folder)
        {
            rows = rows.Where(t => t.FolderId == folder);
        }

        if (q.Text is { } text)
        {
            rows = rows.Where(t => Contains(t.Title, text) || Contains(t.AlbumTitle, text) || t.Artists.Any(a => Contains(a.Name, text)));
        }

        List<TrackDto> ordered = rows.OrderBy(t => t, new RowComparer(q)).ToList();
        if (q.Descending)
        {
            ordered.Reverse();
        }

        return ordered;
    }

    private static bool Contains(string? haystack, string needle) => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private async Task<List<long>> FtsMatchesAsync(string text)
    {
        await using SqliteConnection connection = await _seed.Db.OpenConnectionAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM track_fts WHERE track_fts MATCH $q ORDER BY rowid";
        command.Parameters.AddWithValue("$q", "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        var ids = new List<long>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private static async Task<IReadOnlyList<TrackDto>> AllTracksAsync(LibrarySeed seed)
    {
        long count = (long)(await ScalarAsync(seed, "SELECT MAX(id) FROM track"))!;
        return await seed.Tracks.GetByIdsAsync(Enumerable.Range(1, (int)count).Select(i => (long)i).ToArray());
    }

    private static async Task<object?> ScalarAsync(LibrarySeed seed, string sql)
    {
        await using SqliteConnection connection = await seed.Db.OpenConnectionAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<List<(long, long)>> PairsAsync(LibrarySeed seed, string sql)
    {
        await using SqliteConnection connection = await seed.Db.OpenConnectionAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        var pairs = new List<(long, long)>();
        while (await reader.ReadAsync())
        {
            pairs.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }

        return pairs;
    }

    /// <summary>Orders rows by <see cref="TrackQuery.SortKeysOf"/> (NOCASE text, numeric longs) then id, ascending.</summary>
    private sealed class RowComparer(TrackQuery query) : IComparer<TrackDto>
    {
        public int Compare(TrackDto? x, TrackDto? y)
        {
            object[] a = query.SortKeysOf(x!);
            object[] b = query.SortKeysOf(y!);
            for (int i = 0; i < a.Length; i++)
            {
                int c = a[i] switch
                {
                    string s => SortKeys.NoCase.Compare(s, (string)b[i]),
                    long l => l.CompareTo((long)b[i]),
                    _ => throw new InvalidOperationException("unexpected key type " + a[i].GetType().Name),
                };
                if (c != 0)
                {
                    return c;
                }
            }

            return x!.Id.CompareTo(y!.Id);
        }
    }
}
