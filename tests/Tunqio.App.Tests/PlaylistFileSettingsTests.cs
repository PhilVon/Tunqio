using Tunqio.App.Library;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

/// <summary>
/// E6-S2 (AC-201): Settings › Library's playlist actions, over fakes, and the one-line notices every export and import ends
/// in.
/// </summary>
public sealed class PlaylistFileSettingsTests : IDisposable
{
    private const long Now = 1_757_376_000_000;

    private readonly FakeFolderPicker _folderPicker = new();
    private readonly FakePlaylistFiles _files = new();
    private readonly FakePlaylistFilePicker _filePicker = new();
    private readonly LibraryScanCoordinator _scans = new(new FakeScanner(), new FixedClock(Now));

    public void Dispose() => _scans.Dispose();

    private LibrarySettingsViewModel Create() => new(
        new FakeFolderRepository(), new FakeTrackRepository(), new FakeSearchService(), new FakeWatcher(), _scans, new FakeSettings(),
        _folderPicker, _files, _filePicker, art: null, new FixedClock(Now));

    private static PlaylistImportResult Created(string name, int matched, int unmatched = 0) =>
        new(@"C:\x\" + name + ".m3u8", name, new PlaylistDto(1, name, 0, 0, false, matched, 0), matched, unmatched, AlreadyExists: false);

    [Fact]
    public async Task Import_from_exports_says_what_it_restored_Async()
    {
        _files.ImportResults["a"] = Created("Sunday", 12);
        _files.ImportResults["b"] = new PlaylistImportResult("b", "Monday", null, 0, 0, AlreadyExists: true);
        LibrarySettingsViewModel vm = Create();

        await vm.ImportExportsAsync();

        vm.Notice.Should().Be("Imported Sunday (12 tracks); 1 already in the library.");
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Import_playlist_files_imports_each_picked_file_and_a_cancelled_picker_does_nothing_Async()
    {
        LibrarySettingsViewModel vm = Create();

        await vm.ImportFilesAsync();
        vm.HasNotice.Should().BeFalse();

        _filePicker.OpenAnswer = [@"D:\a.m3u8", @"D:\b.m3u"];
        await vm.ImportFilesAsync();

        vm.Notice.Should().Be("Imported 2 playlists (2 tracks).");
    }

    [Fact]
    public async Task Export_playlists_asks_for_a_folder_first_Async()
    {
        LibrarySettingsViewModel vm = Create();

        await vm.ExportPlaylistsAsync();
        vm.HasNotice.Should().BeFalse();

        _folderPicker.NextPick = @"D:\Music\Lists";
        await vm.ExportPlaylistsAsync();

        vm.Notice.Should().Be("There are no playlists to export.");
    }

    [Fact]
    public void An_import_notice_names_files_with_nothing_in_the_library_and_the_tracks_that_were_missing()
    {
        string text = PlaylistFileText.Imported(
        [
            Created("One", 5, unmatched: 2),
            Created("Two", 3, unmatched: 1),
            new PlaylistImportResult("c", "Three", null, 0, 4, AlreadyExists: false),
        ]);

        text.Should().Be("Imported 2 playlists (8 tracks); 3 tracks not in the library; 1 file with none of its tracks in the library (rescan, then import again).");
    }

    [Fact]
    public void An_import_of_nothing_says_so()
    {
        PlaylistFileText.Imported([]).Should().Be("No playlist files were found.");
        PlaylistFileText.Imported([new PlaylistImportResult("c", "Three", null, 0, 1, false)])
            .Should().Be("1 file with none of its tracks in the library (rescan, then import again).");
    }

    [Fact]
    public void An_export_notice_names_the_file_and_the_track_count()
    {
        PlaylistFileText.Exported(new PlaylistExportResult(@"D:\Music\Sunday.m3u8", 1)).Should().Be(@"Exported 1 track to D:\Music\Sunday.m3u8.");
        PlaylistFileText.ExportedAll([new(@"D:\a.m3u8", 1), new(@"D:\b.m3u8", 2)], @"D:\").Should().Be(@"Exported 2 playlists to D:\.");
    }
}
