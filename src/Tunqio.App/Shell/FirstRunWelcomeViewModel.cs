using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.App.Library;
using Tunqio.App.Playback;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Shell;

/// <summary>
/// The first-run welcome (E6-S6, docs/ui-screens-and-flows.md "First-run welcome" and flow 1): three steps, add folders,
/// choose the output device, choose the theme, each skippable, with Skip all. Nothing here is its own setting: the folder
/// step is <see cref="LibrarySettingsViewModel"/>'s add, the device step is <see cref="OutputSettingsViewModel"/> and
/// the theme step is <see cref="AppearanceSettingsViewModel"/>, so a choice made here is the one the settings pages show.
/// </summary>
public sealed partial class FirstRunWelcomeViewModel : ObservableObject, IDisposable
{
    public const int FoldersStep = 0;
    public const int OutputStep = 1;
    public const int ThemeStep = 2;
    public const int StepCount = 3;

    private readonly ISettingsStore _settings;
    private readonly ILibraryFolderRepository _folders;
    private readonly ILibraryFolderPicker _picker;
    private readonly IPlaybackSessionSource _audio;
    private readonly int _previousLaunchCount;
    private readonly Stopwatch _sinceShown = new();
    private IDisposable? _snapshotSubscription;
    private int _firstSoundLogged;
    private bool _libraryAttached;

    /// <param name="settings">Where <c>ui.welcomeShown</c> is read and written.</param>
    /// <param name="folders">Read once, for the existing-profile guard.</param>
    /// <param name="library">Settings › Library's view model; its add is the folder step's add.</param>
    /// <param name="output">Settings › Output's view model, the device step.</param>
    /// <param name="appearance">Settings › Appearance's view model, the theme step.</param>
    /// <param name="picker">The system folder picker, for Add another folder.</param>
    /// <param name="audio">The session, watched for the first sound so a first run's log says when it came.</param>
    /// <param name="previousLaunchCount"><c>app.launchCount</c> as it was before this launch counted itself.</param>
    /// <param name="suggestedFolder">The folder offered pre-filled: the user's Music folder, or null where there is none.</param>
    public FirstRunWelcomeViewModel(
        ISettingsStore settings,
        ILibraryFolderRepository folders,
        LibrarySettingsViewModel library,
        OutputSettingsViewModel output,
        AppearanceSettingsViewModel appearance,
        ILibraryFolderPicker picker,
        IPlaybackSessionSource audio,
        int previousLaunchCount,
        string? suggestedFolder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(audio);
        _settings = settings;
        _folders = folders;
        Library = library;
        Output = output;
        Appearance = appearance;
        _picker = picker;
        _audio = audio;
        _previousLaunchCount = previousLaunchCount;
        FolderPath = suggestedFolder ?? string.Empty;
    }

    public LibrarySettingsViewModel Library { get; }

    public OutputSettingsViewModel Output { get; }

    public AppearanceSettingsViewModel Appearance { get; }

    /// <summary>Which step is showing, 0 to <see cref="StepCount"/> - 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(IsLastStep), nameof(NextLabel), nameof(StepCaption))]
    public partial int Step { get; set; }

    /// <summary>The folder box: the suggestion to begin with, or a path the user typed.</summary>
    [ObservableProperty]
    public partial string FolderPath { get; set; }

    /// <summary>The folders added from this welcome, in the order they were added.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextLabel), nameof(HasAddedFolders))]
    public partial IReadOnlyList<string> AddedFolders { get; set; } = [];

    /// <summary>What the last add did, or why it did nothing.</summary>
    [ObservableProperty]
    public partial string FolderNotice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasFolderNotice { get; set; }

    /// <summary>An add is between the button and the folder being stored.</summary>
    [ObservableProperty]
    public partial bool IsAdding { get; set; }

    /// <summary>Set once the welcome has closed, by Done or by Skip all.</summary>
    [ObservableProperty]
    public partial bool IsFinished { get; set; }

    public bool CanGoBack => Step > 0;

    public bool IsLastStep => Step == StepCount - 1;

    public bool HasAddedFolders => AddedFolders.Count > 0;

