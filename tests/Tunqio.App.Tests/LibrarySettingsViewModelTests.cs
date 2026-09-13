using Tunqio.App.Library;
using Tunqio.Core;
using Tunqio.Core.Library;

namespace Tunqio.App.Tests;

/// <summary>E3-S12: Settings › Library over fakes for the repositories, watcher, scanner, picker and art cache.</summary>
public sealed class LibrarySettingsViewModelTests : IDisposable
{
    private const long Now = 1_757_376_000_000; // 2025-09-09T00:00:00Z

    private readonly FakeFolderRepository _folders = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeSearchService _search = new();
    private readonly FakeWatcher _watcher = new();
    private readonly FakeScanner _scanner = new();
    private readonly FakeSettings _settings = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly FakeArtCache _art = new();
    private readonly LibraryScanCoordinator _scans;
    private readonly List<string> _libraryChanges = [];

    public LibrarySettingsViewModelTests()
    {
        _scans = new LibraryScanCoordinator(_scanner, new FixedClock(Now));
        _scans.LibraryChanged += (_, _) => _libraryChanges.Add("changed");
    }

    public void Dispose() => _scans.Dispose();

    private LibrarySettingsViewModel Create(bool withArt = true) =>
        new(_folders, _tracks, _search, _watcher, _scans, _settings, _picker, withArt ? _art : null, new FixedClock(Now));

    // ---- E5-S5, Q-74: the hover preview switch --------------------------------------------------------------------

    [Fact]
    public void The_hover_preview_switch_starts_from_the_stored_setting_and_is_off_by_default()
    {
        Create().HoverPreview.Should().BeFalse();

        _settings.SetValue(Tunqio.Core.SettingsKeys.UiHoverPreview, true);
        Create().HoverPreview.Should().BeTrue();
    }

    [Fact]
    public void Flipping_the_switch_writes_the_setting_and_retires_the_first_hover_offer()
    {
        LibrarySettingsViewModel vm = Create();

        vm.HoverPreview = true;

        _settings.GetValue(Tunqio.Core.SettingsKeys.UiHoverPreview, false).Should().BeTrue();
        _settings.GetValue(Tunqio.Core.SettingsKeys.UiHoverPreviewOffered, false).Should().BeTrue(
            "a choice made on the settings page is a choice made; the offer would only ask it again");
    }

    [Fact]
    public void Previews_turned_on_from_the_offer_show_on_the_switch_when_the_page_comes_back()
    {
        LibrarySettingsViewModel vm = Create();
        vm.Attach();
        vm.Detach();

        _settings.SetValue(Tunqio.Core.SettingsKeys.UiHoverPreview, true); // the notice bar's Turn on, while the page was away
        vm.Attach();

        vm.HoverPreview.Should().BeTrue();
        vm.Detach();
    }

    [Fact]
    public void Seeding_the_switch_from_settings_writes_nothing_back()
    {
        _settings.SetValue(Tunqio.Core.SettingsKeys.UiHoverPreview, true);

        _ = Create();

        _settings.Contains(Tunqio.Core.SettingsKeys.UiHoverPreviewOffered).Should().BeFalse(
            "reading the setting is not the user choosing it");
    }

