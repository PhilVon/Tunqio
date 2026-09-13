using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Tunqio.App.Controls;
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
    private readonly NowPlayingViewModel? _nowPlaying;
    private readonly QueueViewModel? _queue;
    private readonly ShellNotices _notices;
    private readonly bool _ownsNotices;
    private readonly DiagnosticsViewModel _diagnostics;
    private readonly OpenCoordinator? _open;
    private ReactiveThemeLayer? _reactiveLayer;
    private SystemAccessibilitySignals? _accessibility;
    private ReactiveThemeController? _reactiveTheme;
    private volatile bool _isDark;
    private readonly IVisualizationHost? _visualization;
    private readonly VisualizerArtLink? _artLink;
    private bool _rendererAttached;
    private bool _panelLoaded;
    private bool _audioSettled;
    private nint _audioEngineNative;

    /// <param name="forceWarp">Render through WARP rather than the adapter (the E0-S5 spike).</param>
    /// <param name="settings">Read for <c>ui.theme</c>; the system theme is used when it is not supplied.</param>
    /// <param name="audio">Where the transport finds the session; it may not exist yet, and may never.</param>
    /// <param name="navigator">The sidebar, for Now Playing's artist and album links; null leaves them inert.</param>
    /// <param name="open">Files and folders opened or dropped (E2-S4); null leaves the window inert to drops.</param>
    /// <param name="tracks">Resolves the queue panel's rows (E2-S5); null leaves the panel showing its empty state.</param>
    /// <param name="scans">The library's scans, for the scan report bar (E2-S7); null leaves scans unreported.</param>
    /// <param name="visualization">
    /// The visualizer surface (E4-S9). The window attaches it to the SwapChainPanel once the panel has loaded and
    /// detaches it on close; Settings › Visualization drives the same object out of the container, which is the
    /// whole reason it is passed in rather than built here. Null leaves the panel blank, which is what the tests
    /// that construct the window directly get.
    /// </param>
    /// <param name="notices">
    /// The shell's notice bars. Supplied by the host so that the window's panel and the tag editor's Undo bar
    /// (E3-S10, flow 8) are the same object — the dialog resolves it from the container, and a window holding a
    /// second one would leave that bar with nowhere to appear. Null builds one, which is what the spike modes and
    /// the tests that construct the window directly get; only then does the window dispose it.
    /// </param>
    /// <param name="art">
    /// The album art cache (T-147). What turns the track now playing into the colours the Ambient Glow preset
    /// draws with and the tint the reactive theme carries; null leaves both on their own palettes.
    /// </param>
    public MainWindow(
        bool forceWarp = false,
        ISettingsStore? settings = null,
        IPlaybackSessionSource? audio = null,
        Library.ILibraryNavigator? navigator = null,
        OpenCoordinator? open = null,
        Core.Library.ITrackRepository? tracks = null,
        Library.LibraryScanCoordinator? scans = null,
        ShellNotices? notices = null,
        IVisualizationHost? visualization = null,
        Core.Library.IArtCache? art = null)
    {
        _forceWarp = forceWarp;
        _settings = settings;
        _open = open;
        _visualization = visualization;
        InitializeComponent();
        Title = Identity.WindowTitle(null, null);

        _chrome = new ShellChrome(Root, ShellGrid, NowPlayingColumn, SidebarPanel, ControlsPanel);
        // Before the first frame: the theme a repaint would otherwise arrive one frame late in, and a shape, so
        // the window never draws with all three panels stacked on top of each other in column 0.
        _chrome.ApplyTheme(settings is null ? ThemePreference.System : ThemePolicy.Read(settings));
        _chrome.ApplyLayout(ShellLayout.MediumThreshold);
        Root.SizeChanged += (_, e) => _chrome.ApplyLayout(e.NewSize.Width);
        // Which theme is on screen decides which text the reactive contrast guarantee is made against, and the
        // theming ticks on a timer thread where ActualTheme cannot be read at all. So it is cached here, on the
        // thread that owns it, and the theming reads the cache.
        _isDark = Root.ActualTheme == ElementTheme.Dark;
        Root.ActualThemeChanged += (_, _) =>
        {
            _isDark = Root.ActualTheme == ElementTheme.Dark;
            _reactiveTheme?.Evaluate();
        };
        // The error surfaces (E2-S7). Bound before the session is attached below, so a failure during start-up has
        // somewhere to be said.
        _ownsNotices = notices is null;
        _notices = notices ?? new ShellNotices(audio, scans, SynchronizationContext.Current);
        Notices.ViewModel = _notices;

        // The diagnostics overlay (E2-S8). It reads the renderer through a delegate rather than being handed one,
        // because the renderer does not exist until the swap-chain panel has loaded and may never exist at all.
        _diagnostics = new DiagnosticsViewModel(
            audio, RendererStats, DescribeEngine(), SynchronizationContext.Current, theming: ThemingStatus,
            rendererAudioSource: RendererAudioSource);
        Diagnostics.ViewModel = _diagnostics;

        if (audio is not null)
        {
            _transport = new TransportViewModel(audio, SynchronizationContext.Current);
            Transport.ViewModel = _transport;
            // The sidebar's navigator is what makes the artist and album lines links (E2-S3); it is not there in
            // the spike modes, and the panel simply leaves them inert when it is missing.
            _nowPlaying = new NowPlayingViewModel(audio, navigator, SynchronizationContext.Current);
            NowPlaying.ViewModel = _nowPlaying;
            // The queue panel needs the library to turn track ids into rows; without it the button opens an empty
            // panel, which is what the spike modes get and is honest about what they have.
            if (tracks is not null)
            {
                _queue = new QueueViewModel(audio, tracks, SynchronizationContext.Current);
                QueuePanelControl.ViewModel = _queue;
            }

            // Now Playing to the visualizer's colours (T-147). Built here rather than with the renderer because
            // it watches the session, which exists now; the renderer attaches when the panel loads, and the link
            // simply says nothing until it has.
            if (visualization is not null)
            {
                _artLink = new VisualizerArtLink(audio, visualization, art, SynchronizationContext.Current);
                _artLink.PaletteChanged += (_, palette) => _reactiveTheme?.SetArtPalette(palette);
            }
        }

        // Registered whether or not audio came up: a shortcut with nothing to act on leaves the key unhandled,
        // which is a better shape than a table that exists only on the machines where start-up went well.
        AddShellShortcuts();

        if (_open is not null)
        {
            NowPlaying.OpenRequested += OnOpenRequested;
        }

        VisualizerPanel.Loaded += OnPanelLoaded;
        VisualizerPanel.SizeChanged += (_, _) => ForwardPanelSize();
        VisualizerPanel.CompositionScaleChanged += (_, _) => ForwardPanelSize();
        Closed += (_, _) =>
        {
            _transport?.Dispose();
            _nowPlaying?.Dispose();
            _queue?.Dispose();
            // Only the one this window built. A container-owned ShellNotices is disposed with the host, and
            // disposing it here would take the notice bars down for anything still using them during shutdown.
            if (_ownsNotices)
            {
                _notices.Dispose();
            }

            _diagnostics.Dispose();
            _artLink?.Dispose();
            _reactiveTheme?.Dispose();
            _accessibility?.Dispose();
            _reactiveLayer?.Dispose();
            TearDownRenderer();
        };
    }

    /// <summary>
    /// Starts audio-reactive theming (E4-S6) over <paramref name="frames"/>. Called once the audio engine is up,
    /// which is after the window is shown, because the analysis stream does not exist until the engine does; a
    /// session with no audio simply never calls it and the window keeps its static theme.
    /// </summary>
    public void AttachReactiveTheming(IAnalysisFrameSource frames, ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(settings);
        if (_reactiveTheme is not null)
        {
            return;
        }

        _reactiveLayer = new ReactiveThemeLayer(ReactiveLayer);
        _accessibility = new SystemAccessibilitySignals();
        // The visualizer is passed (T-156): without it mp_renderer_set_theme has no caller in the app, and the
        // sixteen floats E4-S6 added to every preset's b0 carry nothing. The controller tolerates a host that is
        // detached or absent; see PushToRenderer for why the guard is there and not on the host.
        _reactiveTheme = new ReactiveThemeController(
            frames, settings, _accessibility, _reactiveLayer, () => _isDark, _visualization);
        // The track already playing, if there is one: the link loaded its palette before this controller existed.
        _reactiveTheme.SetArtPalette(_artLink?.Palette);
        _reactiveTheme.Start();
    }

    /// <summary>The reactive theming, for the diagnostics overlay and the tests that drive the window.</summary>
    internal ReactiveThemeController? ReactiveTheming => _reactiveTheme;

    /// <summary>Now Playing's colours as the visualizer takes them (T-147), for the tests that drive the window.</summary>
    internal VisualizerArtLink? ArtLink => _artLink;

    /// <summary>
    /// Everything the overlay says about audio-reactive theming (T-155), gathered from the three objects that
    /// each know part of it: the controller's state and counts, the layer's painted colours, and the art link's
    /// album. Null before the theming has been started at all, which is a different thing from stopped.
    /// </summary>
    private ReactiveThemeStatus? ThemingStatus() =>
        _reactiveTheme is not { } theming
            ? null
            : new ReactiveThemeStatus(
                theming.Active,
                theming.StoppedBecause,
                _reactiveLayer?.Painted,
                theming.Ticks,
                theming.Applied,
                theming.RendererPushes,
                theming.RendererSkips,
                theming.RendererProblem,
                _artLink?.Hash,
                _artLink?.Palette?.Colors);

    /// <summary>The visualizer surface bound to the panel, once the panel has loaded and if there is one.</summary>
    public IVisualizationHost? Renderer => _rendererAttached ? _visualization : null;

    /// <summary>The shape the shell is in, for the tests that drive the window and for the diagnostics overlay.</summary>
    public ShellLayoutMode? LayoutMode => _chrome.Mode;

    /// <summary>Whether the window will take a drag (E2-S4), read off the live tree for the spike.</summary>
    internal bool RootAcceptsDrop => Root.AllowDrop;

    /// <summary>The Now Playing panel, for the E2-S3 spike, which drives it without a session.</summary>
    internal NowPlayingPanel NowPlayingPanelControl => NowPlaying;

    /// <summary>The stored theme preference; the system theme when there is no settings store to read.</summary>
    public ThemePreference ThemePreference => _settings is null ? ThemePreference.System : ThemePolicy.Read(_settings);

    /// <summary>
    /// The stored visualizer quality policy (<c>viz.quality</c>), applied to the renderer when it is created;
    /// <see cref="Core.Visualization.QualityPolicy.Auto"/> when there is no settings store to read, which is
    /// also the core's own default.
    /// </summary>
    public QualityPolicy QualityPolicy =>
        _settings is null ? QualityPolicy.Auto : QualityPolicyStore.Read(_settings);

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
            expected, Root.ActualWidth, NowPlayingColumn.ActualWidth, SidebarPanel.ActualWidth, ControlsPanel.ActualWidth);
        double total = NowPlayingColumn.ActualWidth + SidebarPanel.ActualWidth + ControlsPanel.ActualWidth;
        double share(double width) => total <= 0 ? 0 : Math.Round(width / total, 4);
        return new ShellMeasurement(
            requestedWidth,
            expected.Mode.ToString(),
            expected.Stacked,
            Math.Round(NowPlayingColumn.ActualWidth, 1),
            Math.Round(SidebarPanel.ActualWidth, 1),
            Math.Round(ControlsPanel.ActualWidth, 1),
            share(NowPlayingColumn.ActualWidth),
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
    /// Registers the shell's shortcuts (E2-S6). What each one is and how it has to be delivered is
    /// <see cref="ShellShortcuts"/>'s table; this is the two ways of listening it names — a tunnelling handler at
    /// the root for the keys an ordinary control would otherwise eat, and a <c>KeyboardAccelerator</c> for the
    /// arrows, which fire only on a key the focused grid or list did not want.
    /// </summary>
    private void AddShellShortcuts()
    {
        Root.PreviewKeyDown += OnShellKeyDown;
        foreach (ShellShortcut shortcut in ShellShortcuts.Accelerated)
        {
            var accelerator = new KeyboardAccelerator { Key = shortcut.Key, Modifiers = shortcut.Modifiers };
            ShellShortcut invoked = shortcut;
            accelerator.Invoked += (_, args) => args.Handled = Invoke(invoked);
            Root.KeyboardAccelerators.Add(accelerator);
        }
    }

    /// <summary>
    /// The pre-empting half of the table, taken on the way down so a focused <c>Button</c> or list never sees the
    /// key. <c>PreviewKeyDown</c> tunnels from the root, so this runs before the control that has focus — which is
    /// the whole point: Space over a library tile has to toggle playback and not press the tile (AC-77).
    /// </summary>
    private void OnShellKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e is null || e.Handled)
        {
            return;
        }

        ShellShortcut? found = ShellShortcuts.Find(e.Key, CurrentModifiers(), IsTypingSomewhere());
        if (found is not { Delivery: ShortcutDelivery.PreEmpt } shortcut)
        {
            return;
        }

        e.Handled = Invoke(shortcut);
    }

    /// <summary>
    /// Does what <paramref name="shortcut"/> asks, and says whether it was done — a shortcut with nothing to act
    /// on leaves the key unhandled, so Q before audio is up is still a Q and not a keystroke swallowed in silence.
    /// </summary>
    private bool Invoke(ShellShortcut shortcut)
    {
        if (shortcut.Command == ShellCommand.Diagnostics)
        {
            _diagnostics.Toggle();
            Diagnostics.Visibility = _diagnostics.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            return true;
        }

        if (shortcut.Command == ShellCommand.Queue)
        {
            // The queue is a flyout on its button (E2-S5); showing it from here is the same gesture as clicking it.
            if (QueuePanelControl.ViewModel is null)
            {
                return false;
            }

            QueueButton.Flyout.ShowAt(QueueButton);
            return true;
        }

        if (_transport is not { } vm)
        {
            return false;
        }

        _ = RunSafelyAsync(
            transport => shortcut.Command switch
            {
                ShellCommand.PlayPause => transport.PlayPauseAsync(),
                ShellCommand.Next => transport.NextAsync(),
                ShellCommand.Previous => transport.PreviousAsync(),
                ShellCommand.Seek => transport.NudgeAsync(TimeSpan.FromSeconds(shortcut.Amount)),
                ShellCommand.Shuffle => transport.ToggleShuffleAsync(),
                ShellCommand.Repeat => transport.CycleRepeatAsync(),
                ShellCommand.Volume => DoneAsync(() => transport.SetVolume(transport.Volume + (float)shortcut.Amount)),
                _ => DoneAsync(transport.ToggleMute),
            },
            vm);
        return true;

        static Task DoneAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Which modifiers are down right now. <c>PreviewKeyDown</c> reports the key but not the modifiers, and the
    /// table matches them exactly, because Ctrl+Space is not Space and Shift+S is a capital S.
    /// </summary>
    private static Windows.System.VirtualKeyModifiers CurrentModifiers()
    {
        var modifiers = Windows.System.VirtualKeyModifiers.None;
        if (Controls.Modifiers.Control)
        {
            modifiers |= Windows.System.VirtualKeyModifiers.Control;
        }

        if (Controls.Modifiers.Shift)
        {
            modifiers |= Windows.System.VirtualKeyModifiers.Shift;
        }

        if (Controls.Modifiers.IsDown(Windows.System.VirtualKey.Menu))
        {
            modifiers |= Windows.System.VirtualKeyModifiers.Menu;
        }

        if (Controls.Modifiers.IsDown(Windows.System.VirtualKey.LeftWindows)
            || Controls.Modifiers.IsDown(Windows.System.VirtualKey.RightWindows))
        {
            modifiers |= Windows.System.VirtualKeyModifiers.Windows;
        }

        return modifiers;
    }

    /// <summary>
    /// True when focus is in something that wants the keystroke more than the transport does. Checked for every
    /// shortcut and not only the single-letter ones: Space in a text box is a space, and the arrows are how
    /// anyone moves a caret.
    /// </summary>
    /// <remarks>
    /// The <c>XamlRoot</c> is not optional. The parameterless <c>GetFocusedElement()</c> answers for the calling
    /// thread's <c>CoreWindow</c>, which a desktop WinUI app does not have, so it returns null however deep in a
    /// text box the caret is — and a check that is always false is a check that reads as an opt-out and is not one.
    /// That is what <c>tools/check-shortcuts.ps1</c> caught: S typed into the search box shuffled the queue, which
    /// is the exact thing the design document says must not happen.
    /// </remarks>
    private bool IsTypingSomewhere() =>
        Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot)
            is TextBox or RichEditBox or AutoSuggestBox or PasswordBox;

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

    // ---- open files and drop (E2-S4) ---------------------------------------------------------------------------

    /// <summary>
    /// What the window accepts. Storage items only: a drag of text or an image has nothing to play, and saying
    /// so by declining the drag is better than accepting it and then explaining.
    /// </summary>
    private void OnDragOver(object sender, DragEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (_open is null || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Play";
        e.DragUIOverride.IsGlyphVisible = true;
        DropHint.Visibility = Visibility.Visible;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Takes the drop. The deferral is what makes reading the data package legal: <c>GetStorageItemsAsync</c> is
    /// asynchronous and the drag is over the moment this handler returns, so without one the items are gone
    /// before they arrive. It is taken here, synchronously, and completed by the continuation.
    /// </summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (_open is null || e is null || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }

        var deferral = e.GetDeferral();
        Windows.ApplicationModel.DataTransfer.DataPackageView data = e.DataView;
        _ = OpenDroppedAsync();

        async Task OpenDroppedAsync()
        {
            try
            {
                IReadOnlyList<Windows.Storage.IStorageItem> items;
                try
                {
                    items = await data.GetStorageItemsAsync();
                }
                finally
                {
                    deferral.Complete();
                }

                string[] paths = [.. items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p))];
                if (paths.Length == 0)
                {
                    return;
                }

                await _open.OpenDroppedAsync(paths).ConfigureAwait(true);
                ShowOpenNotice();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Serilog.Log.Error(ex, "A drop could not be opened");
            }
        }
    }

    /// <summary>
    /// The empty state's two buttons (E2-S4). Void over a task, which is the shape XAML gives; the exception is
    /// logged rather than lost, per the error-handling policy in docs/solution-structure.md.
    /// </summary>
    private void OnOpenRequested(object? sender, OpenRequest request)
    {
        if (_open is null)
        {
            return;
        }

        _ = PickAsync();

        async Task PickAsync()
        {
            try
            {
                _ = request == OpenRequest.Files
                    ? await _open.OpenFilesAsync().ConfigureAwait(true)
                    : await _open.OpenFolderAsync().ConfigureAwait(true);
                ShowOpenNotice();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Serilog.Log.Error(ex, "An open request failed");
            }
        }
    }

    /// <summary>Shows whatever the last open had to say, and nothing when it had nothing.</summary>
    private void ShowOpenNotice()
    {
        if (_open?.LastNotice is { } notice)
        {
            ShowNotice(notice);
        }
    }

    /// <summary>Shows a start-up notice in the shell's notice area (closable; one start-up notice at a time).</summary>
    public void ShowNotice(StartupNotice notice) => _notices.Show(notice);

    private void OnPanelLoaded(object sender, RoutedEventArgs e)
    {
        _panelLoaded = true;
        TryAttachVisualizer();
    }

    /// <summary>
    /// The engine the visualizer is to be drawn from, once start-up knows whether there is one (T-179). Called
    /// on every path out of <see cref="AudioStartup.StartAsync"/> - including the ones that produced no engine,
    /// where the handle is <see cref="nint.Zero"/> - because the visualizer must not be held hostage to audio
    /// that never arrives.
    /// </summary>
    /// <remarks>
    /// <b>Why the attach waits for this rather than happening on panel load.</b> <c>mp_renderer_create</c> takes
    /// the engine and the renderer keeps it for life; there is no export that binds one afterwards. The panel
    /// loads on the first layout pass and the engine is deliberately built after the first frame
    /// (docs/solution-structure.md, start-up step 3), so a visualizer attached on panel load is attached before
    /// any engine exists - which is exactly how T-179 shipped a visualizer that could not react. Waiting costs
    /// the few hundred milliseconds it takes to load mpcore and open the device, during which nothing is
    /// playing and the panel would have been showing an idle animation anyway.
    /// </remarks>
    public void AttachVisualizerAudio(nint audioEngineNative)
    {
        _audioEngineNative = audioEngineNative;
        _audioSettled = true;
        TryAttachVisualizer();
    }

    private void TryAttachVisualizer()
    {
        if (_visualization is null || _rendererAttached || !_panelLoaded || !_audioSettled)
        {
            return;
        }

        // Fire-and-forget by the repository's no-async-void rule (docs/solution-structure.md), and synchronous
        // in fact: AttachAsync builds the device, the swap chain and the render thread before it returns, so the
        // renderer exists by the time this handler does.
        AttachVisualizerAsync().Forget("Attach the visualizer");
    }

    private async Task AttachVisualizerAsync()
    {
        if (_visualization is null)
        {
            return;
        }

        try
        {
            // The panel's IUnknown; the core queries ISwapChainPanelNative and calls SetSwapChain on this (UI) thread.
            nint panelNative = ((IWinRTObject)VisualizerPanel).NativeObject.ThisPtr;
            (int width, int height) = PanelPixelSize();
            await _visualization.AttachAsync(panelNative, _audioEngineNative, new RendererConfig(
                width, height, VisualizerPanel.CompositionScaleX, VisualizerPanel.CompositionScaleY, _forceWarp, VSync: true))
                .ConfigureAwait(true);
            _rendererAttached = true;
            // viz.quality (E4-S7). The core's own default is Auto, so this only ever matters when someone has
            // pinned a tier - but a setting nothing reads is a setting that does not exist, and the point of
            // pinning is to be able to stop a controller from having opinions about your machine. Written
            // against NativeRenderer on E4-S7's branch and moved onto the host here, because E4-S9 landed first
            // and replaced the direct NativeRenderer.Create with IVisualizationHost.AttachAsync.
            _visualization.SetQualityPolicy(QualityPolicy);
            await RestoreVisualizationSettingsAsync().ConfigureAwait(true);
            // Now there is a preset to tell. Until this point the track playing had a palette and nowhere to
            // put it, because the panel loads after the window and the catalogue after the device (T-147).
            _artLink?.Reapply();
        }
        catch (Exception ex) when (ex is NativeException or DllNotFoundException)
        {
            _diagnostics.RendererProblem = "unavailable: " + ex.Message;
            _diagnostics.Refresh();
            Serilog.Log.Warning(ex, "The renderer could not be created");
        }
    }

    /// <summary>
    /// The user preset root and the preset <c>viz.preset</c> remembers (E4-S9), applied as soon as the renderer
    /// exists. Both are best-effort: a preset directory that has gone, or a preset whose shader no longer
    /// compiles, costs the user the preset they chose and not the visualizer.
    /// </summary>
    private async Task RestoreVisualizationSettingsAsync()
    {
        if (_visualization is null)
        {
            return;
        }

        try
        {
            if (App.Services.GetService(typeof(IAppPaths)) is IAppPaths paths)
            {
                _visualization.SetUserPresetRoot(paths.PresetsDirectory);
            }
        }
        catch (Exception ex) when (ex is NativeException or InvalidOperationException)
        {
            Serilog.Log.Warning(ex, "The user preset directory could not be given to the renderer");
        }

        string? wanted = _settings?.GetValue(SettingsKeys.VizPreset, SettingsKeys.Defaults.VizPreset);
        if (string.IsNullOrEmpty(wanted) || !_visualization.Presets.Any(p => p.Id == wanted))
        {
            return;
        }

        try
        {
            await _visualization.SetPresetAsync(wanted).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is PresetCompilationException or NativeException)
        {
            Serilog.Log.Warning(ex, "The remembered preset {Preset} could not be loaded", wanted);
        }
    }

    /// <summary>
    /// The renderer's statistics for the overlay, or null when there is no renderer. Called on the UI thread by
    /// the overlay's own refresh, so nothing here has to marshal.
    /// </summary>
    private RenderStats? RendererStats() => _rendererAttached ? _visualization?.TryGetStats() : null;

    /// <summary>
    /// Whether the attached visualizer has an engine to draw from (T-179), for the diagnostics overlay. Null
    /// while nothing is attached, which the overlay reports as "unknown" rather than as "no".
    /// </summary>
    private bool? RendererAudioSource() => _rendererAttached ? _visualization?.HasAudioSource : null;

    private (int Width, int Height) PanelPixelSize()
    {
        int width = Math.Max(1, (int)Math.Round(VisualizerPanel.ActualWidth * VisualizerPanel.CompositionScaleX));
        int height = Math.Max(1, (int)Math.Round(VisualizerPanel.ActualHeight * VisualizerPanel.CompositionScaleY));
        return (width, height);
    }

    private void ForwardPanelSize()
    {
        if (!_rendererAttached || _visualization is null)
        {
            return;
        }

        (int width, int height) = PanelPixelSize();
        _visualization.Resize(width, height, VisualizerPanel.CompositionScaleX, VisualizerPanel.CompositionScaleY);
    }

    private void TearDownRenderer()
    {
        if (!_rendererAttached)
        {
            return;
        }

        // Detached and not disposed: the host is the container's, and the container outlives this window.
        _visualization?.Detach();
        _rendererAttached = false;
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
