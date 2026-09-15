using FluentAssertions;
using Tunqio.Core.Library;
using Tunqio.Library.Playlists;
using Tunqio.Library.Tests.Repositories;

namespace Tunqio.Library.Tests.Playlists;

/// <summary>
/// E6-S2 (AC-145): playlists exported to M3U8 and imported back over the seeded library, and the auto-export that writes
/// every change to the exports folder within its window, renames and deletes included, without ever touching a file this
/// process did not write.
/// </summary>
public sealed class PlaylistFilesTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunqio-playlists-" + Guid.NewGuid().ToString("N"));
    private LibrarySeed _seed = null!;
    private TrackDto[] _tracks = null!;

    private string Exports => Path.Combine(_root, "exports", "playlists");

    private IPlaylistRepository Playlists => _seed.Service.Playlists;

    public async Task InitializeAsync()
    {
        _seed = await LibrarySeed.CreateAsync(withExtras: false);
        _tracks = [.. await _seed.Tracks.ListAsync(new TrackQuery(PageSize: 4))];
    }

    public Task DisposeAsync()
    {
        _seed.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private PlaylistFiles Files(TimeSpan window) => new(Playlists, _seed.Tracks, Exports, window, TimeProvider.System, logger: null);

    private async Task<long> PlaylistAsync(string name, params int[] tracks)
    {
        PlaylistDto created = await Playlists.CreateAsync(name);
        await Playlists.AddTracksAsync(created.Id, [.. tracks.Select(i => _tracks[i].Id)]);
        return created.Id;
    }

    private async Task<long[]> ItemsOfAsync(long id) => [.. (await Playlists.GetDetailAsync(id))!.Tracks.Select(t => t.Id)];

    /// <summary>
    /// Waits for a file condition the auto-export's timer brings about; a real 50 ms window, so seconds is generous.
    /// A condition that reads the file can meet the exporter mid-replace (it writes a .tmp and moves it over the
    /// path) and get a sharing violation; that is "not yet", not a failure, and CI caught it once (T-211).
    /// </summary>
    private static async Task EventuallyAsync(Func<bool> condition, string what)
    {
        for (var waited = System.Diagnostics.Stopwatch.StartNew(); waited.Elapsed < TimeSpan.FromSeconds(5); await Task.Delay(20))
        {
            bool met;
            try
            {
                met = condition();
            }
            catch (IOException)
            {
                met = false;
            }

            if (met)
            {
                return;
            }
        }

        condition().Should().BeTrue(what);
    }

    [Fact]
    public async Task An_exported_playlist_lists_its_tracks_in_order_and_importing_the_file_recreates_it_Async()
    {
        long id = await PlaylistAsync("Sunday", 2, 0, 2, 1);
        long[] before = await ItemsOfAsync(id);
        using PlaylistFiles files = Files(TimeSpan.FromHours(1));
        string file = Path.Combine(_root, "out", "Sunday.m3u8");

        PlaylistExportResult? exported = await files.ExportAsync(id, file);
        await Playlists.DeleteAsync(id);
        PlaylistImportResult imported = await files.ImportAsync(file);

        exported!.TrackCount.Should().Be(4);
        string[] lines = await File.ReadAllLinesAsync(file);
        lines.Where(l => !l.StartsWith('#')).Should().Equal(new[] { _tracks[2].Path, _tracks[0].Path, _tracks[2].Path, _tracks[1].Path }, "the seed's tracks are on D: and the temp folder is not, so they cannot be relative");
        imported.Created.Should().NotBeNull();
        imported.Name.Should().Be("Sunday");
        imported.Unmatched.Should().Be(0);
        (await ItemsOfAsync(imported.Created!.Id)).Should().Equal(before);
    }

    [Fact]
    public async Task Importing_a_name_already_taken_adds_a_number_and_entries_the_library_lacks_are_counted_Async()
    {
        await PlaylistAsync("Mix", 0);
        string file = Path.Combine(_root, "Mix.m3u8");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(file, "#EXTM3U\r\n" + _tracks[1].Path + "\r\n" + @"D:\Music\Nowhere\gone.flac" + "\r\n");
        using PlaylistFiles files = Files(TimeSpan.FromHours(1));

        PlaylistImportResult result = await files.ImportAsync(file);

        result.Name.Should().Be("Mix (2)");
        result.Matched.Should().Be(1);
        result.Unmatched.Should().Be(1);
        (await ItemsOfAsync(result.Created!.Id)).Should().Equal(_tracks[1].Id);
    }

    [Fact]
    public async Task A_file_none_of_whose_tracks_is_in_the_library_makes_no_playlist_so_a_later_import_is_not_blocked_Async()
    {
        Directory.CreateDirectory(_root);
        string file = Path.Combine(_root, "Before rescan.m3u8");
        await File.WriteAllTextAsync(file, "#EXTM3U\r\n" + @"D:\Music\Nowhere\gone.flac" + "\r\n");
        using PlaylistFiles files = Files(TimeSpan.FromHours(1));

        PlaylistImportResult result = await files.ImportAsync(file);

        result.Created.Should().BeNull();
        result.Unmatched.Should().Be(1);
        (await Playlists.ListAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Import_from_exports_restores_playlists_after_a_reset_and_skips_names_already_present_Async()
    {
        long sunday = await PlaylistAsync("Sunday", 0, 1);
        await PlaylistAsync("Road/Trip", 3);
        using (PlaylistFiles writer = Files(TimeSpan.FromHours(1)))
        {
            await writer.StartAsync(); // catches up: writes every playlist now
        }

        // The reset, as far as playlists go: one gone, one survived.
        foreach (PlaylistDto p in await Playlists.ListAsync())
        {
            if (p.Id != sunday)
            {
                await Playlists.DeleteAsync(p.Id);
            }
        }

        using PlaylistFiles files = Files(TimeSpan.FromHours(1));
        IReadOnlyList<PlaylistImportResult> results = await files.ImportExportsAsync();

        Directory.EnumerateFiles(Exports).Select(Path.GetFileName).Should().BeEquivalentTo("Sunday.m3u8", "Road_Trip.m3u8");
        results.Should().ContainSingle(r => r.AlreadyExists).Which.Name.Should().Be("Sunday");
        results.Should().ContainSingle(r => r.Created != null).Which.Name.Should().Be("Road/Trip", "#PLAYLIST keeps the name the file name could not");
        (await Playlists.ListAsync()).Select(p => p.Name).Should().BeEquivalentTo("Sunday", "Road/Trip");
    }

    [Fact]
    public async Task Every_change_is_exported_within_the_window_and_a_rename_or_delete_takes_the_old_file_away_Async()
    {
        using PlaylistFiles files = Files(TimeSpan.FromMilliseconds(50));
        await files.StartAsync();
        string sunday = Path.Combine(Exports, "Sunday.m3u8");
        string monday = Path.Combine(Exports, "Monday.m3u8");

        long id = await PlaylistAsync("Sunday", 0);
        await EventuallyAsync(() => File.Exists(sunday) && File.ReadAllText(sunday).Contains(_tracks[0].Path, StringComparison.Ordinal), "the new playlist is exported");

        await Playlists.AddTracksAsync(id, [_tracks[1].Id]);
        await EventuallyAsync(() => File.ReadAllText(sunday).Contains(_tracks[1].Path, StringComparison.Ordinal), "an added track is exported");

        await Playlists.RenameAsync(id, "Monday");
        await EventuallyAsync(() => File.Exists(monday) && !File.Exists(sunday), "a rename writes the new name and removes the old");

        await Playlists.DeleteAsync(id);
        await EventuallyAsync(() => !File.Exists(monday), "a deleted playlist's export goes with it");
    }

    [Fact]
    public async Task Changes_inside_the_window_wait_for_it_and_a_flush_writes_them_at_once_Async()
    {
        using PlaylistFiles files = Files(TimeSpan.FromHours(1));
        await files.StartAsync();

        long id = await PlaylistAsync("Held", 0);
        await Playlists.AddTracksAsync(id, [_tracks[1].Id]);
        await Task.Delay(100);
        File.Exists(Path.Combine(Exports, "Held.m3u8")).Should().BeFalse("the window has an hour to run");

        await files.FlushAsync();

        File.ReadAllText(Path.Combine(Exports, "Held.m3u8")).Should().Contain(_tracks[1].Path);
    }

    [Fact]
    public async Task Starting_over_an_empty_library_leaves_existing_exports_alone_Async()
    {
        Directory.CreateDirectory(Exports);
        string survivor = Path.Combine(Exports, "From before the reset.m3u8");
        await File.WriteAllTextAsync(survivor, "#EXTM3U\r\n");
        using PlaylistFiles files = Files(TimeSpan.FromMilliseconds(50));

        await files.StartAsync();
        long id = await PlaylistAsync("New", 0);
        await Playlists.DeleteAsync(id);
        await Task.Delay(200);
        await files.FlushAsync();

        File.Exists(survivor).Should().BeTrue("a file this process did not write is the only copy of a playlist the reset lost");
    }

    [Fact]
    public void The_default_window_leaves_room_to_write_inside_five_seconds()
    {
        PlaylistFiles.DefaultExportWindow.Should().BeLessThan(TimeSpan.FromSeconds(5), "AC-145: every change auto-exports within 5 s");
    }

    [Fact]
    public void Two_playlists_whose_names_make_the_same_file_do_not_share_it()
    {
        PlaylistDto first = new(3, "a/b", 0, 0, false, 0, 0);
        PlaylistDto second = new(7, "a_b", 0, 0, false, 0, 0);
        PlaylistDto[] all = [second, first];

        PlaylistFiles.FileNameAmong(first, all).Should().Be("a_b.m3u8");
        PlaylistFiles.FileNameAmong(second, all).Should().Be("a_b (7).m3u8");
    }
}
