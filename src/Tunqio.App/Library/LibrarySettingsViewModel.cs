using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.Core;
using Tunqio.Core.Library;

namespace Tunqio.App.Library;

/// <summary>One library folder on the settings page.</summary>
/// <param name="LastScan">"Scanned 5 min ago · ok", "Never scanned", or "Disabled".</param>
public sealed record LibraryFolderRow(LibraryFolderDto Folder, string Name, string Path, bool Enabled, string LastScan);

/// <summary>A folder that has just been stored, and the scan of it that was started (still running when this is returned).</summary>
public sealed record FolderAdded(LibraryFolderDto Folder, Task<ScanReport?> Scan);

/// <summary>
/// Settings › Library (docs/ui-screens-and-flows.md; E3-S12): the folder list with add, remove, enable and
/// rescan; the running scan's status and the last report; the split-artists and write-ratings toggles; purge
/// missing, rebuild the search index and regenerate art. Adding a folder refreshes the watcher and scans the
/// folder; removing one refreshes the watcher and tells the coordinator the library changed (the rows went
/// with the folder). Every action that changes rows ends in a <see cref="Notice"/> saying what it did.
/// </summary>
public sealed partial class LibrarySettingsViewModel : ObservableObject
{
    /// <summary>A missing file is purged once it has been away this long (docs/library-and-data.md, "Scanner", Diff stage).</summary>
    public static readonly TimeSpan PurgeAge = TimeSpan.FromDays(30);

    /// <summary>Failures listed on the page; the log has the rest.</summary>
    public const int FailuresShown = 50;

    private readonly ILibraryFolderRepository _folders;
    private readonly ITrackRepository _tracks;
    private readonly ISearchService _search;
    private readonly IArtCache? _art;
    private readonly ILibraryWatcher _watcher;
    private readonly LibraryScanCoordinator _scans;
    private readonly ISettingsStore _settings;
    private readonly ILibraryFolderPicker _picker;
    private readonly IPlaylistFiles _playlistFiles;
    private readonly IPlaylistFilePicker _playlistPicker;
    private readonly TimeProvider _clock;
    private bool _attached;

    // Set while the constructor seeds state read out of settings. The seed has to go through the property now
    // that these are partial properties (there is no backing field to assign around the setter), so the change
    // handlers below use this to avoid writing a value straight back to the store it just came from.
    private bool _seeding;

    [ObservableProperty]
    public partial IReadOnlyList<LibraryFolderRow> FolderRows { get; set; } = [];

    [ObservableProperty]
    public partial bool HasFolders { get; set; }

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    /// <summary>The running scan, one line; empty when none runs.</summary>
    [ObservableProperty]
    public partial string ScanStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasLastReport { get; set; }

    /// <summary>The last shell scan's outcome and counts, one line.</summary>
    [ObservableProperty]
    public partial string LastReport { get; set; } = string.Empty;

    /// <summary>"Today at 14:02" for the last report.</summary>
    [ObservableProperty]
    public partial string LastReportWhen { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<string> Failures { get; set; } = [];

    [ObservableProperty]
    public partial bool HasFailures { get; set; }

    [ObservableProperty]
    public partial bool SplitArtists { get; set; }

    [ObservableProperty]
    public partial bool WriteRatingsToFiles { get; set; }

    /// <summary><c>ui.hoverPreview</c> (E5-S5): whether resting the pointer on an album in Discovery previews it.</summary>
    [ObservableProperty]
    public partial bool HoverPreview { get; set; }

    /// <summary>Tracks Purge missing would delete now.</summary>
    [ObservableProperty]
    public partial int MissingCount { get; set; }

    [ObservableProperty]
    public partial bool CanPurge { get; set; }

    /// <summary>A maintenance action (purge, rebuild, regenerate) is running; the buttons wait.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>What the last action did ("Purged 12 missing tracks"); empty when nothing has happened yet.</summary>
    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNotice { get; set; }

    public LibrarySettingsViewModel(
        ILibraryFolderRepository folders,
        ITrackRepository tracks,
        ISearchService search,
        ILibraryWatcher watcher,
        LibraryScanCoordinator scans,
        ISettingsStore settings,
        ILibraryFolderPicker picker,
        IPlaylistFiles playlistFiles,
        IPlaylistFilePicker playlistPicker,
        IArtCache? art,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(scans);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(playlistFiles);
        ArgumentNullException.ThrowIfNull(playlistPicker);
        _folders = folders;
        _tracks = tracks;
        _search = search;
        _watcher = watcher;
        _scans = scans;
        _settings = settings;
        _picker = picker;
        _playlistFiles = playlistFiles;
        _playlistPicker = playlistPicker;
        _art = art;
        _clock = clock ?? TimeProvider.System;
        _seeding = true;
        SplitArtists = settings.GetValue(SettingsKeys.LibrarySplitArtists, SettingsKeys.Defaults.LibrarySplitArtists);
        WriteRatingsToFiles = settings.GetValue(SettingsKeys.LibraryWriteRatingsToFiles, SettingsKeys.Defaults.LibraryWriteRatingsToFiles);
        HoverPreview = settings.GetValue(SettingsKeys.UiHoverPreview, SettingsKeys.Defaults.UiHoverPreview);
        _seeding = false;
        ReadScanState();
    }

    /// <summary>Regenerate art is offered only when the host registered a cache.</summary>
    public bool CanRegenerateArt => _art is not null;

    /// <summary>Follows the coordinator while the page is showing; <see cref="Detach"/> when it is not.</summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _scans.StateChanged += OnScanStateChanged;
        _scans.LibraryChanged += OnLibraryChanged;
        _settings.Changed += OnSettingChanged;
        ReadScanState();
        ReadHoverPreview(); // the first-hover offer may have turned it on while this cached page was away
    }

