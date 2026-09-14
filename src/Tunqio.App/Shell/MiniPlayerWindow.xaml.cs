using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Tunqio.App.Controls;
using Tunqio.App.Playback;
using Windows.Graphics;

namespace Tunqio.App.Shell;

/// <summary>
/// The mini player window (E5-S6). The window chrome and the snap live here; what it shows is two view models it owns
/// over the app's one session, disposed when it closes. The rule for where it snaps is <see cref="MiniPlayerSnap"/>.
/// </summary>
/// <remarks>
/// The main window decides what opening and closing mean: it hides itself when this opens and shows itself again when
/// this closes, whichever way it closed (<see cref="ReturnRequested"/>, the art double-clicked, or Alt+F4). So the app
/// is never left running with no window on the screen.
/// </remarks>
#pragma warning disable CA1001 // Closed is the window's teardown; a Window cannot implement IDisposable.
public sealed partial class MiniPlayerWindow : Window
{
    /// <summary>How long the window has to stay put after a move before it is snapped: a drag in progress is not a drop.</summary>
    private static readonly TimeSpan SnapSettle = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherQueueTimer _snapTimer;

    /// <param name="audio">The app's session source; the mini player's view models attach to it as the main window's do.</param>
    public MiniPlayerWindow(IPlaybackSessionSource audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        // Before InitializeComponent: the x:Bind bindings read these as the window is built.
        Transport = new TransportViewModel(audio, SynchronizationContext.Current);
        // No rater: the mini player shows no stars (E6-S7), and the main window's panel is where a track is rated.
        NowPlaying = new Shell.NowPlayingViewModel(audio, rater: null, navigator: null, SynchronizationContext.Current);
        InitializeComponent();
        Title = "Tunqio mini player";
        AppIcon.Apply(AppWindow, AppContext.BaseDirectory, "mini player");

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        // AppWindow sizes are physical pixels; the design size is in effective pixels.
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(MiniPlayerSnap.Width * scale), (int)Math.Round(MiniPlayerSnap.Height * scale)));

        _snapTimer = DispatcherQueue.CreateTimer();
        _snapTimer.Interval = SnapSettle;
        _snapTimer.IsRepeating = false;
        _snapTimer.Tick += OnSnapTimer;
        AppWindow.Changed += OnAppWindowChanged;

        Closed += (_, _) =>
        {
            AppWindow.Changed -= OnAppWindowChanged;
            _snapTimer.Stop();
            Transport.Dispose();
            NowPlaying.Dispose();
        };
    }

    /// <summary>The transport this window shows.</summary>
    public TransportViewModel Transport { get; }

    /// <summary>What is playing, for the art, title and artist.</summary>
    public NowPlayingViewModel NowPlaying { get; }

    /// <summary>The return button was pressed or the art double-clicked: the main window closes this and shows itself.</summary>
    public event EventHandler? ReturnRequested;

    /// <summary>Whether the window stays above other windows (on by default).</summary>
    public bool IsKeptOnTop => AppWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true };

    private void OnPrevious(object sender, RoutedEventArgs e) => Run(Transport.PreviousAsync);

    private void OnPlayPause(object sender, RoutedEventArgs e) => Run(Transport.PlayPauseAsync);

    private void OnNext(object sender, RoutedEventArgs e) => Run(Transport.NextAsync);

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e) => Transport.SetVolume((float)(e.NewValue / 100));

    private void OnReturn(object sender, RoutedEventArgs e) => ReturnRequested?.Invoke(this, EventArgs.Empty);

    private void OnArtDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        ReturnRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeepOnTop(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = KeepOnTop.IsChecked == true;
            Serilog.Log.Debug("Mini player keep on top {On}", presenter.IsAlwaysOnTop);
        }
    }

    // Volume on hover (docs: "volume on hover"): the slider is there while the pointer is.
    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => VolumeSlider.Visibility = Visibility.Visible;

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => VolumeSlider.Visibility = Visibility.Collapsed;

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange)
        {
            return;
        }

        // Restarted on every move, so it fires once the window has stopped moving.
        _snapTimer.Stop();
        _snapTimer.Start();
    }

    private void OnSnapTimer(DispatcherQueueTimer sender, object args)
    {
        RectInt32 work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var window = new ScreenRect(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        if (MiniPlayerSnap.Snap(window, new ScreenRect(work.X, work.Y, work.Width, work.Height)) is { } to)
        {
            AppWindow.Move(new PointInt32(to.X, to.Y));
            Serilog.Log.Debug("Mini player snapped to {X},{Y}", to.X, to.Y);
        }
    }

    private static void Run(Func<Task> action) => RunAsync(action).Forget("Mini player transport");

    private static async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Serilog.Log.Error(ex, "A mini player transport command failed");
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint hwnd);
}
#pragma warning restore CA1001
