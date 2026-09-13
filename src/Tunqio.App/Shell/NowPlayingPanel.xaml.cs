using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Tunqio.Core;

namespace Tunqio.App.Shell;

/// <summary>
/// What one art load did (E2-S3, AC-73). <paramref name="ElapsedMs"/> is wall time from the source being set to
/// the image opening or failing, which is a bound on how long the decode took; the stall it has to be compared
/// against is measured on the UI thread by <see cref="NowPlayingSpikeRunner"/>. The two together are the
/// criterion: the decode took real time <em>and</em> the UI thread was not the thread that spent it.
/// </summary>
internal sealed record ArtLoad(string? Hash, bool Opened, double ElapsedMs, int PixelWidth, int PixelHeight, string Note);

/// <summary>Which picker the empty state asked for (E2-S4).</summary>
public enum OpenRequest
{
    Files,
    Folder,
}

/// <summary>
/// The Now Playing panel (E2-S3): art, title, artists, album, year and format badge over the visualizer surface.
/// Everything it says comes from <see cref="NowPlayingViewModel"/>; what is here is the art layering, the two
/// links, and the timing the spike reads.
/// </summary>
/// <remarks>
/// The art is a <see cref="BitmapImage"/> over a file URI, which is what keeps the 1000 px decode off the UI
/// thread: setting <c>UriSource</c> returns immediately and the file is read and decoded on a background thread,
/// with <c>ImageOpened</c> arriving here afterwards. That is also why the placeholder is a layer underneath
/// rather than an alternative — between the source being set and the image opening there is a real interval, and
/// something has to be on the screen during it.
/// </remarks>
public sealed partial class NowPlayingPanel : UserControl
{
    private readonly Stopwatch _artClock = new();
    private string? _loadingHash;

    public NowPlayingPanel()
    {
        InitializeComponent();
        ProductText.Text = Identity.ProductName;
        VersionText.Text = string.Create(CultureInfo.InvariantCulture, $"Version {ProductVersion()}");
        Art.RegisterPropertyChangedCallback(Image.SourceProperty, OnArtSourceChanged);
        // T-182: sized from the room left over, not left at 360 and clipped when the stacked shell makes this a short row.
        SizeChanged += (_, _) => FitArt();
        MetadataBlock.SizeChanged += (_, _) => FitArt();
    }

    /// <summary>Bounds the art's box to <see cref="NowPlayingArtLayout.ArtEdge"/> for the panel's current size.</summary>
    private void FitArt()
    {
        double edge = NowPlayingArtLayout.ArtEdge(ActualHeight, ActualWidth, MetadataBlock.ActualHeight);
        ArtBox.MaxWidth = edge;
        ArtBox.MaxHeight = edge;
    }

    /// <summary>Set by the shell once the panel is in the tree; null before that, which XAML tolerates.</summary>
    public NowPlayingViewModel? ViewModel
    {
        get => (NowPlayingViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(NowPlayingViewModel), typeof(NowPlayingPanel), new PropertyMetadata(null));

    /// <summary>The most recent art load, for the spike; null until one has finished.</summary>
    internal ArtLoad? LastArtLoad { get; private set; }

    /// <summary>Where the E2-S3 spike parks its burst images so XAML will actually decode them.</summary>
    internal Canvas ArtMeasurementCanvas => ArtMeasurementHost;

    /// <summary>The size the art is drawn at, so the spike's burst images decode to what the panel decodes to.</summary>
    internal static double ArtEdge => NowPlayingArtLayout.MaxEdge;

    /// <summary>Raised on the UI thread when an art load finishes, opened or failed.</summary>
    internal event EventHandler<ArtLoad>? ArtLoadCompleted;

    /// <summary>
    /// Raised when the empty state's Open files / Open folder is pressed (E2-S4). An event rather than a
    /// dependency on the coordinator: the panel's job is the gesture, and the shell owns what happens next —
    /// which includes showing a notice on the window, something this control cannot reach.
    /// </summary>
    public event EventHandler<OpenRequest>? OpenRequested;

    private void OnOpenFiles(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, OpenRequest.Files);

    private void OnOpenFolder(object sender, RoutedEventArgs e) => OpenRequested?.Invoke(this, OpenRequest.Folder);

    private void OnArtistClick(object sender, RoutedEventArgs e) => ViewModel?.OpenArtist();

    private void OnAlbumClick(object sender, RoutedEventArgs e) => ViewModel?.OpenAlbum();

    /// <summary>
    /// The clock starts when the binding hands the <c>Image</c> a source, which is the moment the decode is asked
    /// for. A null source is the placeholder-only case and has nothing to time.
    /// </summary>
    private void OnArtSourceChanged(DependencyObject sender, DependencyProperty property)
    {
        if (Art.Source is null)
        {
            _artClock.Reset();
            _loadingHash = null;
            return;
        }

        _loadingHash = ViewModel?.ArtHash;
        _artClock.Restart();
    }

    private void OnArtOpened(object sender, RoutedEventArgs e)
    {
        var bitmap = Art.Source as BitmapImage;
        Finish(true, bitmap?.PixelWidth ?? 0, bitmap?.PixelHeight ?? 0, "decoded");
    }

    /// <summary>
    /// A failure is not an error to report: the cache may have been cleared since the row was written, and the
    /// placeholder underneath is already the right thing to be showing. It is recorded so the spike can tell a
    /// load that never happened from one that did.
    /// </summary>
    private void OnArtFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Art.Source = null;
        Finish(false, 0, 0, e.ErrorMessage);
    }

    private void Finish(bool opened, int pixelWidth, int pixelHeight, string note)
    {
        double elapsed = _artClock.IsRunning ? _artClock.Elapsed.TotalMilliseconds : 0;
        _artClock.Reset();
        var load = new ArtLoad(_loadingHash, opened, Math.Round(elapsed, 2), pixelWidth, pixelHeight, note);
        LastArtLoad = load;
        ArtLoadCompleted?.Invoke(this, load);
    }

    private static string ProductVersion()
    {
        string? informational = typeof(NowPlayingPanel).Assembly
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        return informational ?? typeof(NowPlayingPanel).Assembly.GetName().Version?.ToString(3) ?? "?";
    }
}
