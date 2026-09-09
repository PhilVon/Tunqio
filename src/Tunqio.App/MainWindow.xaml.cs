using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tunqio.Core;
using Tunqio.Core.Visualization;
using Tunqio.Interop;
using WinRT;

namespace Tunqio.App;

/// <summary>
/// Shell window. E0-S1 shows the product identity and proves the managed-to-native call path; E0-S5 hands the
/// SwapChainPanel to the native renderer and shows its frame statistics. The real shell layout arrives with E2-S1.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly bool _forceWarp;
    private NativeRenderer? _renderer;
    private DispatcherQueueTimer? _statsTimer;

    public MainWindow(bool forceWarp = false)
    {
        _forceWarp = forceWarp;
        InitializeComponent();
        Title = Identity.WindowTitle(null, null);
        ProductText.Text = Identity.ProductName;
        VersionText.Text = string.Create(CultureInfo.InvariantCulture, $"Version {ProductVersion()}");
        // About-page placeholder (E0-S3): the BASS attribution is shown until E6-S5 builds the real page.
        EngineText.Text = DescribeEngine() + Environment.NewLine + ThirdPartyAttribution.Bass;

        VisualizerPanel.Loaded += OnPanelLoaded;
        VisualizerPanel.SizeChanged += (_, _) => ForwardPanelSize();
        VisualizerPanel.CompositionScaleChanged += (_, _) => ForwardPanelSize();
        Closed += (_, _) => TearDownRenderer();
    }

    /// <summary>The native renderer bound to the panel, once the panel has loaded.</summary>
    public NativeRenderer? Renderer => _renderer;

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
