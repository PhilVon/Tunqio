using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.Library.Repositories;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>
/// E3-S9: <see cref="SqliteSearchService"/> over the seeded library. Terms match as substrings in any of the
/// indexed columns, the groups follow their own rules, short terms go through LIKE, the text can never reach
/// the FTS parser as syntax, and the rebuild repairs an index that has drifted from the track table.
/// </summary>
public sealed class SearchServiceTests : IAsyncLifetime
{
    private LibrarySeed _seed = null!;
    private ISearchService _search = null!;

    public async Task InitializeAsync()
    {
        _seed = await LibrarySeed.CreateAsync();
        _search = _seed.Service.Search;
    }

    public Task DisposeAsync()
    {
        _seed.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_term_matches_title_album_album_artist_or_a_credited_artist_Async()
    {
        SearchResults results = await _search.SearchAsync("field", SearchLimits.Default);

        // "Field Notes" is the artist and album artist of Tape One; "The Field Notes" fronts Odd Years, whose
        // third track credits only Guest Star, so it is found through the album artist column.
        Titles(results.Tracks).Should().Contain(["Rain on Tin", "Kettle", "First", "second", "Third"]);
        results.Albums.Select(a => a.Title).Should().Contain("Tape One").And.Contain(t => t.Equals("odd years", StringComparison.OrdinalIgnoreCase));
        results.Artists.Select(a => a.Name).Should().BeEquivalentTo(["Field Notes", "The Field Notes"], "a co-credit such as Guest Star must not leak into the artists group");
    }

    [Fact]
    public async Task Matching_is_case_insensitive_and_a_substring_Async()
    {
        SearchResults upper = await _search.SearchAsync("ORNIN", SearchLimits.Default);
        SearchResults lower = await _search.SearchAsync("ornin", SearchLimits.Default);

        Titles(upper).Should().BeEquivalentTo(["Morning", "morning", "MORNING"]);
        Titles(lower).Should().BeEquivalentTo(Titles(upper));
        upper.Artists.Select(a => a.Name).Should().BeEmpty();
    }

    [Fact]
    public async Task Every_term_must_match_and_terms_may_land_in_different_columns_Async()
    {
        SearchResults results = await _search.SearchAsync("notes third", SearchLimits.Default);

        Titles(results).Should().Equal(["Third"], "the album artist holds 'notes' and the title 'third'");
        results.Albums.Should().BeEmpty("no album has both terms in its title or album artist");
        results.Artists.Should().BeEmpty("no artist name has both terms");
    }

    [Fact]
    public async Task A_short_term_beside_a_long_one_narrows_through_like_Async()
    {
        Titles(await _search.SearchAsync("field", SearchLimits.Default)).Should().Contain(["First", "second", "Third"]);
        Titles(await _search.SearchAsync("field ir", SearchLimits.Default)).Should().Contain(["First", "Third"]).And.NotContain("second");
    }

    [Fact]
    public async Task A_query_with_no_long_term_runs_on_like_alone_Async()
    {
        SearchResults results = await _search.SearchAsync("ze", SearchLimits.Default);

        Titles(results).Should().BeEquivalentTo(["Morning", "morning", "MORNING"], "the three Same Title tracks credit Zed");
        results.Artists.Select(a => a.Name).Should().Equal("Zed");
        results.Albums.Select(a => a.Title).Should().Equal("Same Title");
    }

    [Fact]
    public async Task A_title_that_starts_with_the_text_ranks_first_then_alphabetical_Async()
    {
        SearchResults results = await _search.SearchAsync("static", SearchLimits.Default);

        Titles(results).Should().Equal("Static", "Dawn Static");
    }

    [Fact]
    public async Task Albums_match_on_title_or_album_artist_only_Async()
    {
        SearchResults byArtist = await _search.SearchAsync("meridian", SearchLimits.Default);
        SearchResults byTrack = await _search.SearchAsync("candlelight", SearchLimits.Default);

        byArtist.Albums.Select(a => a.Title).Should().Equal("Two Halls");
        byArtist.Albums[0].TrackCount.Should().Be(10);
        byArtist.Albums[0].AlbumArtist.Should().Be("Orchestra Meridian");
        byTrack.Tracks.Select(t => t.Title).Should().Equal("Candlelight");
        byTrack.Albums.Should().BeEmpty("a track title does not surface its album in the albums group");
    }

    [Fact]
    public async Task Missing_tracks_are_not_found_Async()
    {
        SearchResults results = await _search.SearchAsync("sketch", SearchLimits.Default);

        Titles(results).Should().Equal(["another sketch"], "Untitled Sketch (id 61) is flagged missing by the seed");
        results.Artists.Should().BeEmpty("Nobody Famous has no present track");
    }

    [Fact]
    public async Task Limits_cut_each_group_and_flag_when_more_exist_Async()
    {
        SearchResults two = await _search.SearchAsync("morning", new SearchLimits(Tracks: 2, Albums: 0, Artists: 0));
        SearchResults three = await _search.SearchAsync("morning", new SearchLimits(Tracks: 3, Albums: 1, Artists: 1));

        two.Tracks.Should().HaveCount(2);
        two.MoreTracks.Should().BeTrue();
        two.Albums.Should().BeEmpty("a limit of 0 skips the group");
        two.MoreAlbums.Should().BeFalse();
        three.Tracks.Should().HaveCount(3);
        three.MoreTracks.Should().BeFalse();
        three.Albums.Should().BeEmpty("no album is called morning");
    }

    [Fact]
    public async Task Blank_text_is_empty_without_a_query_Async()
    {
        SearchResults results = await _search.SearchAsync("  \t ", SearchLimits.Default);

        results.IsEmpty.Should().BeTrue();
        results.Text.Should().Be("  \t ");
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\"\"\"")]
    [InlineData("(*)")]
    [InlineData("NOT AND OR")]
    [InlineData("{title}: x")]
    [InlineData("title:hall")]
    [InlineData("^hall*")]
    [InlineData("%_\\")]
    [InlineData("hall -")]
    public async Task Fts_and_like_syntax_in_the_text_is_literal_Async(string text)
    {
        Func<Task<SearchResults>> act = () => _search.SearchAsync(text, SearchLimits.Default);

        SearchResults results = (await act.Should().NotThrowAsync()).Which;
        results.Text.Should().Be(text);
        if (text == "title:hall")
        {
            results.Tracks.Should().BeEmpty("the colon is part of the term, not a column filter");
        }
    }

