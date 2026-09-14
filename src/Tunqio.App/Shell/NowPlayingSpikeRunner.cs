using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Tunqio.App.Controls;
using Tunqio.Core.Library;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Tunqio.App.Shell;

/// <summary>One art file the spike generated and then loaded, and what the load cost.</summary>
internal sealed record ArtLoadMeasurement(string Hash, long FileBytes, bool Opened, double ElapsedMs, int PixelWidth, int PixelHeight, string Note);

/// <summary>
/// What the UI thread was doing while the loads ran. Frames are the probe because "blocking the UI thread" and
/// "dropping frames" are the same event seen from two sides: <c>CompositionTarget.Rendering</c> fires once per
/// composition frame on that thread, so a gap between two of them is time the thread was not available.
/// </summary>
internal sealed record FrameGaps(int Frames, double WorstGapMs, double MedianGapMs, double WindowMs);

/// <summary>
/// E2-S3 measurement mode: <c>Tunqio.exe --nowplaying-spike [--out FILE]</c>. Generates 1000 px art, loads it
/// the way the panel loads it, and reports what the UI thread was doing while it happened.
/// </summary>
/// <remarks>
/// <para>
/// AC-73 is two claims and they need different evidence. That the art <em>arrives</em> is the sequential pass:
/// the real <see cref="NowPlayingPanel"/>, the real binding, one track after another, each reporting the size it
/// decoded to. That the decode is <em>not on the UI thread</em> cannot be shown by one load, because a single
/// 1000 px JPEG decodes in about a frame and a stall that size hides inside the noise. So the burst pass asks for
/// sixteen at once, and the finding is that they overlap: several hundred milliseconds of decode go in flight and
/// come back in a few dozen. One thread cannot do that, and the UI thread is one thread. The frame gaps are the
/// second half of the same statement, read directly off the thread in question and against an idle control.
/// </para>
/// <para>
/// The images are noise rather than a flat colour, because a flat 1000 px JPEG is a few hundred bytes and decodes
/// to nothing; the measurement would pass for the wrong reason.
/// </para>
/// </remarks>
internal sealed class NowPlayingSpikeRunner
{
    private static readonly JsonSerializerOptions ReportOptions = new() { WriteIndented = true };

    /// <summary>The size the cache stores for Now Playing (<see cref="ArtSize.Large"/>), which is what AC-73 names.</summary>
    private const int Edge = (int)ArtSize.Large;

    /// <summary>How many images the burst asks for at once.</summary>
    private const int BurstCount = 16;

    /// <summary>
    /// The longest gap between composition frames the run may show. Three frames at 60 Hz: one dropped frame is
    /// scheduling, a stall long enough to be seen is not.
    /// </summary>
    private const double FrameBudgetMs = 50;

    /// <summary>How far the burst's decoding has to overlap itself before "off-thread" is a fair reading.</summary>
    private const double WorkToStallRatio = 4;

    private readonly MainWindow _window;
    private readonly NowPlayingPanel _panel;
    private readonly ILogger _logger;
    private readonly string _outputPath;
    private readonly string _artDirectory;
    private readonly List<ArtLoadMeasurement> _sequential = [];
    private readonly List<double> _gaps = [];
    private readonly System.Diagnostics.Stopwatch _frameClock = new();

    private List<SpikeArt> _art = [];
    private FrameGaps _idleFrames = new(0, 0, 0, 0);
    private FrameGaps _burstFrames = new(0, 0, 0, 0);
    private double _burstWallMs;
    private double _burstWorkMs;
    private int _burstOpened;
    private List<string> _visibleText = [];
    private List<string> _emptyStateButtons = [];
    private bool _windowAcceptsDrop;
    private string _panelAutomationName = string.Empty;
    private int _burstFailed;

    public NowPlayingSpikeRunner(MainWindow window, NowPlayingPanel panel, ILogger logger, string[] args, string defaultOutput)
    {
        _window = window;
        _panel = panel;
        _logger = logger;
        _outputPath = StringArg(args, "--out") ?? defaultOutput;
        _artDirectory = Path.Combine(Path.GetDirectoryName(_outputPath)!, "nowplaying-spike-art");
    }

    public static bool IsRequested(string[] args) => args.Contains("--nowplaying-spike", StringComparer.Ordinal);

    public void Start() => _ = RunAsync();

