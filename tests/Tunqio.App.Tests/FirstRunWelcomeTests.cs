using Tunqio.App.Library;
using Tunqio.App.Shell;
using Tunqio.Core;

namespace Tunqio.App.Tests;

/// <summary>
/// E6-S6: the first-run welcome over fakes. The once-only rule and its existing-profile guard, the three steps with Next,
/// Back and Skip all, a folder added through Settings › Library's own add with its scan left running, and the device and
/// theme steps being the Output and Appearance pages' view models.
/// </summary>
public sealed class FirstRunWelcomeTests : IDisposable
{
    private const long Now = 1_757_376_000_000;

    private readonly FakeFolderRepository _folders = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeWatcher _watcher = new();
    private readonly FakeScanner _scanner = new();
    private readonly FakeSettings _settings = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly StubSessionSource _audio = new();
    private readonly LibraryScanCoordinator _scans;
    private readonly string _music = Path.Combine(Path.GetTempPath(), "tunqio-welcome-" + Guid.NewGuid().ToString("N"));
    private readonly List<FirstRunWelcomeViewModel> _created = [];

    public FirstRunWelcomeTests()
    {
        _scans = new LibraryScanCoordinator(_scanner, new FixedClock(Now));
        Directory.CreateDirectory(_music);
    }

    public void Dispose()
    {
        _scanner.Finish(FakeScanner.Report());
        foreach (FirstRunWelcomeViewModel vm in _created)
        {
            vm.Dispose();
        }

        _scans.Dispose();
        Directory.Delete(_music, recursive: true);
    }

    private FirstRunWelcomeViewModel Create(int previousLaunchCount = 0, string? suggested = null)
    {
        var library = new LibrarySettingsViewModel(
            _folders, _tracks, new FakeSearchService(), _watcher, _scans, _settings, _picker,
            new FakePlaylistFiles(), new FakePlaylistFilePicker(), null, new FixedClock(Now));
        var output = new OutputSettingsViewModel(_audio, _settings, _music);
        var appearance = new AppearanceSettingsViewModel(_settings);
        var vm = new FirstRunWelcomeViewModel(
            _settings, _folders, library, output, appearance, _picker, _audio, previousLaunchCount, suggested ?? _music);
        _created.Add(vm);
        return vm;
    }

    // ---- the once-only rule ----------------------------------------------------------------------------------------

