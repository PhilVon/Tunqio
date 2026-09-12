using Tunqio.App.Controls;
using Tunqio.App.Playback;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Shell;

/// <summary>
/// Now Playing's album art, as the visualizer's colours (T-147). Watches the playback session, loads the art
/// palette E3-S7 stored beside the image, and hands it to the two things that take one: the
/// <c>ambient-glow</c> preset through <see cref="AmbientGlowPalette.Apply"/>, and - through
/// <see cref="PaletteChanged"/> - the reactive theming, which blends the track's own colour into the hue the
/// music is asking for.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the join, and it is the whole of what was missing.</b> E3-S7 extracts the palette and T-56 gives
/// the preset parameters that consume one; <see cref="AmbientGlowPalette"/> is the arithmetic between them and
/// had no caller outside its own tests, because nothing in the shell watched Now Playing on the visualizer's
/// behalf. Both halves were proved and the path between them did not exist.
/// </para>
/// <para>
/// <b>Keyed on the art hash, not on the track.</b> Ten tracks off one album share one <c>art_hash</c> and one
/// stored palette, so keying on the hash makes an album's worth of track changes cost one load - and means the
/// glow does not restate the same colours between two songs off the same record.
/// </para>
/// <para>
/// <b>Pushed at three moments, because any one of them alone leaves a gap.</b> The track changing is the
/// obvious one. The renderer attaching is the second: the panel loads after the window does, and until it has
/// there is no preset to tell. The preset changing is the third, and it is the one a person notices - Ambient
/// Glow chosen in Settings in the middle of a song would otherwise draw its declared defaults until the next
/// track started.
/// </para>
/// <para>
/// <b>Nothing here is allowed to be an error.</b> A missing palette file, a cache cleared under a playing
/// track, a renderer detached mid-push: each costs the colours and nothing else, because a visualizer that
/// throws while the music plays is a worse outcome than one drawing the preset's own palette.
/// </para>
/// </remarks>
public sealed class VisualizerArtLink : IDisposable
{
    private readonly IPlaybackSessionSource _source;
    private readonly IVisualizationHost _host;
    private readonly IArtCache? _art;
    private readonly SynchronizationContext? _ui;
    private IDisposable? _subscription;
    private CancellationTokenSource? _pending;
    private bool _haveHash;
    private bool _disposed;

    /// <param name="source">Where the session comes from; it may not exist yet, and may never.</param>
    /// <param name="host">The visualizer surface. Held whether or not it is attached; a detached one is skipped.</param>
    /// <param name="art">The art cache. Null - the spike modes - simply leaves every track without a palette.</param>
    /// <param name="ui">The XAML thread's context. Null runs updates inline, which is what the tests want.</param>
    public VisualizerArtLink(
        IPlaybackSessionSource source,
        IVisualizationHost host,
        IArtCache? art = null,
        SynchronizationContext? ui = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(host);
        _source = source;
        _host = host;
        _art = art;
        _ui = ui;
        _host.PresetChanged += OnPresetChanged;
        if (source.Session is { } ready)
        {
            Attach(ready);
        }
        else
        {
            source.SessionReady += OnSessionReady;
        }
    }

    /// <summary>
    /// The palette of the art now playing, or null for a track without any. Raised after every load, including
    /// the load that finds nothing, so a consumer is told to forget the last track's colours as well as to take
    /// the new ones.
    /// </summary>
    public event EventHandler<ArtPalette?>? PaletteChanged;

    /// <summary>The palette of the art now playing; null for a track with none, and before the first track.</summary>
    public ArtPalette? Palette { get; private set; }

    /// <summary>The <c>art_hash</c> the palette came from, for the diagnostics readout.</summary>
    public string? Hash { get; private set; }

    /// <summary>How many palettes have been loaded - one per album, not one per track.</summary>
    public long Loads { get; private set; }

    /// <summary>How many times the colours have actually reached the preset. Zero is the bug this task fixes.</summary>
    public long Pushes { get; private set; }

    /// <summary>
    /// Pushes the current palette again. What the shell calls once the renderer has attached and its remembered
    /// preset is running, because until then there was no preset to tell.
    /// </summary>
    public void Reapply() => Push();

    /// <summary>
    /// Takes an <c>art_hash</c> directly. The session is what normally drives this; the tests and the spike
    /// modes call it, because a palette needs a hash and nothing else that opening an audio device would bring.
    /// </summary>
    public void Show(string? hash)
    {
        if (_disposed || (_haveHash && string.Equals(hash, Hash, StringComparison.Ordinal)))
        {
            return;
        }

        _haveHash = true;
        Hash = hash;

        // The previous load is abandoned rather than awaited: skipping through five tracks must not queue five
        // palettes behind each other and finish on whichever the disk returned last.
        _pending?.Cancel();
        _pending?.Dispose();
        var cts = new CancellationTokenSource();
        _pending = cts;
        LoadAsync(hash, cts.Token).Forget("Load the album art palette for the visualizer");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.SessionReady -= OnSessionReady;
        _host.PresetChanged -= OnPresetChanged;
        _subscription?.Dispose();
        _subscription = null;
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }

    private async Task LoadAsync(string? hash, CancellationToken ct)
    {
        ArtPalette? palette = null;
        if (_art is not null && !string.IsNullOrEmpty(hash))
        {
            try
            {
                palette = await _art.LoadPaletteAsync(hash, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The cache can be cleared under a playing track (Settings > Library > Regenerate art).
                Serilog.Log.Debug(e, "The art palette for {Hash} could not be read", hash);
            }
        }

        if (ct.IsCancellationRequested || _disposed)
        {
            return;
        }

        Loads++;
        Palette = palette;
        Push();
        PaletteChanged?.Invoke(this, palette);
    }

    /// <summary>
    /// The three parameters, when the preset that declares them is the one drawing. Detached is the same case as
    /// "some other preset": <see cref="IVisualizationHost.ActivePresetId"/> is null, and nothing is said.
    /// </summary>
    private void Push()
    {
        try
        {
            if (_host.ActivePresetId != AmbientGlowPalette.PresetId)
            {
                return;
            }

            AmbientGlowPalette.Apply(_host, Palette);
            Pushes++;
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // Detached, or the window closed, between the check above and the call. NativeException is an
            // InvalidOperationException, so a core that refuses the parameter lands here too.
            Serilog.Log.Debug(e, "The album art palette could not be given to the visualizer");
        }
    }

    private void OnPresetChanged(object? sender, string id)
    {
        if (string.Equals(id, AmbientGlowPalette.PresetId, StringComparison.Ordinal))
        {
            Push();
        }
    }

    private void OnSessionReady(object? sender, PlaybackSession session)
    {
        _source.SessionReady -= OnSessionReady;
        Post(() => Attach(session));
    }

    private void Attach(PlaybackSession session)
    {
        if (_disposed)
        {
            return;
        }

        _subscription = session.Snapshots.Subscribe(s => Post(() => Show(s.Track?.ArtHash)));
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
            return;
        }

        // No JoinableTaskFactory in this app; the context is the XAML thread's DispatcherQueue one and Post never blocks the caller.
#pragma warning disable VSTHRD001
        _ui.Post(_ => action(), null);
#pragma warning restore VSTHRD001
    }
}