    [Fact]
    public async Task Non_ascii_text_matches_case_insensitively_through_the_index_Async()
    {
        TrackDto track = (await _seed.Tracks.ListAsync(new TrackQuery(PageSize: 500))).First(t => t.Title.Any(c => c > 127));
        string prefix = track.Title[..4];

        Titles(await _search.SearchAsync(prefix.ToUpperInvariant(), SearchLimits.Default)).Should().Contain(track.Title);
        Titles(await _search.SearchAsync(prefix.ToLowerInvariant(), SearchLimits.Default)).Should().Contain(track.Title);
    }

    [Fact]
    public async Task Rebuild_repairs_a_desynchronised_index_Async()
    {
        long third = (await _seed.Tracks.ListAsync(new TrackQuery(Text: "Third"))).Single().Id;
        long trackRows = await CountAsync("SELECT COUNT(*) FROM track");

        // Two kinds of drift: a row the index lost, and a row it holds for a track that does not exist.
        await DesynchroniseAsync(third);
        Titles(await _search.SearchAsync("third", SearchLimits.Default)).Should().NotContain("Third", "the index no longer holds the row");
        (await FtsMatchesAsync("ghost")).Should().Equal(999_999);

        int indexed = await _search.RebuildIndexAsync();

        indexed.Should().Be((int)trackRows, "every track row, present or missing, is indexed as the maintenance would");
        (await CountAsync("SELECT COUNT(*) FROM track_fts")).Should().Be(trackRows);
        Titles(await _search.SearchAsync("third", SearchLimits.Default)).Should().Contain("Third");
        (await FtsMatchesAsync("ghost")).Should().BeEmpty();
        Titles(await _search.SearchAsync("sketch", SearchLimits.Default)).Should().Equal(["another sketch"], "the rebuilt index still hides missing tracks through the query");
    }

    private static List<string> Titles(SearchResults results) => Titles(results.Tracks);

    private static List<string> Titles(IReadOnlyList<TrackDto> tracks) => tracks.Select(t => t.Title).ToList();

    private async Task DesynchroniseAsync(long id)
    {
        await using SqliteConnection connection = await _seed.Db.OpenConnectionAsync();
        await using SqliteCommand row = connection.CreateCommand();
        row.CommandText = FtsSql.Row;
        row.Parameters.AddWithValue("$id", id);
        string[] values;
        await using (SqliteDataReader reader = await row.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeTrue();
            values = [reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)];
        }

        await using SqliteCommand drop = connection.CreateCommand();
        drop.CommandText = FtsSql.Delete;
        drop.Parameters.AddWithValue("$id", id);
        drop.Parameters.AddWithValue("$title", values[0]);
        drop.Parameters.AddWithValue("$artists", values[1]);
        drop.Parameters.AddWithValue("$album", values[2]);
        drop.Parameters.AddWithValue("$album_artist", values[3]);
        await drop.ExecuteNonQueryAsync();

        await using SqliteCommand ghost = connection.CreateCommand();
        ghost.CommandText = FtsSql.Insert;
        ghost.Parameters.AddWithValue("$id", 999_999L);
        ghost.Parameters.AddWithValue("$title", "Ghost Row");
        ghost.Parameters.AddWithValue("$artists", string.Empty);
        ghost.Parameters.AddWithValue("$album", string.Empty);
        ghost.Parameters.AddWithValue("$album_artist", string.Empty);
        await ghost.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using SqliteConnection connection = await _seed.Db.OpenConnectionAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<List<long>> FtsMatchesAsync(string text)
    {
        await using SqliteConnection connection = await _seed.Db.OpenConnectionAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM track_fts WHERE track_fts MATCH $q ORDER BY rowid";
        command.Parameters.AddWithValue("$q", "\"" + text + "\"");
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        var ids = new List<long>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }
}