    private async Task RunAsync()
    {
        try
        {
            _logger.LogInformation("now playing spike: generating {Count} {Edge} px images in {Directory}", BurstCount, Edge, _artDirectory);
            _art = await GenerateArtAsync().ConfigureAwait(true);
            AlbumArt.Cache = new SpikeArtCache(_art);

            // A window that has just been activated is still doing first-frame work; the idle window is both the
            // warm-up and the baseline the burst's gaps are read against.
            _idleFrames = await MeasureFramesAsync(TimeSpan.FromMilliseconds(1500), null).ConfigureAwait(true);
            ReadEmptyState();
            await RunSequentialAsync().ConfigureAwait(true);
            _burstFrames = await MeasureFramesAsync(TimeSpan.FromMilliseconds(1500), RunBurstAsync).ConfigureAwait(true);
            Finish();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _logger.LogError(e, "now playing spike failed");
            Environment.ExitCode = 1;
            _window.Close();
        }
    }

    // ---- the sequential pass: the real panel, the real binding ----------------------------------------------------

    /// <summary>
    /// One track at a time through <see cref="NowPlayingViewModel"/>, which is the path a track change takes. It
    /// answers "does 1000 px art reach the panel, and how big was what arrived" — a source that silently failed
    /// would show as a load that never opened rather than as a panel that looks fine because the placeholder is
    /// underneath.
    /// </summary>
    private async Task RunSequentialAsync()
    {
        var vm = new NowPlayingViewModel(new NoSessionSource(), rater: null); // no library behind the spike, so nothing to rate into
        _panel.ViewModel = vm;
        foreach (SpikeArt art in _art.Take(4))
        {
            var done = new TaskCompletionSource<ArtLoad>();
            void OnCompleted(object? sender, ArtLoad load) => done.TrySetResult(load);
            _panel.ArtLoadCompleted += OnCompleted;
            try
            {
                vm.Show(TrackFor(art));
                ArtLoad load = await done.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(true);
                _sequential.Add(new ArtLoadMeasurement(
                    art.Hash, art.Bytes, load.Opened, load.ElapsedMs, load.PixelWidth, load.PixelHeight, load.Note));
                _logger.LogInformation(
                    "now playing spike: {Hash} {Result} at {Width}x{Height} in {ElapsedMs} ms",
                    art.Hash, load.Opened ? "opened" : "failed", load.PixelWidth, load.PixelHeight, load.ElapsedMs);
            }
            finally
            {
                _panel.ArtLoadCompleted -= OnCompleted;
            }
        }

        // What the panel is showing, read off the tree rather than off the view model. The view-model tests already
        // say what the strings should be; the only thing left to doubt is whether anything is bound to them.
        _visibleText = [.. VisualTree.Descendants<TextBlock>(_panel)
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))];
        _panelAutomationName = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(_panel.Content as UIElement ?? _panel);
        _logger.LogInformation("now playing spike: the panel is showing {Text}", string.Join(" | ", _visibleText));
        vm.Dispose();
    }

    /// <summary>
    /// The empty state, before anything has been shown (E2-S4). Two buttons and a window that accepts a drop are
    /// the whole of "there is a way in from here", and all three fail silently: a button nobody wired does
    /// nothing when pressed, and <c>AllowDrop</c> left off means a drag is simply refused with no error anywhere.
    /// </summary>
    private void ReadEmptyState()
    {
        _emptyStateButtons =
        [
            .. VisualTree.Descendants<Button>(_panel)
                .Select(b => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(b))
                .Where(name => !string.IsNullOrWhiteSpace(name)),
        ];
        _windowAcceptsDrop = _window.RootAcceptsDrop;
        _logger.LogInformation(
            "now playing spike: empty state offers {Buttons}; the window accepts drops: {AcceptsDrop}",
            string.Join(", ", _emptyStateButtons), _windowAcceptsDrop);
    }

    // ---- the burst pass: is the decode on this thread? -------------------------------------------------------------

    /// <summary>
    /// Every image asked for inside one tick, through <see cref="AlbumArt.Large"/> — the same call the panel's
    /// binding makes — into <see cref="Image"/> elements the size the panel draws art at, in the panel's own
    /// off-screen host. They have to be in the tree: XAML does not decode an image nothing is showing, and it
    /// decodes to the size the element renders at, so a detached bitmap measures neither the right work nor any.
    /// </summary>
    /// <remarks>
    /// A load that never completes is reported rather than thrown: "four of sixteen opened" is a finding, and a
    /// bare <c>TimeoutException</c> out of the awaiting frame is not.
    /// </remarks>
    private async Task RunBurstAsync()
    {
        Canvas host = _panel.ArtMeasurementCanvas;
        var wall = System.Diagnostics.Stopwatch.StartNew();
        var pending = new List<Task<double>>(BurstCount);
        foreach (SpikeArt art in _art)
        {
            if (AlbumArt.Large(art.Hash) is not BitmapImage bitmap)
            {
                continue;
            }

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var opened = new TaskCompletionSource<double>();
            var image = new Image
            {
                Width = NowPlayingPanel.ArtEdge,
                Height = NowPlayingPanel.ArtEdge,
                Stretch = Stretch.UniformToFill,
            };
            image.ImageOpened += (_, _) =>
            {
                _burstOpened++;
                opened.TrySetResult(clock.Elapsed.TotalMilliseconds);
            };
            image.ImageFailed += (_, _) =>
            {
                _burstFailed++;
                opened.TrySetResult(clock.Elapsed.TotalMilliseconds);
            };
            host.Children.Add(image);
            image.Source = bitmap;
            pending.Add(opened.Task);
        }

        Task<double[]> all = Task.WhenAll(pending);
        await Task.WhenAny(all, DelayOnUiAsync(TimeSpan.FromSeconds(1))).ConfigureAwait(true);
        _burstWallMs = Math.Round(wall.Elapsed.TotalMilliseconds, 2);
        _burstWorkMs = Math.Round(pending.Where(t => t.IsCompletedSuccessfully).Sum(t => t.Result), 2);
        host.Children.Clear();
    }

    // ---- the probe -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Watches composition frames for the whole of <paramref name="window"/>, running <paramref name="during"/>
    /// inside it.
    /// </summary>
    /// <remarks>
    /// It keeps watching after the work finishes rather than stopping with it. The burst is over in about fifty
    /// milliseconds, which is three frames — and a worst-of-three is not a measurement of anything. Watching a
    /// fixed window either side of it gives the number something to be worst <em>of</em>, and makes it comparable
    /// with the idle window, which is the control.
    /// </remarks>
    private async Task<FrameGaps> MeasureFramesAsync(TimeSpan window, Func<Task>? during)
    {
        _gaps.Clear();
        _frameClock.Restart();
        void OnRendering(object? sender, object e)
        {
            _gaps.Add(_frameClock.Elapsed.TotalMilliseconds);
            _frameClock.Restart();
        }

        CompositionTarget.Rendering += OnRendering;
        var watching = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (during is not null)
            {
                await during().WaitAsync(window).ConfigureAwait(true);
            }

            TimeSpan remaining = window - watching.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                await DelayOnUiAsync(remaining).ConfigureAwait(true);
            }
        }
        finally
        {
            CompositionTarget.Rendering -= OnRendering;
            _frameClock.Stop();
        }

        // The first gap is from subscribing to the next frame, which is scheduling and not a stall.
        List<double> gaps = _gaps.Skip(1).ToList();
        if (gaps.Count == 0)
        {
            return new FrameGaps(_gaps.Count, 0, 0, Math.Round(watching.Elapsed.TotalMilliseconds, 2));
        }

        List<double> sorted = [.. gaps];
        sorted.Sort();
        return new FrameGaps(
            gaps.Count,
            Math.Round(gaps.Max(), 2),
            Math.Round(sorted[sorted.Count / 2], 2),
            Math.Round(watching.Elapsed.TotalMilliseconds, 2));
    }

    /// <summary>
    /// A wait that lets the dispatcher keep running. <c>Task.Delay</c> and not a <c>DispatcherQueueTimer</c>: the
    /// timer's managed wrapper is only rooted by the local that made it, and the burst allocates sixteen 1000 px
    /// surfaces, so the collection that follows it took the timer with it and the run hung waiting for a tick
    /// that was never going to come.
    /// </summary>
    private static Task DelayOnUiAsync(TimeSpan duration) => Task.Delay(duration);

    // ---- the report --------------------------------------------------------------------------------------------------

    private void Finish()
    {
        bool arrived = _sequential.Count > 0
            && _sequential.TrueForAll(m => m.Opened && m.PixelWidth == Edge && m.PixelHeight == Edge);

        // Title, artist, album line and format badge, each as its own visible TextBlock in the panel.
        bool metadataShown = _visibleText.Count >= 4 && _panelAutomationName.StartsWith("Now playing:", StringComparison.Ordinal);

        // E2-S4's way in from an empty window: the two pickers, named, and a root that will take a drag.
        bool wayIn = _windowAcceptsDrop
            && _emptyStateButtons.Contains("Open files", StringComparer.Ordinal)
            && _emptyStateButtons.Contains("Open folder", StringComparer.Ordinal);
        bool burstLoaded = _burstOpened == _art.Count && _burstFailed == 0;
        bool withinBudget = _burstFrames.Frames > 20 && _burstFrames.WorstGapMs > 0 && _burstFrames.WorstGapMs <= FrameBudgetMs;

        // The load-bearing number. Every image's clock runs from its source being set to it opening, so the sum is
        // how much decoding was in flight; the wall is how long that took end to end. A ratio well above one is
        // decoding that overlapped itself, and a single thread — the UI thread or any other — cannot do that.
        double concurrency = _burstWallMs > 0 ? Math.Round(_burstWorkMs / _burstWallMs, 2) : 0;
        bool overlapped = concurrency >= WorkToStallRatio;
        bool offThread = withinBudget && overlapped;
        bool pass = arrived && metadataShown && wayIn && burstLoaded && offThread;

        string verdict = pass
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"PASS: {_sequential.Count} loads through the panel arrived at {Edge}x{Edge}; {BurstCount} at once " +
                $"put {_burstWorkMs} ms of decode in flight and finished in {_burstWallMs} ms ({concurrency}x " +
                $"overlap, which one thread cannot do), and the worst gap between composition frames over the " +
                $"burst window was {_burstFrames.WorstGapMs} ms against {_idleFrames.WorstGapMs} ms with the " +
                $"window idle — the thread that paints is not the thread that decoded")
            : !arrived
                ? "FAIL: art did not reach the panel at the size the cache stores; see Sequential"
                : !wayIn
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"FAIL: an empty window offers no way in — buttons [{string.Join(", ", _emptyStateButtons)}], " +
                    $"accepts drops: {_windowAcceptsDrop}")
                : !metadataShown
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"FAIL: the panel is showing {_visibleText.Count} lines of text and reads as " +
                    $"\"{_panelAutomationName}\" — the metadata is not bound")
                : !burstLoaded
                    ? string.Create(CultureInfo.InvariantCulture, $"FAIL: {_burstFailed} of {_art.Count} burst loads failed")
                    : _burstFrames.Frames <= 20
                        ? string.Create(
                            CultureInfo.InvariantCulture,
                            $"INCONCLUSIVE: only {_burstFrames.Frames} composition frames were seen over the burst " +
                            $"window; a worst-of-{_burstFrames.Frames} is not a measurement of anything")
                        : !withinBudget
                            ? string.Create(
                                CultureInfo.InvariantCulture,
                                $"FAIL: the UI thread lost {_burstFrames.WorstGapMs} ms between frames during the " +
                                $"burst, against a budget of {FrameBudgetMs} ms and {_idleFrames.WorstGapMs} ms idle")
                            : string.Create(
                                CultureInfo.InvariantCulture,
                                $"FAIL: {_burstWorkMs} ms of decode took {_burstWallMs} ms of wall time — only " +
                                $"{concurrency}x overlap, which is consistent with the decodes being serialised " +
                                $"onto one thread");

        var report = new
        {
            Machine = Environment.MachineName,
            Verdict = verdict,
            Edge,
            BurstCount,
            FrameBudgetMs,
            WorkToStallRatio,
            ArtDirectory = _artDirectory,
            PanelAutomationName = _panelAutomationName,
            PanelVisibleText = _visibleText,
            EmptyStateButtons = _emptyStateButtons,
            WindowAcceptsDrop = _windowAcceptsDrop,
            Sequential = _sequential,
            IdleFrames = _idleFrames,
            BurstFrames = _burstFrames,
            BurstWallMs = _burstWallMs,
            BurstDecodeInFlightMs = _burstWorkMs,
            BurstOverlap = concurrency,
            BurstOpened = _burstOpened,
            BurstFailed = _burstFailed,
            ArtArrived = arrived,
            MetadataShown = metadataShown,
            WayIn = wayIn,
            DecodedOffThread = offThread,
            Pass = pass,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        File.WriteAllText(_outputPath, JsonSerializer.Serialize(report, ReportOptions));
        _logger.LogInformation("now playing spike {Result}: {Verdict}; report {Path}", pass ? "PASS" : "FAIL", verdict, _outputPath);
        Environment.ExitCode = pass ? 0 : 1;
        _window.Close();
    }

    // ---- the fixtures ------------------------------------------------------------------------------------------------

    private async Task<List<SpikeArt>> GenerateArtAsync()
    {
        Directory.CreateDirectory(_artDirectory);
        var art = new List<SpikeArt>(BurstCount);
        var random = new Random(20260910);
        for (int i = 0; i < BurstCount; i++)
        {
            string hash = string.Create(CultureInfo.InvariantCulture, $"spike{i:00}");
            string path = Path.Combine(_artDirectory, hash + ".jpg");
            byte[] jpeg = await EncodeNoiseAsync(random).ConfigureAwait(true);
            await File.WriteAllBytesAsync(path, jpeg).ConfigureAwait(true);
            art.Add(new SpikeArt(hash, path, jpeg.LongLength));
        }

        _logger.LogInformation(
            "now playing spike: wrote {Count} images averaging {Bytes} bytes", art.Count, art.Sum(a => a.Bytes) / art.Count);
        return art;
    }

    /// <summary>
    /// A <see cref="Edge"/>-square JPEG of coloured noise. Noise so the file is the size a real cover is and the
    /// decode is real work; a flat colour compresses to nothing and would make the burst pass for free.
    /// </summary>
    private static async Task<byte[]> EncodeNoiseAsync(Random random)
    {
        var pixels = new byte[Edge * Edge * 4];
        random.NextBytes(pixels);
        using var output = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output).AsTask().ConfigureAwait(true);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, Edge, Edge, 96, 96, pixels);
        await encoder.FlushAsync().AsTask().ConfigureAwait(true);
        output.Seek(0);
        using var reader = new DataReader(output);
        uint size = (uint)output.Size;
        await reader.LoadAsync(size).AsTask().ConfigureAwait(true);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static TrackDto TrackFor(SpikeArt art) => new(
        Id: 1,
        FolderId: 1,
        Path: art.Path,
        Title: "Spike " + art.Hash,
        Artists: [new ArtistRef(1, "Now Playing spike")],
        AlbumId: 1,
        AlbumTitle: "Measurement",
        AlbumArtist: "Now Playing spike",
        TrackNo: 1,
        DiscNo: 1,
        Year: 2026,
        DurationMs: 240_000,
        Codec: "flac",
        BitrateKbps: null,
        SampleRate: 96_000,
        Channels: 2,
        BitDepth: 24,
        FileSize: art.Bytes,
        FileMtime: 0,
        Composer: null,
        Comment: null,
        ReplayGain: null,
        ArtHash: art.Hash,
        Mbid: null,
        AddedAt: 0,
        Rating: null,
        PlayCount: 0,
        LastPlayedAt: null,
        Missing: false);

    private static string? StringArg(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private sealed record SpikeArt(string Hash, string Path, long Bytes);

    /// <summary>The generated files behind <see cref="AlbumArt"/>, so the binding resolves them the usual way.</summary>
    private sealed class SpikeArtCache : IArtCache
    {
        private readonly Dictionary<string, string> _paths;

        public SpikeArtCache(IEnumerable<SpikeArt> art) =>
            _paths = art.ToDictionary(a => a.Hash, a => a.Path, StringComparer.Ordinal);

        public string? PathFor(string? hash, ArtSize size) =>
            hash is not null && size == ArtSize.Large && _paths.TryGetValue(hash, out string? path) ? path : null;

        public Task<ArtHashes> StoreAsync(EmbeddedPicture? picture, string audioPath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ArtPalette?> LoadPaletteAsync(string? hash, CancellationToken ct = default) =>
            Task.FromResult<ArtPalette?>(null);

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A session source that never produces one: the spike drives the panel directly.</summary>
    private sealed class NoSessionSource : Playback.IPlaybackSessionSource
    {
        public Tunqio.Core.Playback.PlaybackSession? Session => null;

        public bool Started => false;

        public event EventHandler<Tunqio.Core.Playback.PlaybackSession>? SessionReady
        {
            add { }
            remove { }
        }
    }
}