    [Fact]
    public void A_fresh_profile_is_shown_the_welcome()
    {
        FirstRunWelcomeViewModel.ShouldShow(_settings, previousLaunchCount: 0, libraryFolderCount: 0).Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_profile_that_has_settled_the_welcome_is_never_shown_it_again(bool recorded)
    {
        _settings.SetValue(SettingsKeys.UiWelcomeShown, recorded);

        FirstRunWelcomeViewModel.ShouldShow(_settings, 0, 0).Should().BeFalse("the key's presence, not its value, settles it");
    }

    [Fact]
    public void An_existing_profile_upgraded_to_this_build_is_not_shown_the_welcome_though_the_key_is_absent()
    {
        // Phil's profile: launched many times before E6-S6 existed, so app.launchCount is there and ui.welcomeShown is not.
        _settings.SetValue(SettingsKeys.AppLaunchCount, 212);

        FirstRunWelcomeViewModel.ShouldShow(_settings, previousLaunchCount: 212, libraryFolderCount: 1).Should().BeFalse();
        FirstRunWelcomeViewModel.ShouldShow(_settings, previousLaunchCount: 212, libraryFolderCount: 0).Should().BeFalse(
            "a counted launch is an existing profile even with no library");
    }

    [Fact]
    public void A_library_with_folders_is_not_shown_the_welcome_even_when_its_settings_file_is_gone()
    {
        // settings.json deleted by hand, or set aside as corrupt: the store starts empty but library.db still has folders.
        FirstRunWelcomeViewModel.ShouldShow(_settings, previousLaunchCount: 0, libraryFolderCount: 2).Should().BeFalse();
    }

    [Fact]
    public async Task Deciding_on_a_fresh_profile_shows_it_once_and_records_it_so_the_next_launch_does_not()
    {
        (await Create().DecideAsync()).Should().BeTrue();
        _settings.GetValue<bool?>(SettingsKeys.UiWelcomeShown, null).Should().BeTrue();

        (await Create(previousLaunchCount: 1).DecideAsync()).Should().BeFalse();
        (await Create(previousLaunchCount: 0).DecideAsync()).Should().BeFalse("the recorded key settles it whatever the count says");
    }

    [Fact]
    public async Task Deciding_on_an_existing_profile_records_that_it_was_not_shown()
    {
        await _folders.AddAsync(@"D:\Music\");

        (await Create(previousLaunchCount: 0).DecideAsync()).Should().BeFalse();

        _settings.GetValue<bool?>(SettingsKeys.UiWelcomeShown, null).Should().BeFalse();
        _settings.Contains(SettingsKeys.UiWelcomeShown).Should().BeTrue("the question is asked once, whichever way it went");
    }

    // ---- the steps ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Three_steps_go_forward_and_back_and_Done_on_the_last_closes_it()
    {
        FirstRunWelcomeViewModel vm = Create();
        await vm.DecideAsync();
        int finished = 0;
        vm.Finished += (_, _) => finished++;

        vm.Step.Should().Be(FirstRunWelcomeViewModel.FoldersStep);
        vm.CanGoBack.Should().BeFalse();
        vm.NextLabel.Should().Be("Skip this step", "nothing has been added, so moving on skips the step");
        vm.StepCaption.Should().Be("Step 1 of 3");

        vm.Next();
        vm.Step.Should().Be(FirstRunWelcomeViewModel.OutputStep);
        vm.CanGoBack.Should().BeTrue();
        vm.NextLabel.Should().Be("Next");

        vm.Back();
        vm.Step.Should().Be(FirstRunWelcomeViewModel.FoldersStep);

        vm.Next();
        vm.Next();
        vm.Step.Should().Be(FirstRunWelcomeViewModel.ThemeStep);
        vm.IsLastStep.Should().BeTrue();
        vm.NextLabel.Should().Be("Done");
        finished.Should().Be(0);

        vm.Next();
        vm.IsFinished.Should().BeTrue();
        finished.Should().Be(1);
        _settings.GetValue(SettingsKeys.UiWelcomeShown, false).Should().BeTrue();
    }

    [Fact]
    public async Task Skip_all_closes_it_from_any_step_and_nothing_moves_afterwards()
    {
        FirstRunWelcomeViewModel vm = Create();
        await vm.DecideAsync();
        int finished = 0;
        vm.Finished += (_, _) => finished++;
        vm.Next();

        vm.SkipAll();
        vm.SkipAll();
        vm.Next();

        vm.IsFinished.Should().BeTrue();
        finished.Should().Be(1, "Skip all and the dialog's closing both call it, and it closes once");
        vm.Step.Should().Be(FirstRunWelcomeViewModel.OutputStep);
        _settings.GetValue(SettingsKeys.UiWelcomeShown, false).Should().BeTrue();
    }

    [Fact]
    public void The_Music_folder_is_offered_pre_filled()
    {
        string music = Path.Combine(_music, "Music");

        Create(suggested: music).FolderPath.Should().Be(music);
    }

    // ---- the folder step ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Adding_a_folder_stores_it_through_the_library_page_and_returns_with_the_scan_still_running()
    {
        FirstRunWelcomeViewModel vm = Create();
        await vm.DecideAsync();

        await vm.AddFolderAsync();

        _folders.Rows.Select(r => r.Path).Should().Equal(_music);
        _watcher.Refreshes.Should().Be(1, "Settings › Library's add refreshes the watcher");
        await WaitForAsync(() => _scanner.Requests.Count == 1);
        _scanner.IsScanning.Should().BeTrue("the add did not wait for the scan: the grid fills behind the dialog");
        vm.AddedFolders.Should().Equal(_music);
        vm.NextLabel.Should().Be("Next");
        vm.FolderNotice.Should().StartWith("Added ");
        vm.Library.FolderRows.Should().ContainSingle("it is the page's own view model that was told");

        _scanner.Finish(FakeScanner.Report(added: 3));
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_is_refused_with_a_notice_and_nothing_is_stored()
    {
        FirstRunWelcomeViewModel vm = Create();
        await vm.DecideAsync();

        await vm.AddFolderAsync(Path.Combine(_music, "not-there"));

        _folders.Rows.Should().BeEmpty();
        _scanner.Requests.Should().BeEmpty();
        vm.HasFolderNotice.Should().BeTrue();
        vm.FolderNotice.Should().StartWith("There is no folder at ");
    }

    [Fact]
    public async Task Add_another_folder_goes_through_the_picker_and_a_cancelled_pick_adds_nothing()
    {
        FirstRunWelcomeViewModel vm = Create(suggested: string.Empty);
        await vm.DecideAsync();

        _picker.NextPick = null;
        await vm.PickFolderAsync();
        _folders.Rows.Should().BeEmpty();

        _picker.NextPick = _music;
        await vm.PickFolderAsync();
        _folders.Rows.Select(r => r.Path).Should().Equal(_music);
        vm.FolderPath.Should().Be(_music);
    }

    // ---- the device and theme steps ----------------------------------------------------------------------------------

    [Fact]
    public async Task The_device_step_is_the_output_page_view_model_loaded_when_the_step_is_reached()
    {
        FirstRunWelcomeViewModel vm = Create();
        await vm.DecideAsync();
        vm.Output.Devices.Should().BeEmpty();

        vm.Next();

        vm.Output.Devices.Should().ContainSingle().Which.Name.Should().Be(OutputSettingsViewModel.DefaultDeviceName);
        vm.Output.HasAudio.Should().BeFalse("no session in this test, so the step says audio is not available");
    }

    [Fact]
    public async Task Choosing_a_theme_on_the_last_step_writes_ui_theme_as_the_appearance_page_does()
    {
        FirstRunWelcomeViewModel vm = Create();
        await vm.DecideAsync();
        vm.Next();
        vm.Next();
        string? changed = null;
        _settings.Changed += (_, key) => changed = key;

        vm.Appearance.ThemeIndex = AppearanceSettingsViewModel.Themes.ToList().IndexOf(ThemePreference.Dark);

        changed.Should().Be(SettingsKeys.UiTheme, "the window repaints on this key, so the choice is live");
        ThemePolicy.Read(_settings).Should().Be(ThemePreference.Dark);
        new AppearanceSettingsViewModel(_settings).ThemeIndex.Should().Be(vm.Appearance.ThemeIndex, "the settings page shows the same choice");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        condition().Should().BeTrue();
    }
}
