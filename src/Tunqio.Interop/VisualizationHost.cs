using System.Reactive.Subjects;
using Tunqio.Core.Visualization;

namespace Tunqio.Interop;

/// <summary>
/// <see cref="IVisualizationHost"/> over <see cref="NativeRenderer"/> (E4-S3).
///
/// <para>
/// Everything here is a forward to one <c>mp_renderer_*</c> call; what the type adds is the lifetime. The native
/// renderer is a handle that owns a D3D device and a thread, and the shell attaches and detaches it as the Now
/// Playing panel comes and goes - so the two states have to be a property the caller can ask about rather than a
/// null reference it has to remember, and every call has to be safe in both.
/// </para>
/// <para>
/// <see cref="Stats"/> polls rather than taking a callback, for the same reason
/// <see cref="NativeAnalysisFrameSource.Frames"/> does: the render thread is paced by the swap chain and must not
/// be made to call into managed code. Twice a second is what a diagnostics overlay reads at.
/// </para>
/// </summary>
public sealed class VisualizationHost : IVisualizationHost
{
    /// <summary>Poll interval for <see cref="Stats"/>. The overlay reads numbers, not motion.</summary>
    public static readonly TimeSpan StatsInterval = TimeSpan.FromMilliseconds(500);

    private readonly Subject<RenderStats> _stats = new();
    private readonly object _gate = new();
    private readonly Timer _timer;
    private NativeRenderer? _renderer;
    private IReadOnlyList<PresetInfo> _presets = [];
    private string? _activePresetId;
    private int _disposed;

    public VisualizationHost() => _timer = new Timer(Poll, null, StatsInterval, StatsInterval);

    public bool IsAttached
    {
        get
        {
            lock (_gate)
            {
                return _renderer is not null;
            }
        }
    }

    public IReadOnlyList<PresetInfo> Presets
    {
        get
        {
            lock (_gate)
            {
                return _presets;
            }
        }
    }

    public string? ActivePresetId
    {
        get
        {
            lock (_gate)
            {
                return _activePresetId;
            }
        }
    }

    public IObservable<RenderStats> Stats => _stats;

    /// <summary>Statistics pushed to <see cref="Stats"/> since construction. Diagnostics and tests.</summary>
    public long Pushed { get; private set; }

    public Task AttachAsync(nint swapChainPanelNative, RendererConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // Created outside the lock: it builds a device, a swap chain and a thread, and it can throw.
        NativeRenderer renderer = config.Headless
            ? NativeRenderer.CreateHeadless(config)
            : NativeRenderer.Create(swapChainPanelNative, config);
        NativeRenderer? previous;
        try
        {
            IReadOnlyList<PresetInfo> presets = renderer.EnumeratePresets();
            lock (_gate)
            {
                previous = _renderer;
                _renderer = renderer;
                _presets = presets;
                // The core starts on the first entry of its own catalogue - the built-in preset, which is the one
                // that cannot be missing. After this, SetPresetAsync is what moves it.
                _activePresetId = presets.Count > 0 ? presets[0].Id : null;
            }
        }
        catch
        {
            renderer.Dispose();
            throw;
        }

        previous?.Dispose();
        return Task.CompletedTask;
    }

    public void Detach()
    {
        NativeRenderer? renderer;
        lock (_gate)
        {
            renderer = _renderer;
            _renderer = null;
            _presets = [];
            _activePresetId = null;
        }

        renderer?.Dispose();
    }

    public void Resize(int width, int height, float scaleX, float scaleY) =>
        Require().Resize(width, height, scaleX, scaleY);

    public void SetVisible(bool visible) => Require().SetVisible(visible);

    /// <inheritdoc />
    /// <remarks>
    /// The compile happens inside the native call, on this thread, which is what makes the failure recoverable:
    /// it either returns having swapped the preset in or throws having changed nothing. So there is nothing to
    /// roll back here, and <see cref="ActivePresetId"/> only moves after the call has come back.
    /// </remarks>
    public Task SetPresetAsync(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        Require().SetPreset(id);
        lock (_gate)
        {
            _activePresetId = id;
        }

        return Task.CompletedTask;
    }

    public void SetParameter(string name, float value) => Require().SetParameter(name, value);

    public IReadOnlyList<PresetParameter> GetPresetParameters(string presetId) => Require().EnumerateParameters(presetId);

    /// <inheritdoc />
    /// <remarks>
    /// The catalogue is rescanned by the native call, so the presets already in the new root are here when this
    /// returns and no <see cref="RefreshPresets"/> is owed after it.
    /// </remarks>
    public void SetUserPresetRoot(string path)
    {
        NativeRenderer renderer = Require();
        renderer.SetUserPresetRoot(path);
        IReadOnlyList<PresetInfo> presets = renderer.EnumeratePresets();
        lock (_gate)
        {
            _presets = presets;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="ActivePresetId"/> is deliberately left alone even when the rescan no longer lists it: the
    /// preset is compiled and still drawing, and reporting it as gone would make the shell think it had lost a
    /// picture it can see.
    /// </remarks>
    public IReadOnlyList<PresetInfo> RefreshPresets()
    {
        NativeRenderer renderer = Require();
        renderer.RescanPresets();
        IReadOnlyList<PresetInfo> presets = renderer.EnumeratePresets();
        lock (_gate)
        {
            _presets = presets;
        }

        return presets;
    }

    public RenderStats? TryGetStats()
    {
        NativeRenderer? renderer;
        lock (_gate)
        {
            renderer = _renderer;
        }

        try
        {
            return renderer?.GetStats();
        }
        catch (ObjectDisposedException)
        {
            // Detached between the read above and the call.
            return null;
        }
    }

    public void SetThemeColors(ThemeColors colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        Require().SetThemeColors([.. colors.Primary], [.. colors.Secondary], [.. colors.Accent], [.. colors.Background]);
    }

    public void SetQualityPolicy(QualityPolicy policy) => Require().SetQuality((int)policy);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _timer.Dispose();
        Detach();
        _stats.OnCompleted();
        _stats.Dispose();
    }

    private NativeRenderer Require()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            return _renderer ?? throw new InvalidOperationException(
                "The visualization host is not attached to a SwapChainPanel; call AttachAsync first.");
        }
    }

    private void Poll(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        NativeRenderer? renderer;
        lock (_gate)
        {
            renderer = _renderer;
        }

        if (renderer is null)
        {
            return;
        }

        try
        {
            RenderStats stats = renderer.GetStats();
            Pushed++;
            _stats.OnNext(stats);
        }
        catch (ObjectDisposedException)
        {
            // Detached between the read above and the call; nothing left to publish.
        }
    }
}