    [Fact]
    public async Task Load_lists_folders_with_their_last_scan_and_counts_what_purge_would_remove_Async()
    {
        _folders.Rows.Add(new LibraryFolderDto(1, @"D:\Music\", true, Now - 5 * 60_000, "ok"));
        _folders.Rows.Add(new LibraryFolderDto(2, @"E:\More\", false, null, null));
        _folders.Rows.Add(new LibraryFolderDto(3, @"F:\", true, null, null));
        _tracks.MissingOlderThanCutoff = 4;
        LibrarySettingsViewModel vm = Create();

        await vm.LoadAsync();

        vm.HasFolders.Should().BeTrue();
        vm.FolderRows.Select(r => (r.Name, r.Enabled, r.LastScan)).Should().Equal(
            ("Music", true, "Scanned 5 min ago · ok"),
            ("More", false, "Disabled"),
            (@"F:", true, "Never scanned"));
        vm.MissingCount.Should().Be(4);
        vm.CanPurge.Should().BeTrue();
        LibrarySettingsViewModel.PurgeCutoff(DateTimeOffset.FromUnixTimeMilliseconds(Now)).Should().Be(Now - 30L * 24 * 3_600_000);
    }

    [Fact]
    public async Task Add_folder_stores_the_pick_refreshes_the_watcher_and_scans_that_folder_Async()
    {
        LibrarySettingsViewModel vm = Create();
        await vm.LoadAsync();
        vm.HasFolders.Should().BeFalse();

        _picker.NextPick = null;
        await vm.AddFolderAsync();
        _folders.Rows.Should().BeEmpty("the user cancelled the picker");
        _watcher.Refreshes.Should().Be(0);

        _picker.NextPick = @"D:\Music";
        Task add = vm.AddFolderAsync();
        await LibraryScanCoordinatorTests.WaitUntilAsync(() => _scanner.Requests.Count == 1);
        _folders.Rows.Should().ContainSingle(r => r.Path == @"D:\Music");
        _watcher.Refreshes.Should().Be(1, "the new folder gets a watch before its scan");
        vm.HasFolders.Should().BeTrue();
        vm.Notice.Should().Be(@"Added D:\Music; scanning it now.");
        _scanner.Requests[0].Should().BeEquivalentTo(ScanRequest.Folder(1));
        vm.IsScanning.Should().BeFalse("the view model follows the coordinator only while attached");

        vm.Attach();
        vm.IsScanning.Should().BeTrue();
        vm.ScanStatus.Should().Be("Starting scan…");
        _scanner.ReportProgress(new ScanProgress(ScanPhase.Enumerating, 10, 0, 0, 0, 0, 0, 0, null));
        vm.ScanStatus.Should().Be("Looking for files · 10 files seen");

        _scanner.Finish(FakeScanner.Report(added: 10, failed: 2, failures: [new ScanFailure(@"D:\Music\bad.mp3", TagReadOutcome.CorruptTags, null), new ScanFailure(@"D:\Music\slow.wav", TagReadOutcome.TimedOut, null)]));
        await add;
        vm.IsScanning.Should().BeFalse();
        vm.HasLastReport.Should().BeTrue();
        vm.LastReport.Should().Be("Completed in 1.5 s · 12 files · 10 added · 2 failed");
        vm.LastReportWhen.Should().Be("Last scan just now");
        vm.Failures.Should().Equal(@"D:\Music\bad.mp3 — corrupt tags", @"D:\Music\slow.wav — tag read timed out");
        _libraryChanges.Should().HaveCount(1, "the scan added rows");
    }

    [Fact]
    public async Task Remove_and_enable_go_through_the_repository_and_the_watcher_Async()
    {
        _folders.Rows.Add(new LibraryFolderDto(1, @"D:\Music\", true, null, null));
        _folders.Rows.Add(new LibraryFolderDto(2, @"E:\More\", true, null, null));
        LibrarySettingsViewModel vm = Create();
        await vm.LoadAsync();

        await vm.SetEnabledAsync(vm.FolderRows[1], false);
        _folders.Rows[1].Enabled.Should().BeFalse();
        _watcher.Refreshes.Should().Be(1);
        vm.FolderRows[1].LastScan.Should().Be("Disabled");
        await vm.SetEnabledAsync(vm.FolderRows[1], false);
        _watcher.Refreshes.Should().Be(1, "no change, no work");

        await vm.RemoveFolderAsync(vm.FolderRows[0]);
        _folders.Rows.Select(r => r.Id).Should().Equal(2);
        _watcher.Refreshes.Should().Be(2);
        _libraryChanges.Should().HaveCount(1, "the rows went with the folder, without a scan");
        vm.Notice.Should().Be(@"Removed D:\Music\ and its tracks from the library.");
        vm.FolderRows.Should().HaveCount(1);
    }

    [Fact]
    public async Task Rescan_forces_a_reread_of_one_folder_or_all_and_cancel_stops_it_Async()
    {
        _folders.Rows.Add(new LibraryFolderDto(7, @"D:\Music\", true, null, null));
        LibrarySettingsViewModel vm = Create();
        await vm.LoadAsync();

        Task one = vm.RescanFolderAsync(vm.FolderRows[0]);
        await LibraryScanCoordinatorTests.WaitUntilAsync(() => _scanner.Requests.Count == 1);
        _scanner.Requests[0].Should().BeEquivalentTo(new ScanRequest([7], ForceReread: true));
        vm.CancelScan();
        await one;

        Task all = vm.RescanAllAsync();
        await LibraryScanCoordinatorTests.WaitUntilAsync(() => _scanner.Requests.Count == 2);
        _scanner.Requests[1].Should().Be(new ScanRequest(ForceReread: true));
        _scanner.Finish(FakeScanner.Report(unchanged: 3));
        await all;
    }

    [Fact]
    public void The_toggles_persist_to_settings()
    {
        _settings.SetValue(SettingsKeys.LibrarySplitArtists, false);
        LibrarySettingsViewModel vm = Create();
        vm.SplitArtists.Should().BeFalse("read from settings");
        vm.WriteRatingsToFiles.Should().BeFalse("the default");

        vm.SplitArtists = true;
        vm.WriteRatingsToFiles = true;

        _settings.GetValue(SettingsKeys.LibrarySplitArtists, false).Should().BeTrue();
        _settings.GetValue(SettingsKeys.LibraryWriteRatingsToFiles, false).Should().BeTrue();
    }

    [Fact]
    public async Task Purge_deletes_at_the_cutoff_and_says_what_it_did_Async()
    {
        _tracks.MissingOlderThanCutoff = 12;
        LibrarySettingsViewModel vm = Create();
        await vm.LoadAsync();

        await vm.PurgeMissingAsync();

        _tracks.PurgeCutoffs.Should().Equal(Now - 30L * 24 * 3_600_000);
        vm.Notice.Should().Be("Purged 12 tracks missing for over 30 days.");
        vm.MissingCount.Should().Be(0, "reloaded after the purge");
        vm.CanPurge.Should().BeFalse();
        vm.IsBusy.Should().BeFalse();
        _libraryChanges.Should().HaveCount(1);

        await vm.PurgeMissingAsync();
        vm.Notice.Should().Be("No tracks have been missing for over 30 days.");
        _libraryChanges.Should().HaveCount(1, "nothing changed the second time");
    }

    [Fact]
    public async Task Rebuild_index_and_regenerate_art_report_and_regenerate_rescans_everything_Async()
    {
        LibrarySettingsViewModel vm = Create();
        vm.CanRegenerateArt.Should().BeTrue();
        Create(withArt: false).CanRegenerateArt.Should().BeFalse();

        await vm.RebuildIndexAsync();
        _search.Rebuilds.Should().Be(1);
        vm.Notice.Should().Be("Search index rebuilt over 0 tracks.");

        Task regenerate = vm.RegenerateArtAsync();
        await LibraryScanCoordinatorTests.WaitUntilAsync(() => _scanner.Requests.Count == 1);
        _art.Clears.Should().Be(1);
        vm.Notice.Should().Be("Art cache cleared; rescanning to render it again.");
        _scanner.Requests[0].Should().Be(new ScanRequest(ForceReread: true));
        _scanner.Finish(FakeScanner.Report(updated: 40));
        await regenerate;
    }

    [Fact]
    public async Task Detach_stops_following_the_coordinator_Async()
    {
        LibrarySettingsViewModel vm = Create();
        vm.Attach();
        vm.Attach(); // idempotent
        vm.Detach();

        Task scan = _scans.ScanAsync(ScanRequest.All);
        await LibraryScanCoordinatorTests.WaitUntilAsync(() => _scanner.Requests.Count == 1);
        vm.IsScanning.Should().BeFalse();
        _scanner.Finish(FakeScanner.Report());
        await scan;
        vm.HasLastReport.Should().BeFalse();

        vm.Attach();
        vm.HasLastReport.Should().BeTrue("attaching reads the state it missed");
    }

    [Fact]
    public void Ago_reads_naturally()
    {
        LibrarySettingsViewModel.Ago(TimeSpan.FromSeconds(30)).Should().Be("just now");
        LibrarySettingsViewModel.Ago(TimeSpan.FromMinutes(7)).Should().Be("7 min ago");
        LibrarySettingsViewModel.Ago(TimeSpan.FromHours(3.5)).Should().Be("3 h ago");
        LibrarySettingsViewModel.Ago(TimeSpan.FromDays(1.2)).Should().Be("yesterday");
        LibrarySettingsViewModel.Ago(TimeSpan.FromDays(9)).Should().Be("9 days ago");
    }
}
