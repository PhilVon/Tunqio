using Tunqio.Core.Library;
using Tunqio.FixtureGen;
using Tunqio.Library.Art;
using Tunqio.Library.Tags;
using Tunqio.Library.Tests.Scanning;

namespace Tunqio.Library.Tests.Art;

/// <summary>
/// T-207: an album's art follows its tracks. The album row used to keep the first hash it was ever given, so a
/// cover removed from every track (T-113) left the album tile showing it from the cache for good.
/// </summary>
public sealed class AlbumArtTests : IDisposable
{
    private readonly string _artRoot = Path.Combine(Path.GetTempPath(), "tunqio-album-art-" + Guid.NewGuid().ToString("N"));
    private readonly ArtCache _cache;

    public AlbumArtTests() => _cache = new ArtCache(_artRoot);

    public void Dispose()
    {
        if (Directory.Exists(_artRoot))
        {
            Directory.Delete(_artRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Removing_the_cover_from_every_track_takes_the_album_s_art_with_it_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync(artCache: _cache);
        await h.ScanAsync();
        (string title, List<TrackDto> tracks) = await EmbeddedAlbumAsync(h);
        string original = (await AlbumHashAsync(h, title))!;
        var writer = new TagLibTagWriter();

        // The first track loses its cover: the album still has one, on the next track along.
        (await writer.WriteAsync(tracks[0].Path, new TagEdit(Pictures: []))).Outcome.Should().Be(TagWriteOutcome.Written);
        await h.ScanAsync(ScanRequest.Targeted(h.Folder.Id, [tracks[0].Path]));
        (await AlbumHashAsync(h, title)).Should().Be(original, "the other tracks still carry the same picture");

        foreach (TrackDto track in tracks.Skip(1))
        {
            await writer.WriteAsync(track.Path, new TagEdit(Pictures: []));
        }

        await h.ScanAsync(ScanRequest.Targeted(h.Folder.Id, [.. tracks.Skip(1).Select(t => t.Path)]));

        (await AlbumHashAsync(h, title)).Should().BeNull("no track carries a picture any more and the folder has no image");
        foreach (TrackDto track in tracks)
        {
            (await h.TrackAtAsync(track.Path)).ArtHash.Should().BeNull("the row's fallback to the album's art has nothing to fall back to");
        }
    }

    [Fact]
    public async Task A_new_cover_on_the_first_track_becomes_the_album_s_art_and_a_later_track_s_does_not_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync(artCache: _cache);
        await h.ScanAsync();
        (string title, List<TrackDto> tracks) = await EmbeddedAlbumAsync(h);
        byte[] front = TestImages.Solid(64, 64, 0, 0, 200);
        byte[] later = TestImages.Solid(64, 64, 200, 0, 0);
        var writer = new TagLibTagWriter();

        await writer.WriteAsync(tracks[^1].Path, new TagEdit(Pictures: [new EmbeddedPicture(later, "image/png")]));
        await h.ScanAsync(ScanRequest.Targeted(h.Folder.Id, [tracks[^1].Path]));
        (await AlbumHashAsync(h, title)).Should().NotBe(ArtCache.Hash(later), "the last track's new picture is its own, the album keeps the first track's");

        await writer.WriteAsync(tracks[0].Path, new TagEdit(Pictures: [new EmbeddedPicture(front, "image/png")]));
        await h.ScanAsync(ScanRequest.Targeted(h.Folder.Id, [tracks[0].Path]));

        (await AlbumHashAsync(h, title)).Should().Be(ArtCache.Hash(front), "the first track's picture is the album's");
    }

    [Fact]
    public async Task A_folder_image_album_keeps_its_art_through_a_targeted_rescan_Async()
    {
        using ScanHarness h = await ScanHarness.CreateAsync(artCache: _cache);
        await h.ScanAsync();
        FixtureFileEntry folderOnly = ScanHarness.Manifest().Files.First(f => f.FolderArt && !f.EmbeddedArt);
        string? before = await AlbumHashAsync(h, folderOnly.AlbumTitle);
        before.Should().NotBeNull("the fixture album has a folder image");

        await h.ScanAsync(ScanRequest.Targeted(h.Folder.Id, [h.PathOf(folderOnly)]));

        (await AlbumHashAsync(h, folderOnly.AlbumTitle)).Should().Be(before, "the batch reported the folder image and the derivation fell back to it");
    }

    /// <summary>An album every one of whose fixture files carries an embedded picture, with its tracks in disc and track order.</summary>
    private static async Task<(string Title, List<TrackDto> Tracks)> EmbeddedAlbumAsync(ScanHarness h)
    {
        FixtureManifest manifest = ScanHarness.Manifest();
        string title = manifest.Files
            .GroupBy(f => f.AlbumTitle, StringComparer.Ordinal)
            .First(g => g.All(f => f.EmbeddedArt && !f.CorruptTags) && g.Count() >= 2)
            .Key;
        List<TrackDto> tracks = [.. (await h.AllTracksAsync())
            .Where(t => string.Equals(t.AlbumTitle, title, StringComparison.Ordinal))
            .OrderBy(t => t.DiscNo ?? 0).ThenBy(t => t.TrackNo ?? 0).ThenBy(t => t.Id)];
        tracks.Should().HaveCountGreaterThanOrEqualTo(2);
        return (title, tracks);
    }

    private static async Task<string?> AlbumHashAsync(ScanHarness h, string title) =>
        (await h.Service.Albums.ListAsync(new AlbumQuery(PageSize: 100))).Single(a => string.Equals(a.Title, title, StringComparison.Ordinal)).ArtHash;
}
