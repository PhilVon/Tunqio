using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tunqio.App.Controls;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Library.Database;
using Tunqio.Library.Repositories;
using Windows.Graphics;

namespace Tunqio.App;

/// <summary>
/// E3-S3 measurement mode: <c>Tunqio.exe --library-spike [DB] [--seconds N] [--step F] [--out FILE]</c>. Opens the
/// given library database (default: the app's own), shows the Tracks table and Albums grid over it at 1920×1080,
/// then scrolls the Tracks table by F viewports per frame (default 0.5, a fast flick; 1.0 realises every row
/// afresh each frame) until every row has been loaded and shown. Writes a
/// JSON report with the frame gaps seen during the scroll, how many item containers the list created against
/// how many rows it showed (recycling), and the process working set before and after. The pass line is the
/// two E3-S3 criteria (never below 50 fps, memory growth under 50 MB), which are stated against the reference
/// machine (T-90); on any other machine the numbers are information, not a verdict.
/// </summary>
internal sealed class LibrarySpikeRunner
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    private readonly ILogger _logger;
    private readonly string _databasePath;
    private readonly int _seconds;
    private readonly double _step;
    private readonly string _outputPath;
    private readonly Stopwatch _clock = new();
    private readonly List<double> _frameGapsMs = new(16_384);

    private DispatcherQueueTimer? _settle; // timers are fields: a DispatcherQueueTimer held only by a local can be collected before it ticks
    private DispatcherQueueTimer? _progress;
    private LibraryDatabase? _database;
    private LibraryService? _service;
    private LibrarySpikeWindow? _window;
    private IncrementalItemsSource<TrackDto>? _tracks;
    private ScrollViewer? _scroller;
    private int _totalTracks;
    private long _lastFrameTicks;
    private long _workingSetStart;
    private long _managedStart;
    private bool _finished;

    public LibrarySpikeRunner(ILogger logger, string[] args, string defaultDatabase, string defaultOutput)
    {
        _logger = logger;
        int i = Array.IndexOf(args, "--library-spike");
        _databasePath = i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : defaultDatabase;
        _seconds = IntArg(args, "--seconds", 300);
        _step = Math.Clamp(double.TryParse(StringArg(args, "--step"), NumberStyles.Float, CultureInfo.InvariantCulture, out double step) ? step : 0.5, 0.01, 10);
        _outputPath = StringArg(args, "--out") ?? defaultOutput;
    }

    public static bool IsRequested(string[] args) => args.Contains("--library-spike", StringComparer.Ordinal);

    /// <summary>Opens the database and builds the window; the caller activates it and then calls <see cref="Start"/>.</summary>
    public Window CreateWindow()
    {
        _database = LibraryDatabase.Open(_databasePath, logger: _logger);
        _service = new LibraryService(_database);
        _window = new LibrarySpikeWindow();
        _window.Closed += (_, _) =>
        {
            _database.Dispose();
            _database = null;
        };
        return _window;
    }

    public void Start()
    {
        if (_window is null || _service is null)
        {
            throw new InvalidOperationException("CreateWindow first");
        }

        _window.AppWindow.ResizeClient(new SizeInt32(1920, 1080));
        _tracks = IncrementalItemsSource.Tracks(_service.Tracks, new TrackQuery(TrackSort.Title));
        _window.TracksList.ItemsSource = _tracks;
        _window.AlbumsGrid.ItemsSource = IncrementalItemsSource.Albums(_service.Albums, new AlbumQuery(AlbumSort.Artist));
        _window.SetStatus($"library spike over {_databasePath}: loading");
        _logger.LogInformation("library spike: {Database}, scrolling the Tracks table end to end at {Step} viewports per frame (limit {Seconds}s)", _databasePath, _step, _seconds);

        _ = CountAsync();

        // Let the first page land and the layout settle before the clock starts.
        _settle = _window.DispatcherQueue.CreateTimer();
        _settle.Interval = TimeSpan.FromSeconds(2);
        _settle.IsRepeating = false;
        _settle.Tick += (_, _) => BeginScroll();
        _settle.Start();
    }

    private async Task CountAsync()
    {
        try
        {
            _totalTracks = await _service!.Tracks.CountAsync(new TrackQuery());
        }
        catch (Exception e)
        {
            _logger.LogError(e, "library spike: count failed");
        }
    }

    private void BeginScroll()
    {
        _scroller = _window!.TracksList.Scroller;
        if (_scroller is null)
        {
            _logger.LogError("library spike: the Tracks table has no ScrollViewer yet");
            Finish();
            return;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        _workingSetStart = Environment.WorkingSet;
        _managedStart = GC.GetTotalMemory(forceFullCollection: true);
        _clock.Start();
        _lastFrameTicks = _clock.ElapsedTicks;
        CompositionTarget.Rendering += OnRendering;
        _logger.LogInformation("library spike: scroll started with {Rows} rows loaded, extent {Extent:F0} px, viewport {Viewport:F0} px, working set {WorkingSet:F0} MB",
            _tracks!.Count, _scroller.ExtentHeight, _scroller.ViewportHeight, Mb(_workingSetStart));

        // Progress and watchdog independent of the render loop, so a run that stalls still explains itself.
        _progress = _window.DispatcherQueue.CreateTimer();
        _progress.Interval = TimeSpan.FromSeconds(5);
        _progress.IsRepeating = true;
        _progress.Tick += (timer, _) =>
        {
            if (_finished)
            {
                timer.Stop();
                return;
            }

            _logger.LogInformation("library spike: {Seconds:F0}s, {Frames} frames, {Rows}/{Total} rows loaded, offset {Offset:F0}/{Extent:F0} px, {Shown} shown by {Containers} containers, loading={Loading}, hasMore={HasMore}",
                _clock.Elapsed.TotalSeconds, _frameGapsMs.Count, _tracks.Count, _totalTracks, _scroller.VerticalOffset, _scroller.ExtentHeight,
                _window.TracksList.ItemsRealised, _window.TracksList.ContainersCreated, _tracks.IsLoading, _tracks.HasMore);
            if (_clock.Elapsed.TotalSeconds >= _seconds)
            {
                _logger.LogWarning("library spike: time limit reached with {Rows} of {Total} rows loaded", _tracks.Count, _totalTracks);
                Finish();
            }
        };
        _progress.Start();
    }

    private void OnRendering(object? sender, object e)
    {
        if (_finished)
        {
            return;
        }

        long now = _clock.ElapsedTicks;
        _frameGapsMs.Add((now - _lastFrameTicks) * 1000.0 / Stopwatch.Frequency);
        _lastFrameTicks = now;

        ScrollViewer scroller = _scroller!;
        bool atEnd = scroller.VerticalOffset + scroller.ViewportHeight >= scroller.ExtentHeight - 1;
        if (atEnd)
        {
            if (!_tracks!.HasMore)
            {
                Finish();
                return;
            }

            if (!_tracks.IsLoading)
            {
                _ = LoadMoreAsync(); // the ListView's own trigger usually fires first; this covers a jump that lands exactly on the edge
            }
        }
        else
        {
            scroller.ChangeView(null, scroller.VerticalOffset + _step * scroller.ViewportHeight, null, disableAnimation: true);
        }

        if (_frameGapsMs.Count % 60 == 0)
        {
            _window!.SetStatus(string.Create(CultureInfo.InvariantCulture,
                $"library spike: {_tracks!.Count:N0}/{_totalTracks:N0} rows loaded, {_window.TracksList.ItemsRealised:N0} shown by {_window.TracksList.ContainersCreated} containers, {_clock.Elapsed.TotalSeconds:F0}s"));
        }

        if (_clock.Elapsed.TotalSeconds >= _seconds)
        {
            _logger.LogWarning("library spike: time limit reached with {Rows} of {Total} rows loaded", _tracks!.Count, _totalTracks);
            Finish();
        }
    }

    private async Task LoadMoreAsync()
    {
        try
        {
            await _tracks!.LoadMoreAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "library spike: page load failed");
            Finish();
        }
    }

    private void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        CompositionTarget.Rendering -= OnRendering;
        _clock.Stop();

        long workingSetRaw = Environment.WorkingSet;
        long managedRaw = GC.GetTotalMemory(forceFullCollection: false);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long managedRetained = GC.GetTotalMemory(forceFullCollection: true);
        long workingSetSettled = Environment.WorkingSet;

        TracksList list = _window!.TracksList;
        int rows = _tracks?.Count ?? 0;
        double seconds = _clock.Elapsed.TotalSeconds;
        double[] gaps = _frameGapsMs.Skip(1).ToArray(); // the first "gap" is the settle timer
        double maxGap = gaps.Length > 0 ? gaps.Max() : 0;
        int over20 = gaps.Count(g => g > 20);
        int over33 = gaps.Count(g => g > 33.4);
        int over100 = gaps.Count(g => g > 100);
        double growthMb = (workingSetSettled - _workingSetStart) / 1048576.0;
        bool complete = rows > 0 && rows == _totalTracks;
        bool fpsPass = over20 == 0;
        bool memoryPass = growthMb < 50;
        bool pass = complete && fpsPass && memoryPass;

        var report = new
        {
            Machine = Environment.MachineName,
#if DEBUG
            Configuration = "Debug",
#else
            Configuration = "Release",
#endif
            Database = _databasePath,
            StepViewportsPerFrame = _step,
            Verdict = !complete
                ? "INCOMPLETE: not every row was loaded before the time limit or an error; see the log"
                : pass
                    ? "PASS: every row loaded and shown, no frame gap over 20 ms during the scroll, working set grew by less than 50 MB after a full GC"
                    : "FAIL against the E3-S3 criteria (stated for the reference machine, T-90): see FramesOver20ms and WorkingSetGrowthMB",
            Pass = pass,
            Complete = complete,
            ScrollFpsPass = fpsPass,
            MemoryPass = memoryPass,
            TotalTracks = _totalTracks,
            RowsLoaded = rows,
            RowsShown = list.ItemsRealised,
            ContainersCreated = list.ContainersCreated,
            AlbumsShown = _window.AlbumsGrid.ElementsPrepared,
            AlbumTilesCreated = _window.AlbumsGrid.ElementsCreated,
            Seconds = Math.Round(seconds, 2),
            Frames = gaps.Length,
            AverageFps = seconds > 0 ? Math.Round(gaps.Length / seconds, 1) : 0,
            MaxFrameGapMs = Math.Round(maxGap, 2),
            FramesOver20ms = over20,
            FramesOver33ms = over33,
            FramesOver100ms = over100,
            WorkingSetStartMB = Mb(_workingSetStart),
            WorkingSetEndRawMB = Mb(workingSetRaw),
            WorkingSetEndAfterGcMB = Mb(workingSetSettled),
            WorkingSetGrowthMB = Math.Round(growthMb, 1),
            ManagedStartMB = Mb(_managedStart),
            ManagedEndRawMB = Mb(managedRaw),
            ManagedRetainedMB = Mb(managedRetained),
            ManagedRetainedGrowthMB = Mb(managedRetained - _managedStart),
            BytesPerRowRetained = rows > 0 ? (managedRetained - _managedStart) / rows : 0,
        };
        string json = JsonSerializer.Serialize(report, ReportOptions);
        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        File.WriteAllText(_outputPath, json);
        _logger.LogInformation("library spike {Result}: {Rows}/{Total} rows in {Seconds:F1}s, {Frames} frames, max gap {MaxGap:F1} ms, {Over20} over 20 ms, working set +{Growth:F1} MB, {Containers} containers; report {Path}",
            pass ? "PASS" : complete ? "FAIL" : "INCOMPLETE", rows, _totalTracks, seconds, gaps.Length, maxGap, over20, growthMb, list.ContainersCreated, _outputPath);
        Environment.ExitCode = pass ? 0 : 1;
        _window.Close();
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);

    private static int IntArg(string[] args, string name, int fallback)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;
    }

    private static string? StringArg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
