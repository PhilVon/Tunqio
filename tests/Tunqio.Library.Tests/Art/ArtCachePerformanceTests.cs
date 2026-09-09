using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Tunqio.Core.Library;
using Tunqio.FixtureGen;
using Tunqio.Library.Art;
using Tunqio.Library.Database;
using Tunqio.Library.Repositories;
using Xunit.Abstractions;

namespace Tunqio.Library.Tests.Art;

/// <summary>
/// E3-S7 gate (AC-98): regenerating the cache from scratch for 1k albums takes under 60 s. The tree is 1000
/// album folders of ten tagged WAVs, each file carrying its album's cover, so every file passes through the
/// ExtractArt stage and every album is decoded once. The first scan fills the cache; then the cache is cleared
/// and a forced re-read rebuilds it, which is what Settings &gt; Library &gt; Regenerate art does. The budget is
/// stated for the reference machine (T-90); the output line records what this machine did.
/// </summary>
public sealed class ArtCachePerformanceTests(ITestOutputHelper output) : IDisposable
{
    private const int Albums = 1_000;
    private const int Files = Albums * ScanTreeBuilder.TracksPerAlbum;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunqio-art-perf-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task Regenerating_art_for_a_thousand_albums_takes_under_60s_Async()
    {
        string tree = Path.Combine(_root, "music");
        Stopwatch generate = Stopwatch.StartNew();
        ScanTreeBuilder.Build(tree, Files, embedArt: true);
        generate.Stop();

        var paths = new AppPaths(Path.Combine(_root, "app"));
        paths.EnsureCreated();
        using LibraryDatabase db = LibraryDatabase.Open(paths);
        var cache = new ArtCache(paths);
        var service = new LibraryService(db, artCache: cache);
        await service.Folders.AddAsync(tree);

        Stopwatch first = Stopwatch.StartNew();
        ScanReport initial = await service.Scanner.ScanAsync(ScanRequest.All);
        first.Stop();

        initial.Outcome.Should().Be(ScanOutcome.Completed);
        initial.Added.Should().Be(Files);
        cache.Rendered.Should().Be(Albums, "one distinct cover per album");
        IReadOnlyList<AlbumDto> albums = await service.Albums.ListAsync(new AlbumQuery(PageSize: Albums + 1));
        albums.Should().HaveCount(Albums);
        albums.Should().OnlyContain(a => a.ArtHash != null);

        await cache.ClearAsync();
        Stopwatch regenerate = Stopwatch.StartNew();
        ScanReport rebuild = await service.Scanner.ScanAsync(new ScanRequest(ForceReread: true));
        regenerate.Stop();

        output.WriteLine($"generate {generate.Elapsed.TotalSeconds:0.0} s; first scan with art {first.Elapsed.TotalSeconds:0.0} s; regenerate (clear + forced re-read) {regenerate.Elapsed.TotalSeconds:0.0} s for {Albums} albums / {Files} files");

        rebuild.Outcome.Should().Be(ScanOutcome.Completed);
        rebuild.Processed.Should().Be(Files);
        cache.Rendered.Should().Be(2 * Albums);
        foreach (AlbumDto album in albums)
        {
            File.Exists(cache.PathFor(album.ArtHash, ArtSize.Tile)).Should().BeTrue();
        }

        regenerate.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60), $"regenerating art for {Albums} albums took {regenerate.Elapsed.TotalSeconds:0.0} s");
    }
}
