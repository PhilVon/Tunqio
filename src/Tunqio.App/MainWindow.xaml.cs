using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Tunqio.App.Playback;
using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Core.Visualization;
using Tunqio.Interop;
using WinRT;

namespace Tunqio.App;

/// <summary>
/// The shell window (E2-S1): three panels whose shape follows the window width, over a system backdrop, in the
/// theme <c>ui.theme</c> asks for. It also still carries E0-S5's renderer surface and readout, which E2-S8 will
/// move into the diagnostics overlay.
/// </summary>
/// <remarks>
/// The layout and theme rules live in <see cref="ShellLayout"/> and <see cref="ThemePolicy"/>, and
/// <see cref="ShellChrome"/> is the only part that touches the visual tree. That split is deliberate: the
/// criteria here are about proportions and about a theme change not flashing, and neither can be asserted about a
/// rule that exists only as a <c>VisualState</c>.
/// </remarks>
#pragma warning disable CA1001 // The window's Closed handler is its teardown; it cannot implement IDisposable.
public sealed partial class MainWindow : Window
{
    private readonly bool _forceWarp;
    private readonly ShellChrome _chrome;
    private readonly ISettingsStore? _settings;
    private readonly TransportViewModel? _transport;
    private NativeRenderer? _renderer;
    private DispatcherQueueTimer? _statsTimer;

    /// <param name="forceWarp">Render through WARP rather than the adapter (the E0-S5 spike).</param>
    /// <param name="settings">Read for <c>ui.theme</c>; the system theme is used when it is not supplied.</param>
    /// <param name="audio">Where the transport finds the session; it may not exist yet, and may never.</param>
    public MainWindow(bool forceWarp = false, ISettingsStore? settings = null, IPlaybackSessionSource? audio = null)
    {
        _forceWarp = forceWarp;
        _settings = settings;
        InitializeComponent();
        Title = Identity.WindowTitle(null, null);

        _chrome = new ShellChrome(Root, ShellGrid, NowPlayingPanel, SidebarPanel, ControlsPanel);
        // Before the first frame: the theme a repaint would otherwise arrive one frame late in, and a shape, so
        // the window never draws with all three panels stacked on top of each other in column 0.
        _chrome.ApplyTheme(settings is null ? ThemePreference.System : ThemePolicy.Read(settings));
        _chrome.ApplyLayout(ShellLayout.MediumThreshold);
        Root.SizeChanged += (_, e) => _chrome.ApplyLayout(e.NewSize.Width);
        ProductText.Text = Identity.ProductName;
        VersionText.Text = string.Create(CultureInfo.InvariantCulture, $"Version {ProductVersion()}");
        // About-page placeholder (E0-S3): the BASS attribution is shown until E6-S5 builds the real page.
        EngineText.Text = DescribeEngine() + Environment.NewLine + ThirdPartyAttribution.Bass;

        if (audio is not null)
        {
            _transport = new TransportViewModel(audio, SynchronizationContext.Current);
            Transport.ViewModel = _transport;
            AddTransportShortcuts();
        }

        VisualizerPanel.Loaded += OnPanelLoaded;
        VisualizerPanel.SizeChanged += (_, _) => ForwardPanelSize();
        VisualizerPanel.CompositionScaleChanged += (_, _) => ForwardPanelSize();
        Closed += (_, _) =>
        {
            _transport?.Dispose();
            TearDownRenderer();
        };
    }

    /// <summary>The native renderer bound to the panel, once the panel has loaded.</summary>
    public NativeRenderer? Renderer => _renderer;

    /// <summary>The shape the shell is in, for the tests that drive the window and for the diagnostics overlay.</summary>
    public ShellLayoutMode? LayoutMode => _chrome.Mode;

    /// <summary>The stored theme preference; the system theme when there is no settings store to read.</summary>
    public ThemePreference ThemePreference => _settings is null ? ThemePreference.System : ThemePolicy.Read(_settings);

    /// <summary>
    /// Switches the theme and remembers the choice. The Appearance page (E6-S3) is what will call this; it lives
    /// here because the repaint has to happen on the shell's root for it to be a repaint rather than a reload.
    /// </summary>
    public void SetTheme(ThemePreference preference)
    {
        _chrome.ApplyTheme(preference);
        if (_settings is not null)
        {
            ThemePolicy.Write(_settings, preference);
            _settings.Flush();
        }
    }

    /// <summary>
    /// Mica on Windows 11, desktop acrylic on Windows 10, neither where the machine has no backdrop at all. Called
    /// after the window exists because a backdrop needs its target, and before it is activated so the first frame
    /// is already painted (AC-69: the white flash to avoid is the one before anything has drawn).
    /// </summary>
    /// <remarks>
    /// The root's background is decided here rather than in XAML because it depends on the answer: an opaque root
    /// would hide the very material this method just installed, and the layered Fluent the design asks for (Mica
    /// base, acrylic sidebar, solid cards) needs the base to show through. With no backdrop there is nothing to
    /// show through to, and the root has to paint something itself.
    /// </remarks>
    public ShellBackdrop.Kind ApplyBackdrop()
    {
        (Microsoft.UI.Xaml.Media.SystemBackdrop? backdrop, ShellBackdrop.Kind which) = ShellBackdrop.Choose();
        SystemBackdrop = backdrop;
        Root.Background = which == ShellBackdrop.Kind.None
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"]
            : null;
        return which;
    }