    /// <summary>"Step 2 of 3".</summary>
    public string StepCaption => "Step " + (Step + 1) + " of " + StepCount;

    /// <summary>The forward button: Done on the last step; on the folder step, Skip this step until a folder is added.</summary>
    public string NextLabel => IsLastStep ? "Done" : Step == FoldersStep && !HasAddedFolders ? "Skip this step" : "Next";

    /// <summary>The welcome closed.</summary>
    public event EventHandler? Finished;

    /// <summary>
    /// The once-only rule. Shown when all three hold:
    /// <list type="bullet">
    /// <item><c>ui.welcomeShown</c> is absent. Present with either value, the question was settled on an earlier launch.</item>
    /// <item>No earlier launch was counted. <c>app.launchCount</c> has been written and flushed by every launch since E0-S6,
    /// before the window exists, so its absence is a profile that has never run. That is a stronger test than "no
    /// settings.json", which is the same thing on a clean machine but not after a corrupt file was set aside (the store
    /// then starts empty on a profile with a library), and it needs no file check taken before the host starts.</item>
    /// <item>The library has no folders. The guard for a profile upgraded from before this build: Phil's has a library and
    /// a settings file but no <c>ui.welcomeShown</c>, and the key's absence alone would show him a welcome he does not
    /// need. It also covers a settings file deleted by hand beside a library that is still there.</item>
    /// </list>
    /// </summary>
    public static bool ShouldShow(ISettingsStore settings, int previousLaunchCount, int libraryFolderCount)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return !settings.Contains(SettingsKeys.UiWelcomeShown) && previousLaunchCount <= 0 && libraryFolderCount == 0;
    }

    /// <summary>
    /// Decides whether this launch shows the welcome and records the answer, so it is only ever asked once: true is
    /// written as the welcome opens, false for a profile that predates it. False when the welcome is not to be shown.
    /// </summary>
    public async Task<bool> DecideAsync(CancellationToken ct = default)
    {
        if (_settings.Contains(SettingsKeys.UiWelcomeShown))
        {
            return false;
        }

        int folderCount = (await _folders.ListAsync(ct)).Count;
        bool show = ShouldShow(_settings, _previousLaunchCount, folderCount);
        _settings.SetValue(SettingsKeys.UiWelcomeShown, show);
        await _settings.FlushAsync(ct);
        Serilog.Log.Information(
            "First-run welcome: {Decision} (previous launches {Launches}, library folders {Folders})",
            show ? "shown" : "not shown, this profile predates it", _previousLaunchCount, folderCount);
        if (show)
        {
            _sinceShown.Start();
            Library.Attach();
            _libraryAttached = true;
            WatchForFirstSound();
        }

        return show;
    }

    /// <summary>Next, or Done on the last step. Moving past a step without doing anything in it is skipping it.</summary>
    public void Next()
    {
        if (IsFinished)
        {
            return;
        }

        if (IsLastStep)
        {
            Finish("done");
            return;
        }

        Step++;
    }

    public void Back()
    {
        if (!IsFinished && Step > 0)
        {
            Step--;
        }
    }

    /// <summary>Skip all: closes the welcome where it is. Anything already added or chosen stays.</summary>
    public void SkipAll()
    {
        if (!IsFinished)
        {
            Finish("skip all on step " + (Step + 1));
        }
    }

    /// <summary>Add this folder: the path in the box.</summary>
    public Task AddFolderAsync(CancellationToken ct = default) => AddFolderAsync(FolderPath, ct);

    /// <summary>Add another folder…: the system picker, then the same add.</summary>
    public async Task PickFolderAsync(CancellationToken ct = default)
    {
        if (await _picker.PickFolderAsync(ct) is { } path)
        {
            FolderPath = path;
            await AddFolderAsync(path, ct);
        }
    }

    /// <summary>
    /// Stores <paramref name="path"/> through Settings › Library's add and returns once it is stored, with the scan it
    /// started left running in the background: the grid fills while the welcome carries on (flow 1).
    /// </summary>
    public async Task AddFolderAsync(string path, CancellationToken ct = default)
    {
        string trimmed = (path ?? string.Empty).Trim().Trim('"');
        if (trimmed.Length == 0)
        {
            SetFolderNotice("Type a folder, or choose one with Add another folder.");
            return;
        }

        if (!Directory.Exists(trimmed))
        {
            SetFolderNotice("There is no folder at " + trimmed + ".");
            return;
        }

        if (IsAdding)
        {
            return;
        }

        IsAdding = true;
        try
        {
            FolderAdded added = await Library.BeginAddFolderAsync(trimmed, ct);
            if (!AddedFolders.Contains(added.Folder.Path, StringComparer.OrdinalIgnoreCase))
            {
                AddedFolders = [.. AddedFolders, added.Folder.Path];
            }

            SetFolderNotice("Added " + added.Folder.Path + ". Scanning it now; albums appear as they are found.");
            Serilog.Log.Information("First-run welcome added {Folder}; its scan has started", added.Folder.Path);
            LogScanAsync(added).Forget("First-run scan");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetFolderNotice("That folder could not be added: " + e.Message);
            Serilog.Log.Warning(e, "First-run welcome could not add {Folder}", trimmed);
        }
        finally
        {
            IsAdding = false;
        }
    }

    public void Dispose()
    {
        _audio.SessionReady -= OnSessionReady;
        Interlocked.Exchange(ref _snapshotSubscription, null)?.Dispose();
        DetachLibrary();
    }

    partial void OnStepChanged(int value)
    {
        if (value == OutputStep)
        {
            // Audio starts after the first frame, so the list is read when the step is reached rather than when the
            // welcome opened, by which time the session is normally up.
            Output.Load();
        }
    }

    private void Finish(string how)
    {
        IsFinished = true;
        _settings.SetValue(SettingsKeys.UiWelcomeShown, true);
        _settings.FlushAsync().Forget("Save first-run welcome");
        Serilog.Log.Information(
            "First-run welcome closed ({How}) after {Seconds:F1} s with {Folders} folder(s) added",
            how, _sinceShown.Elapsed.TotalSeconds, AddedFolders.Count);
        DetachLibrary();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void DetachLibrary()
    {
        if (_libraryAttached)
        {
            _libraryAttached = false;
            Library.Detach();
        }
    }

    private static async Task LogScanAsync(FolderAdded added)
    {
        ScanReport? report = await added.Scan;
        Serilog.Log.Information(
            "First-run scan of {Folder} ended: {Report}",
            added.Folder.Path, report is null ? "no report" : LibraryScanCoordinator.Describe(report));
    }

    private void SetFolderNotice(string text)
    {
        FolderNotice = text;
        HasFolderNotice = text.Length > 0;
    }

    private void WatchForFirstSound()
    {
        if (_audio.Session is { } session)
        {
            Subscribe(session);
        }
        else
        {
            _audio.SessionReady += OnSessionReady;
        }
    }

    private void OnSessionReady(object? sender, PlaybackSession session)
    {
        _audio.SessionReady -= OnSessionReady;
        Subscribe(session);
    }

    private void Subscribe(PlaybackSession session) =>
        Interlocked.Exchange(ref _snapshotSubscription, session.Snapshots.Subscribe(new FirstSoundObserver(this)))?.Dispose();

    /// <summary>
    /// Flow 1 ends at the first sound. Logged once, on the session's timer thread, so a harness reads it from the log after
    /// the app exits; nothing on screen depends on it.
    /// </summary>
    private void OnSnapshot(PlaybackSnapshot snapshot)
    {
        if (snapshot.State != PlaybackState.Playing || snapshot.Current is null || Interlocked.Exchange(ref _firstSoundLogged, 1) != 0)
        {
            return;
        }

        Serilog.Log.Information(
            "First run: first sound, track {TrackId} ({Title}) playing {Seconds:F1} s after the welcome opened",
            snapshot.Current.TrackId, snapshot.Track?.Title ?? "untitled", _sinceShown.Elapsed.TotalSeconds);
    }

    private sealed class FirstSoundObserver(FirstRunWelcomeViewModel owner) : IObserver<PlaybackSnapshot>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(PlaybackSnapshot value) => owner.OnSnapshot(value);
    }
}
