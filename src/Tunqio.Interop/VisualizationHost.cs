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

    /// <inheritdoc />
    public bool HasAudioSource { get; private set; }

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

    /// <inheritdoc />
    public event EventHandler<string>? PresetChanged;

    /// <summary>Statistics pushed to <see cref="Stats"/> since construction. Diagnostics and tests.</summary>
    public long Pushed { get; private set; }

    /// <summary>
    /// Why the statistics poll stopped, when it has. Null while it is running. The poll gives up on the first
    /// failure that is not a detach, because it runs on a timer and a failure it cannot fix would otherwise
    /// repeat twice a second for the life of the process.
    /// </summary>
    public Exception? StatsFailure { get; private set; }

    public Task AttachAsync(nint swapChainPanelNative, nint audioEngineNative, RendererConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        HasAudioSource = audioEngineNative != nint.Zero;
        // Created outside the lock: it builds a device, a swap chain and a thread, and it can throw.
        NativeRenderer renderer = config.Headless
            ? NativeRenderer.CreateHeadless(config, audioEngineNative)
            : NativeRenderer.Create(swapChainPanelNative, audioEngineNative, config);
        NativeRenderer? previous;
        string? started;
        try
        {
            IReadOnlyList<PresetInfo> presets = renderer.EnumeratePresets();
            // Asked, not assumed (T-127): the core starts on its built-in preset, and it says so.
            string active = renderer.GetActivePreset().Id;
            lock (_gate)
            {
                previous = _renderer;
                _renderer = renderer;
                _presets = presets;
                _activePresetId = active;
                started = active;
            }
        }
        catch
        {
            renderer.Dispose();
            throw;
        }

        previous?.Dispose();
        // Outside the lock, and after the old renderer has gone: a handler is free to call back in.
        if (started is not null)
        {
            PresetChanged?.Invoke(this, started);
        }

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
            HasAudioSource = false;
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
    /// roll back here, and <see cref="ActivePresetId"/> only moves after the call has come back - and it moves to
    /// what the core says is drawing (T-127), which after a successful switch is <paramref name="id"/>.
    /// </remarks>
    public Task SetPresetAsync(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        NativeRenderer renderer = Require();
        renderer.SetPreset(id);
        string active = renderer.GetActivePreset().Id;
        lock (_gate)
        {
            _activePresetId = active;
        }

        PresetChanged?.Invoke(this, active);
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
        string active = renderer.GetActivePreset().Id;
        lock (_gate)
        {
            _presets = presets;
            _activePresetId = active;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="ActivePresetId"/> is read back from the core after the rescan (T-127), and the core keeps the
    /// preset that is drawing even when the rescan no longer lists it: it is compiled and still on screen, and
    /// reporting it as gone would make the shell think it had lost a picture it can see.
    /// </remarks>
    public IReadOnlyList<PresetInfo> RefreshPresets()
    {
        NativeRenderer renderer = Require();
        renderer.RescanPresets();
        IReadOnlyList<PresetInfo> presets = renderer.EnumeratePresets();
        string active = renderer.GetActivePreset().Id;
        lock (_gate)
        {
            _presets = presets;
            _activePresetId = active;
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

    public void SetTemporalSmoothing(TemporalSmoothing smoothing) =>
        Require().SetTemporalSmoothing(smoothing.AttackMs, smoothing.DecayMs);

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

    /// <summary>
    /// The renderer this host is currently driving, or null while detached. For the tests that have to assert
    /// something about the renderer the host BUILT rather than one they built themselves - which is exactly the
    /// gap T-179 fell through: every renderer test constructed its own with an engine, so nothing noticed that
    /// the host's own construction was passing none.
    /// </summary>
    internal NativeRenderer? AttachedRenderer
    {
        get
        {
            lock (_gate)
            {
                return _renderer;
            }
        }
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