    /// <summary>
    /// What the three panels actually came out at, for the shell spike (E2-S1). Read from the live visual tree
    /// rather than recomputed, because the point of the measurement is whether the policy reached the tree.
    /// </summary>
    internal ShellMeasurement MeasurePanels(int requestedWidth)
    {
        ShellLayoutState expected = ShellLayout.For(Root.ActualWidth);
        (bool pass, string note) = ShellSpikeRunner.Judge(
            expected, Root.ActualWidth, NowPlayingPanel.ActualWidth, SidebarPanel.ActualWidth, ControlsPanel.ActualWidth);
        double total = NowPlayingPanel.ActualWidth + SidebarPanel.ActualWidth + ControlsPanel.ActualWidth;
        double share(double width) => total <= 0 ? 0 : Math.Round(width / total, 4);
        return new ShellMeasurement(
            requestedWidth,
            expected.Mode.ToString(),
            expected.Stacked,
            Math.Round(NowPlayingPanel.ActualWidth, 1),
            Math.Round(SidebarPanel.ActualWidth, 1),
            Math.Round(ControlsPanel.ActualWidth, 1),
            share(NowPlayingPanel.ActualWidth),
            share(SidebarPanel.ActualWidth),
            share(ControlsPanel.ActualWidth),
            Root.ActualTheme.ToString(),
            pass,
            note);
    }

