using Tunqio.Core.Library;
using Tunqio.Library.Art;

namespace Tunqio.Library.Tests.Art;

/// <summary>
/// E3-S7: the hashed on-disk cache. Layout and sizes from docs/library-and-data.md "Storage layout", the
/// embedded-over-folder preference (AC-96), palette.json beside the sizes (AC-97), one render per distinct
/// image, and nothing thrown for a bad image.
/// </summary>
public sealed class ArtCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunqio-art-" + Guid.NewGuid().ToString("N"));
    private readonly ArtCache _cache;

    public ArtCacheTests()
    {
        Directory.CreateDirectory(_root);
        _cache = new ArtCache(Path.Combine(_root, "art"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string AudioIn(string album, string name = "01 - Track.flac")
    {
        string dir = Path.Combine(_root, "music", album);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static EmbeddedPicture Picture(byte[] bytes, string mime = "image/png") => new(bytes, mime);

    private string Dir(string hash) => Path.Combine(_cache.Root, hash[..2], hash);

    [Fact]
    public async Task An_embedded_picture_is_rendered_into_the_hash_directory_with_three_sizes_the_original_and_a_palette_Async()
    {
        byte[] png = TestImages.Quadrants(1200, 800);
        string expected = ArtCache.Hash(png);

        ArtHashes hashes = await _cache.StoreAsync(Picture(png), AudioIn("A"));

        hashes.Should().Be(new ArtHashes(expected, expected), "an embedded picture is both the track's and the album's art");
        expected.Should().MatchRegex("^[0-9a-f]{64}$");
        string dir = Dir(expected);
        Directory.Exists(dir).Should().BeTrue();
        Directory.GetFiles(dir).Select(Path.GetFileName).Should().BeEquivalentTo(["1000.jpg", "300.jpg", "96.jpg", "original.png", "palette.json"]);
        (await TestImages.SizeOfAsync(Path.Combine(dir, "1000.jpg"))).Should().Be((1000, 667), "long edge 1000 at the source aspect");
        (await TestImages.SizeOfAsync(Path.Combine(dir, "300.jpg"))).Should().Be((300, 200));
        (await TestImages.SizeOfAsync(Path.Combine(dir, "96.jpg"))).Should().Be((96, 64));
        (await File.ReadAllBytesAsync(Path.Combine(dir, "original.png"))).Should().Equal(png, "the source is kept untouched");
        Directory.GetDirectories(_cache.Root, "*.tmp-*", SearchOption.AllDirectories).Should().BeEmpty();
        _cache.Rendered.Should().Be(1);
        _cache.Contains(expected).Should().BeTrue();

        ArtPalette? palette = await _cache.LoadPaletteAsync(expected);
        palette.Should().NotBeNull();
        palette!.Colors.Should().HaveCount(ArtPalette.Size);
        palette.Colors.Take(4).Should().OnlyContain(c => Math.Abs(c.Population - 0.25) < 0.02, "four quadrants survive the JPEG-free render and the resize");
        palette.Colors.Should().Contain(c => c.Luminance > 0.9 && c.R > 230, "white");
        palette.Colors.Should().Contain(c => c.Luminance < 0.1 && c.B > 180, "blue");
    }

    [Fact]
    public async Task A_jpeg_source_keeps_original_jpg_and_a_small_image_is_never_upscaled_Async()
    {
        byte[] jpeg = await TestImages.JpegAsync(TestImages.Gradient(200, 150));

        ArtHashes hashes = await _cache.StoreAsync(Picture(jpeg, "image/jpeg"), AudioIn("A"));

        string dir = Dir(hashes.TrackArtHash!);
        File.Exists(Path.Combine(dir, "original.jpg")).Should().BeTrue();
        (await TestImages.SizeOfAsync(Path.Combine(dir, "1000.jpg"))).Should().Be((200, 150), "a source below the size keeps its own size");
        (await TestImages.SizeOfAsync(Path.Combine(dir, "300.jpg"))).Should().Be((200, 150));
        (await TestImages.SizeOfAsync(Path.Combine(dir, "96.jpg"))).Should().Be((96, 72));
    }

    [Fact]
    public async Task A_source_over_four_megabytes_is_rendered_but_not_kept_Async()
    {
        byte[] big = TestImages.Noise(1300, 1200);
        big.Length.Should().BeGreaterThan(ArtCache.OriginalLimit, "noise does not compress");

        ArtHashes hashes = await _cache.StoreAsync(Picture(big), AudioIn("A"));

        string dir = Dir(hashes.AlbumArtHash!);
        Directory.GetFiles(dir).Select(Path.GetFileName).Should().BeEquivalentTo(["1000.jpg", "300.jpg", "96.jpg", "palette.json"]);
        (await TestImages.SizeOfAsync(Path.Combine(dir, "1000.jpg"))).Should().Be((1000, 923));
    }

    [Fact]
    public async Task Without_an_embedded_picture_the_folder_image_becomes_the_album_art_only_Async()
    {
        string audio = AudioIn("Folder Only");
        byte[] png = TestImages.Gradient(400, 400);
        await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(audio)!, "folder.png"), png);

        ArtHashes hashes = await _cache.StoreAsync(null, audio);

        hashes.Should().Be(new ArtHashes(null, ArtCache.Hash(png)), "track.art_hash is embedded art only; the album takes the folder image");
        _cache.Contains(hashes.AlbumArtHash).Should().BeTrue();
    }

    [Fact]
    public async Task An_embedded_picture_wins_over_the_folder_image_Async()
    {
        string audio = AudioIn("Both");
        byte[] folder = TestImages.Gradient(400, 400);
        byte[] embedded = TestImages.Quadrants(300, 300);
        await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(audio)!, "cover.png"), folder);

        ArtHashes hashes = await _cache.StoreAsync(Picture(embedded), audio);

        hashes.Should().Be(new ArtHashes(ArtCache.Hash(embedded), ArtCache.Hash(embedded)));
        _cache.Contains(ArtCache.Hash(folder)).Should().BeFalse("the folder image is not looked at when the file has its own picture");
    }

    [Fact]
    public async Task An_undecodable_picture_falls_back_to_the_folder_image_and_leaves_nothing_behind_Async()
    {
        string audio = AudioIn("Bad Embedded");
        byte[] folder = TestImages.Solid(64, 64, 10, 20, 30);
        await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(audio)!, "folder.jpg"), folder);
        byte[] garbage = new byte[5000];
        new Random(1).NextBytes(garbage);

        ArtHashes hashes = await _cache.StoreAsync(Picture(garbage), audio);

        hashes.Should().Be(new ArtHashes(null, ArtCache.Hash(folder)));
        Directory.Exists(Dir(ArtCache.Hash(garbage))).Should().BeFalse();
        Directory.GetDirectories(_cache.Root, "*.tmp-*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_to_store_gives_none_and_never_throws_Async()
    {
        string audio = AudioIn("Plain");
        (await _cache.StoreAsync(null, audio)).Should().Be(ArtHashes.None);
        (await _cache.StoreAsync(Picture([]), audio)).Should().Be(ArtHashes.None);
        (await _cache.StoreAsync(Picture([1, 2, 3]), audio)).Should().Be(ArtHashes.None, "three bytes are not an image");
        await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(audio)!, "cover.jpg"), [0xFF, 0xD8, 0xFF]);
        (await _cache.StoreAsync(null, audio)).Should().Be(ArtHashes.None, "a truncated folder image is no art");
        _cache.Rendered.Should().Be(0);
    }

    [Fact]
    public async Task The_same_picture_is_rendered_once_however_many_files_carry_it_Async()
    {
        byte[] png = TestImages.Gradient(500, 500);
        string a = AudioIn("A", "01.flac");
        string b = AudioIn("A", "02.flac");
        string c = AudioIn("B", "01.flac");

        ArtHashes first = await _cache.StoreAsync(Picture(png), a);
        ArtHashes second = await _cache.StoreAsync(Picture(png), b);
        ArtHashes third = await _cache.StoreAsync(Picture(png), c);

        second.Should().Be(first);
        third.Should().Be(first);
        _cache.Rendered.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_stores_of_one_picture_share_a_single_render_Async()
    {
        byte[] png = TestImages.Gradient(800, 800);
        string[] files = Enumerable.Range(1, 12).Select(i => AudioIn("A", $"{i:00}.flac")).ToArray();

        ArtHashes[] results = await Task.WhenAll(files.Select(f => _cache.StoreAsync(Picture(png), f)));

        results.Should().AllBeEquivalentTo(new ArtHashes(ArtCache.Hash(png), ArtCache.Hash(png)));
        _cache.Rendered.Should().Be(1);
        Directory.GetDirectories(_cache.Root, "*.tmp-*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Ten_files_of_one_folder_read_the_folder_image_once_Async()
    {
        string[] files = Enumerable.Range(1, 10).Select(i => AudioIn("Folder", $"{i:00}.wav")).ToArray();
        byte[] png = TestImages.Quadrants(300, 300);
        string image = Path.Combine(Path.GetDirectoryName(files[0])!, "folder.png");
        await File.WriteAllBytesAsync(image, png);

        foreach (string file in files)
        {
            (await _cache.StoreAsync(null, file)).AlbumArtHash.Should().Be(ArtCache.Hash(png));
        }

        _cache.Rendered.Should().Be(1);
    }

    [Fact]
    public async Task A_replaced_folder_image_is_noticed_Async()
    {
        string audio = AudioIn("Folder");
        string image = Path.Combine(Path.GetDirectoryName(audio)!, "folder.png");
        byte[] first = TestImages.Solid(64, 64, 200, 0, 0);
        byte[] second = TestImages.Solid(64, 64, 0, 0, 200);
        await File.WriteAllBytesAsync(image, first);
        (await _cache.StoreAsync(null, audio)).AlbumArtHash.Should().Be(ArtCache.Hash(first));

        await File.WriteAllBytesAsync(image, second);
        File.SetLastWriteTimeUtc(image, File.GetLastWriteTimeUtc(image).AddSeconds(5));

        (await _cache.StoreAsync(null, audio)).AlbumArtHash.Should().Be(ArtCache.Hash(second));
        _cache.Rendered.Should().Be(2);
    }

    [Fact]
    public async Task Paths_are_derived_without_io_and_only_for_real_hashes_Async()
    {
        byte[] png = TestImages.Solid(32, 32, 1, 2, 3);
        string hash = ArtCache.Hash(png);

        _cache.PathFor(hash, ArtSize.Tile).Should().Be(Path.Combine(_cache.Root, hash[..2], hash, "300.jpg"));
        _cache.PathFor(hash.ToUpperInvariant(), ArtSize.Large).Should().Be(Path.Combine(_cache.Root, hash[..2], hash, "1000.jpg"));
        _cache.PathFor(hash, ArtSize.Thumbnail).Should().EndWith("96.jpg");
        _cache.PathFor(null, ArtSize.Tile).Should().BeNull();
        _cache.PathFor("art-1a2b3c4d", ArtSize.Tile).Should().BeNull("the synthetic 100k database carries placeholder hashes");
        _cache.PathFor(new string('z', 64), ArtSize.Tile).Should().BeNull();
        (await _cache.LoadPaletteAsync(hash)).Should().BeNull("nothing is stored yet");
        _cache.Contains(hash).Should().BeFalse();

        await _cache.StoreAsync(Picture(png), AudioIn("A"));

        File.Exists(_cache.PathFor(hash, ArtSize.Tile)).Should().BeTrue();
        (await _cache.LoadPaletteAsync(hash))!.Dominant.Should().Match<PaletteColor>(c => c.Population == 1);
    }

    [Fact]
    public async Task Clearing_removes_every_image_and_a_later_store_renders_again_Async()
    {
        byte[] png = TestImages.Gradient(100, 100);
        string audio = AudioIn("A");
        ArtHashes hashes = await _cache.StoreAsync(Picture(png), audio);
        string other = AudioIn("B");
        await File.WriteAllBytesAsync(Path.Combine(Path.GetDirectoryName(other)!, "folder.png"), TestImages.Solid(8, 8, 9, 9, 9));
        (await _cache.StoreAsync(null, other)).AlbumArtHash.Should().NotBeNull();
        _cache.Rendered.Should().Be(2);

        await _cache.ClearAsync();

        _cache.Contains(hashes.AlbumArtHash).Should().BeFalse();
        Directory.Exists(_cache.Root).Should().BeTrue("the root stays");
        Directory.GetFileSystemEntries(_cache.Root).Should().BeEmpty();
        (await _cache.StoreAsync(Picture(png), audio)).Should().Be(hashes);
        _cache.Rendered.Should().Be(3);
    }

    [Fact]
    public async Task An_incomplete_directory_from_a_crash_is_rendered_over_Async()
    {
        byte[] png = TestImages.Gradient(100, 100);
        string hash = ArtCache.Hash(png);
        Directory.CreateDirectory(Dir(hash));
        await File.WriteAllBytesAsync(Path.Combine(Dir(hash), "1000.jpg"), [1, 2, 3]);

        await _cache.StoreAsync(Picture(png), AudioIn("A"));

        _cache.Contains(hash).Should().BeTrue();
        (await TestImages.SizeOfAsync(Path.Combine(Dir(hash), "1000.jpg"))).Should().Be((100, 100), "the stub was replaced");
        _cache.Rendered.Should().Be(1);
    }

    [Fact]
    public async Task Cancellation_propagates_and_leaves_no_temporary_directory_Async()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => _cache.StoreAsync(Picture(TestImages.Gradient(100, 100)), AudioIn("A"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _cache.Rendered.Should().Be(0);
        (Directory.Exists(_cache.Root) ? Directory.GetDirectories(_cache.Root, "*", SearchOption.AllDirectories) : []).Should().BeEmpty();
    }
}
