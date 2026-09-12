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

    /// <summary>
    /// Why the statistics poll stopped, when it has. Null while it is running. The poll gives up on the first
    /// failure that is not a detach, because it runs on a timer and a failure it cannot fix would otherwise
    /// repeat twice a second for the life of the process.
    /// </summary>
    public Exception? StatsFailure { get; private set; }

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
        catch (Exception ex)
        {
            // A System.Threading.Timer callback runs on a pool thread, so an exception that leaves it does not
            // fail a call - it takes the process down. Found by E4-S7: mp_render_stats grew a tail, and an
            // Interop that asks for it against an mpcore.dll one minor older is refused on every poll, which
            // is the ABI working exactly as the header says (a struct_size the callee has never heard of is
            // MP_E_INVALID_ARG) and is a version pairing the loader deliberately permits, since it refuses
            // only on a MAJOR mismatch. Twice a second, for ever, one of them fatal, was the previous
            // behaviour of that pairing.
            //
            // So the poll stops rather than repeating a failure nothing will fix, and the reason is kept for
            // whoever asks. The diagnostics overlay does not lose the renderer with it: MainWindow reads
            // mp_renderer_get_stats on its own path and shows the failure as the Renderer row's text.
            StatsFailure = ex;
            _ = _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }
}