    /// <summary>
    /// Switches the theme and reports what happened, for the shell spike (AC-69). The two things worth recording
    /// are that the new theme was in force by the time the call returned — a switch that needed a frame would show
    /// as an unchanged <c>ActualTheme</c> here — and that the shell's content is the same object it was, which is
    /// what makes it a repaint rather than a reload with a blank frame in the middle.
    /// </summary>
    internal ThemeSwitch SwitchThemeAndReport(ThemePreference preference)
    {
        object contentBefore = Content;
        ElementTheme from = Root.ActualTheme;
        string backdrop = SystemBackdrop is null ? "none" : SystemBackdrop.GetType().Name;

        SetTheme(preference);

        ElementTheme to = Root.ActualTheme;
        bool contentUnchanged = ReferenceEquals(contentBefore, Content);
        // System resolves to whatever Windows is, so the only thing it can be asserted to equal is itself.
        ElementTheme wanted = preference switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            _ => to,
        };
        bool applied = to == wanted;
        bool pass = applied && contentUnchanged;
        string note = pass
            ? "applied in place; the shell's content object is the one it was"
            : applied
                ? "applied, but the content object changed — that is a reload, and a reload is where a flash comes from"
                : $"asked for {wanted} and ActualTheme is {to}; the switch did not take synchronously";
        return new ThemeSwitch(from.ToString(), preference.ToString(), to.ToString(), contentUnchanged, applied, backdrop, pass, note);
    }

    /// <summary>
    /// The transport shortcuts from docs/ui-screens-and-flows.md, registered on the shell's root so they work
    /// wherever focus is (E2-S2, AC-70). The single-letter ones are the reason they are checked rather than simply
    /// fired: S, M and Space belong to whatever text box has focus first, and a search box that shuffles the queue
    /// every time someone types an S is not a search box.
    /// </summary>
    private void AddTransportShortcuts()
    {
        Add(Windows.System.VirtualKey.Space, Windows.System.VirtualKeyModifiers.None, vm => vm.PlayPauseAsync());
        Add(Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.Control, vm => vm.NextAsync());
        Add(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.Control, vm => vm.PreviousAsync());
        Add(Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.None, vm => vm.NudgeAsync(TimeSpan.FromSeconds(5)));
        Add(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.None, vm => vm.NudgeAsync(TimeSpan.FromSeconds(-5)));
        Add(Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.Shift, vm => vm.NudgeAsync(TimeSpan.FromSeconds(30)));
        Add(Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.Shift, vm => vm.NudgeAsync(TimeSpan.FromSeconds(-30)));
        Add(Windows.System.VirtualKey.S, Windows.System.VirtualKeyModifiers.None, vm => vm.ToggleShuffleAsync());
        Add(Windows.System.VirtualKey.R, Windows.System.VirtualKeyModifiers.None, vm => vm.CycleRepeatAsync());
        Add(Windows.System.VirtualKey.Up, Windows.System.VirtualKeyModifiers.None, vm => VolumeAsync(vm, +0.05f));
        Add(Windows.System.VirtualKey.Down, Windows.System.VirtualKeyModifiers.None, vm => VolumeAsync(vm, -0.05f));
        Add(Windows.System.VirtualKey.M, Windows.System.VirtualKeyModifiers.None, vm => { vm.ToggleMute(); return Task.CompletedTask; });

        static Task VolumeAsync(TransportViewModel vm, float by)
        {
            vm.SetVolume(vm.Volume + by);
            return Task.CompletedTask;
        }

        void Add(Windows.System.VirtualKey key, Windows.System.VirtualKeyModifiers modifiers, Func<TransportViewModel, Task> action)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (invoked, args) =>
            {
                if (_transport is null || IsTypingSomewhere())
                {
                    return;
                }

                args.Handled = true;
                _ = RunSafelyAsync(action, _transport);
            };
            Root.KeyboardAccelerators.Add(accelerator);
        }
    }

    /// <summary>
    /// True when focus is in something that wants the keystroke more than the transport does. Checked for every
    /// accelerator and not only the single-letter ones: Space in a text box is a space, and the arrows are how
    /// anyone moves a caret.
    /// </summary>
    private static bool IsTypingSomewhere() =>
        Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement() is TextBox or RichEditBox or AutoSuggestBox or PasswordBox;

    private static async Task RunSafelyAsync(Func<TransportViewModel, Task> action, TransportViewModel vm)
    {
        try
        {
            await action(vm).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Serilog.Log.Error(e, "A transport shortcut failed");
        }
    }

    /// <summary>Shows a start-up notice in the window's InfoBar (closable; one at a time).</summary>
    public void ShowNotice(StartupNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        NoticeBar.Title = notice.Title;
        NoticeBar.Message = notice.Message;
        NoticeBar.Severity = notice.Severity switch
        {
            StartupNoticeSeverity.Error => InfoBarSeverity.Error,
            StartupNoticeSeverity.Warning => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };
        NoticeBar.IsOpen = true;
    }

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        if (_renderer is not null)
        {
            return;
        }

        try
        {
            // The panel's IUnknown; the core queries ISwapChainPanelNative and calls SetSwapChain on this (UI) thread.
            nint panelNative = ((IWinRTObject)VisualizerPanel).NativeObject.ThisPtr;
            (int width, int height) = PanelPixelSize();
            _renderer = NativeRenderer.Create(panelNative, new RendererConfig(
                width, height, VisualizerPanel.CompositionScaleX, VisualizerPanel.CompositionScaleY, _forceWarp, VSync: true));
        }
        catch (Exception ex) when (ex is NativeException or DllNotFoundException)
        {
            RenderText.Text = "Renderer unavailable: " + ex.Message;
            return;
        }

        _statsTimer = DispatcherQueue.CreateTimer();
        _statsTimer.Interval = TimeSpan.FromMilliseconds(500);
        _statsTimer.Tick += (_, _) => RenderText.Text = DescribeRenderer();
        _statsTimer.Start();
    }

    private (int Width, int Height) PanelPixelSize()
    {
        int width = Math.Max(1, (int)Math.Round(VisualizerPanel.ActualWidth * VisualizerPanel.CompositionScaleX));
        int height = Math.Max(1, (int)Math.Round(VisualizerPanel.ActualHeight * VisualizerPanel.CompositionScaleY));
        return (width, height);
    }

    private void ForwardPanelSize()
    {
        if (_renderer is null)
        {
            return;
        }

        (int width, int height) = PanelPixelSize();
        _renderer.Resize(width, height, VisualizerPanel.CompositionScaleX, VisualizerPanel.CompositionScaleY);
    }

    private void TearDownRenderer()
    {
        _statsTimer?.Stop();
        _statsTimer = null;
        _renderer?.Dispose();
        _renderer = null;
    }

    private string DescribeRenderer()
    {
        if (_renderer is null)
        {
            return string.Empty;
        }

        RenderStats s = _renderer.GetStats();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{s.Adapter}{(s.Warp ? " (WARP)" : string.Empty)} · {s.Width}×{s.Height} · {s.Fps:F1} fps · frame avg {s.FrameAverage.TotalMilliseconds:F2} ms, max {s.FrameMax.TotalMilliseconds:F1} ms · missed refreshes {s.DxgiMissedRefreshes} · histogram [{string.Join(", ", s.FrameHistogram)}]");
    }

    private static string ProductVersion()
    {
        string? informational = typeof(MainWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        return informational ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
    }

    private static string DescribeEngine()
    {
        try
        {
            // A native breakpoint on mpcore_abi_version() in mpcore.dll is hit from here (mixed-mode debugging).
            NativeEngineInfo.EnsureAbiCompatible();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"mpcore {NativeEngineInfo.Version} · ABI {NativeEngineInfo.AbiMajor}.{NativeEngineInfo.AbiMinor}");
        }
        catch (DllNotFoundException)
        {
            return "mpcore.dll not found next to the executable. Build native/mpcore first (msbuild Tunqio.sln).";
        }
        catch (NativeAbiMismatchException ex)
        {
            Debug.WriteLine(ex);
            return ex.Message;
        }
    }
}
#pragma warning restore CA1001