    public void Detach()
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        _scans.StateChanged -= OnScanStateChanged;
        _scans.LibraryChanged -= OnLibraryChanged;
        _settings.Changed -= OnSettingChanged;
    }

    /// <summary>The folder list and the missing count.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        IReadOnlyList<LibraryFolderDto> folders = await _folders.ListAsync(ct);
        DateTimeOffset now = _clock.GetUtcNow();
        FolderRows = folders.Select(f => new LibraryFolderRow(f, FoldersViewModel.NameOf(f.Path), f.Path, f.Enabled, DescribeLastScan(f, now))).ToArray();
        HasFolders = FolderRows.Count > 0;
        MissingCount = await _tracks.CountMissingAsync(PurgeCutoff(now), ct);
        CanPurge = MissingCount > 0 && !IsBusy;
    }

    /// <summary>Add folder: the picker, the row (idempotent for a folder already listed), the watcher, then a scan of that folder.</summary>
    public async Task AddFolderAsync(CancellationToken ct = default)
    {
        string? path = await _picker.PickFolderAsync(ct);
        if (path is null)
        {
            return;
        }

        await AddFolderAsync(path, ct);
    }

    /// <summary>Add a folder by path: the row, the watcher, then a scan of that folder, which this waits for.</summary>
    public async Task AddFolderAsync(string path, CancellationToken ct = default)
    {
        FolderAdded added = await BeginAddFolderAsync(path, ct);
#pragma warning disable VSTHRD003 // Started by BeginAddFolderAsync just above, on this context.
        await added.Scan;
#pragma warning restore VSTHRD003
    }

    /// <summary>
    /// The same add as <see cref="AddFolderAsync(string, CancellationToken)"/>, returning as soon as the folder is stored
    /// and the watcher refreshed, with the scan it started still running. The first-run welcome (E6-S6) uses it so the
    /// dialog can move on while the Albums grid fills behind it (flow 1).
    /// </summary>
    public async Task<FolderAdded> BeginAddFolderAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        LibraryFolderDto folder = await _folders.AddAsync(path, ct);
        await _watcher.RefreshAsync(ct);
        await LoadAsync(ct);
        SetNotice("Added " + folder.Path + "; scanning it now.");
        return new FolderAdded(folder, _scans.ScanAsync(ScanRequest.Folder(folder.Id), ct));
    }

    /// <summary>Remove: the folder and every track under it (with their play history and playlist entries). The page confirms first.</summary>
    public async Task RemoveFolderAsync(LibraryFolderRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        await _folders.RemoveAsync(row.Folder.Id, ct);
        await _watcher.RefreshAsync(ct);
        await LoadAsync(ct);
        _scans.NotifyLibraryChanged();
        SetNotice("Removed " + row.Path + " and its tracks from the library.");
    }

    /// <summary>A disabled folder keeps its rows but is neither scanned nor watched.</summary>
    public async Task SetEnabledAsync(LibraryFolderRow row, bool enabled, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Enabled == enabled)
        {
            return;
        }

        await _folders.SetEnabledAsync(row.Folder.Id, enabled, ct);
        await _watcher.RefreshAsync(ct);
        await LoadAsync(ct);
    }

    /// <summary>Rescan one folder, every file re-read.</summary>
    public Task RescanFolderAsync(LibraryFolderRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _scans.ScanAsync(new ScanRequest([row.Folder.Id], ForceReread: true), ct);
    }

    /// <summary>Rescan every enabled folder, every file re-read (docs: Settings › Library › Rescan).</summary>
    public Task RescanAllAsync(CancellationToken ct = default) => _scans.ScanAsync(new ScanRequest(ForceReread: true), ct);

    public void CancelScan() => _scans.Cancel();

    /// <summary>Deletes tracks missing for over <see cref="PurgeAge"/>.</summary>
    public async Task PurgeMissingAsync(CancellationToken ct = default)
    {
        await RunBusyAsync(async () =>
        {
            int purged = await _tracks.PurgeMissingAsync(PurgeCutoff(_clock.GetUtcNow()), ct);
            if (purged > 0)
            {
                _scans.NotifyLibraryChanged();
            }

            return purged == 0
                ? "No tracks have been missing for over 30 days."
                : "Purged " + N(purged) + (purged == 1 ? " track" : " tracks") + " missing for over 30 days.";
        }, ct);
    }

    public async Task RebuildIndexAsync(CancellationToken ct = default)
    {
        await RunBusyAsync(async () =>
        {
            int indexed = await _search.RebuildIndexAsync(ct);
            return "Search index rebuilt over " + N(indexed) + (indexed == 1 ? " track." : " tracks.");
        }, ct);
    }

    /// <summary>Clears the art cache, then a forced rescan renders every image again.</summary>
    public async Task RegenerateArtAsync(CancellationToken ct = default)
    {
        if (_art is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await _art.ClearAsync(ct);
            return "Art cache cleared; rescanning to render it again.";
        }, ct);
        await _scans.ScanAsync(new ScanRequest(ForceReread: true), ct);
    }

    /// <summary>
    /// Import playlists from exports (E6-S2): the auto-exports in the data folder, for after a database reset. A playlist
    /// whose name the library already has is left alone, so it is safe to press twice.
    /// </summary>
    public Task ImportExportsAsync(CancellationToken ct = default) =>
        RunBusyAsync(() => PlaylistActionAsync(async () => PlaylistFileText.Imported(await _playlistFiles.ImportExportsAsync(ct))), ct);

    /// <summary>Import playlist files…: M3U8 or M3U files the user picks, each one a new playlist.</summary>
    public async Task ImportFilesAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> files = await _playlistPicker.PickOpenFilesAsync(ct);
        if (files.Count == 0)
        {
            return;
        }

        await RunBusyAsync(() => PlaylistActionAsync(async () =>
        {
            var results = new List<PlaylistImportResult>(files.Count);
            foreach (string file in files)
            {
                results.Add(await _playlistFiles.ImportAsync(file, ct));
            }

            return PlaylistFileText.Imported(results);
        }), ct);
    }

    /// <summary>Export playlists…: every playlist into a folder the user picks, paths relative to it.</summary>
    public async Task ExportPlaylistsAsync(CancellationToken ct = default)
    {
        if (await _picker.PickFolderAsync(ct) is not { } directory)
        {
            return;
        }

        await RunBusyAsync(() => PlaylistActionAsync(async () => PlaylistFileText.ExportedAll(await _playlistFiles.ExportAllAsync(directory, ct), directory)), ct);
    }

    /// <summary>A file that cannot be read or written ends in a notice saying so, not in an unhandled exception.</summary>
    private static async Task<string> PlaylistActionAsync(Func<Task<string>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "Could not finish: " + e.Message;
        }
    }

    /// <summary>The Unix-millisecond cutoff for <see cref="PurgeAge"/> at <paramref name="now"/>.</summary>
    public static long PurgeCutoff(DateTimeOffset now) => (now - PurgeAge).ToUnixTimeMilliseconds();

    /// <summary>"Disabled", "Never scanned", or when and how the last scan ended ("Scanned 5 min ago · ok").</summary>
    public static string DescribeLastScan(LibraryFolderDto folder, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!folder.Enabled)
        {
            return "Disabled";
        }

        if (folder.LastScanAt is not { } at)
        {
            return "Never scanned";
        }

        string when = Ago(now - DateTimeOffset.FromUnixTimeMilliseconds(at));
        return folder.LastScanStatus is { Length: > 0 } status ? "Scanned " + when + " · " + status : "Scanned " + when;
    }

    /// <summary>"just now", "5 min ago", "3 h ago", "2 days ago".</summary>
    public static string Ago(TimeSpan since)
    {
        if (since < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (since < TimeSpan.FromHours(1))
        {
            return ((int)since.TotalMinutes).ToString(CultureInfo.CurrentCulture) + " min ago";
        }

        if (since < TimeSpan.FromDays(1))
        {
            return ((int)since.TotalHours).ToString(CultureInfo.CurrentCulture) + " h ago";
        }

        int days = (int)since.TotalDays;
        return days == 1 ? "yesterday" : days.ToString(CultureInfo.CurrentCulture) + " days ago";
    }

    partial void OnSplitArtistsChanged(bool value)
    {
        if (!_seeding)
        {
            _settings.SetValue(SettingsKeys.LibrarySplitArtists, value);
        }
    }

    partial void OnWriteRatingsToFilesChanged(bool value)
    {
        if (!_seeding)
        {
            _settings.SetValue(SettingsKeys.LibraryWriteRatingsToFiles, value);
        }
    }

    partial void OnHoverPreviewChanged(bool value)
    {
        if (_seeding)
        {
            return;
        }

        _settings.SetValue(SettingsKeys.UiHoverPreview, value);
        // A choice made here is a choice made: the first-hover offer (Q-74) would only be asking it again.
        _settings.SetValue(SettingsKeys.UiHoverPreviewOffered, true);
    }

    private void OnSettingChanged(object? sender, string key)
    {
        if (key == SettingsKeys.UiHoverPreview)
        {
            ReadHoverPreview();
        }
    }

    private void ReadHoverPreview()
    {
        bool stored = _settings.GetValue(SettingsKeys.UiHoverPreview, SettingsKeys.Defaults.UiHoverPreview);
        if (HoverPreview == stored)
        {
            return;
        }

        _seeding = true;
        try
        {
            HoverPreview = stored;
        }
        finally
        {
            _seeding = false;
        }
    }

    partial void OnIsBusyChanged(bool value) => CanPurge = MissingCount > 0 && !value;

    private async Task RunBusyAsync(Func<Task<string>> action, CancellationToken ct)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            SetNotice(await action());
        }
        finally
        {
            IsBusy = false;
        }

        await LoadAsync(ct);
    }

    private void SetNotice(string text)
    {
        Notice = text;
        HasNotice = text.Length > 0;
    }

    private void OnScanStateChanged(object? sender, EventArgs e) => ReadScanState();

    private void OnLibraryChanged(object? sender, EventArgs e) => LoadAsync().Forget("Library settings reload");

    private void ReadScanState()
    {
        IsScanning = _scans.IsScanning;
        ScanStatus = _scans.Progress is { } progress
            ? LibraryScanCoordinator.Describe(progress)
            : IsScanning ? "Starting scan…" : string.Empty;
        if (_scans.LastReport is { } report)
        {
            HasLastReport = true;
            LastReport = LibraryScanCoordinator.Describe(report);
            LastReportWhen = _scans.LastReportAt is { } at ? "Last scan " + Ago(_clock.GetUtcNow() - at) : string.Empty;
            Failures = report.Failures.Take(FailuresShown).Select(f => f.Path + " — " + DescribeFailure(f)).ToArray();
            HasFailures = Failures.Count > 0;
        }
        else
        {
            HasLastReport = false;
            LastReport = string.Empty;
            LastReportWhen = string.Empty;
            Failures = [];
            HasFailures = false;
        }
    }

    private static string DescribeFailure(ScanFailure failure) => failure.Outcome switch
    {
        TagReadOutcome.Unsupported => "unsupported format",
        TagReadOutcome.TimedOut => "tag read timed out",
        TagReadOutcome.CorruptTags => "corrupt tags",
        _ => failure.Error is { Length: > 0 } error ? error : "could not be read",
    };

    private static string N(int n) => n.ToString("N0", CultureInfo.CurrentCulture);
}
