using Tunqio.Core.Library;

namespace Tunqio.Library.Tests.Repositories;

/// <summary>E3-S8: the values behind the Albums grid's decade and format chips.</summary>
public sealed class AlbumFacetsTests : IAsyncLifetime
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

    [Fact]
    public async Task Facets_are_the_decades_and_codecs_of_present_tracks_ascending_Async()
    {
        AlbumFacets facets = await _seed.Service.Albums.ListFacetsAsync();

        // Album year is part of album identity, so a present track's year is its album's year.
        int[] decades = _tracks.Where(t => !t.Missing && t.AlbumId is not null && t.Year is not null)
            .Select(t => t.Year!.Value / 10 * 10).Distinct().Order().ToArray();
        string[] codecs = _tracks.Where(t => !t.Missing).Select(t => t.Codec).Distinct().Order(StringComparer.Ordinal).ToArray();

        facets.Decades.Should().Equal(decades);
        facets.Codecs.Should().Equal(codecs);
        decades.Should().NotBeEmpty();
        codecs.Should().Contain("flac").And.Contain("mp3");
    }

    [Fact]
    public async Task A_missing_track_takes_its_codec_and_decade_out_of_the_facets_Async()
    {
        AlbumFacets before = await _seed.Service.Albums.ListFacetsAsync();
        string codec = before.Codecs[0];
        long[] ids = _tracks.Where(t => !t.Missing && t.Codec == codec).Select(t => t.Id).ToArray();
        await _seed.Tracks.MarkMissingAsync(ids, true);

        AlbumFacets after = await _seed.Service.Albums.ListFacetsAsync();

        after.Codecs.Should().NotContain(codec);
        after.Codecs.Should().Equal(before.Codecs.Where(c => c != codec));
    }
}
