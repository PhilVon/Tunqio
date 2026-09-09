using TagLib;
using Tunqio.Core.Library;
using Tunqio.FixtureGen;
using Tunqio.Library.Art;
using Tunqio.Library.Tests.Scanning;

namespace Tunqio.Library.Tests.Art;

/// <summary>
/// AC-96 end to end: the scanner over the fixture library with the real cache. The album with only
/// <c>folder.png</c> gets art, albums with embedded pictures get theirs, a file with a back and a front cover
/// records the front one, and one distinct picture costs one render.
/// </summary>
public sealed class ArtScanTests : IDisposable
{
    private readonly string _artRoot = Path.Combine(Path.GetTempPath(), "tunqio-art-scan-" + Guid.NewGuid().ToString("N"));
    private readonly ArtCache _cache;

    public ArtScanTests() => _cache = new ArtCache(_artRoot);

    public void Dispose()
    {
        if (Directory.Exists(_artRoot))
        {
            Directory.Delete(_artRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Folder_only_albums_and_embedded_albums_both_end_up_with_art()
    {
        using ScanHarness h = await ScanHarness.CreateAsync(artCache: _cache);
        FixtureManifest manifest = ScanHarness.Manifest();

        ScanReport report = await h.ScanAsync();

        report.Outcome.Should().Be(ScanOutcome.Completed);
        IReadOnlyList<AlbumDto> albums = await h.Service.Albums.ListAsync(new AlbumQuery(PageSize: 100));
        var expectedByAlbum = manifest.Files
            .Where(f => !f.CorruptTags && (f.EmbeddedArt || f.FolderArt))
            .GroupBy(f => f.AlbumTitle)
            .ToDictionary(g => g.Key, g => ArtCache.Hash(ArtGenerator.Png(g.Key)), StringComparer.Ordinal);
        expectedByAlbum.Should().NotBeEmpty();

        foreach ((string title, string hash) in expectedByAlbum)
        {
            AlbumDto album = albums.Should().ContainSingle(a => a.Title == title).Subject;
            album.ArtHash.Should().Be(hash, $"'{title}' has the generator's cover as its art");
            _cache.Contains(hash).Should().BeTrue();
            System.IO.File.Exists(_cache.PathFor(hash, ArtSize.Tile)).Should().BeTrue();
            (await _cache.LoadPaletteAsync(hash))!.Colors.Should().HaveCount(ArtPalette.Size);
        }

        FixtureFileEntry folderOnly = manifest.Files.First(f => f.FolderArt && !f.EmbeddedArt);
        TrackDto folderTrack = await h.TrackAtAsync(h.PathOf(folderOnly));
        folderTrack.ArtHash.Should().Be(expectedByAlbum[folderOnly.AlbumTitle], "the track shows its album's folder image");
        h.Scalar($"SELECT COUNT(*) FROM track t JOIN album a ON a.id = t.album_id WHERE a.title = '{folderOnly.AlbumTitle.Replace("'", "''")}' AND t.art_hash IS NOT NULL")
            .Should().Be(0, "track.art_hash is embedded art only");

        FixtureFileEntry embedded = manifest.Files.First(f => f.EmbeddedArt && !f.CorruptTags);
        (await h.TrackAtAsync(h.PathOf(embedded))).ArtHash.Should().Be(expectedByAlbum[embedded.AlbumTitle]);
        h.Scalar("SELECT COUNT(*) FROM track WHERE art_hash IS NOT NULL")
            .Should().Be(manifest.Files.Count(f => f.EmbeddedArt && !f.CorruptTags), "every file with an embedded picture records it");

        _cache.Rendered.Should().Be(expectedByAlbum.Count, "one distinct picture per album, rendered once");
        foreach (AlbumDto album in albums.Where(a => !expectedByAlbum.ContainsKey(a.Title)))
        {
            album.ArtHash.Should().BeNull($"'{album.Title}' has no art");
        }
    }

    [Fact]
    public async Task The_front_cover_is_preferred_over_other_embedded_pictures()
    {
        using ScanHarness h = await ScanHarness.CreateAsync(artCache: _cache);
        FixtureManifest manifest = ScanHarness.Manifest();
        FixtureFileEntry entry = manifest.Files.First(f => f.Format == "flac" && f.EmbeddedArt);
        string path = h.PathOf(entry);
        byte[] back = TestImages.Solid(64, 64, 200, 0, 0);
        byte[] front = TestImages.Solid(64, 64, 0, 0, 200);
        using (TagLib.File file = TagLib.File.Create(path))
        {
            file.Tag.Pictures =
            [
                new Picture(new ByteVector(back)) { Type = PictureType.BackCover, MimeType = "image/png" },
                new Picture(new ByteVector(front)) { Type = PictureType.FrontCover, MimeType = "image/png" },
            ];
            file.Save();
        }

        await h.ScanAsync();

        (await h.TrackAtAsync(path)).ArtHash.Should().Be(ArtCache.Hash(front));
        _cache.Contains(ArtCache.Hash(back)).Should().BeFalse();
    }
}
