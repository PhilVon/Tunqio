using System.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Library;

/// <summary>
/// Hover preview's rules (E5-S5, docs/ui-screens-and-flows.md): the pointer resting on an album tile for 500 ms
/// previews the album's first track; leaving the tile fades it out; only in Discovery; only once the user has turned
/// <c>ui.hoverPreview</c> on (AC-142, R-15). The grids report the pointer; this decides what that means.
/// </summary>
/// <remarks>
/// <para>
/// <b>The single-preview rule falls out of the dwell.</b> The engine refuses a second preview while the first is still
/// audible, fade-out included, and the fade is 200 ms. Moving from one tile to the next stops the first on leaving
/// and cannot start the second for 500 ms, so the second always arrives after the first has gone, which is what
/// "moving between tiles queues the next after the current fade-out" asks. Nothing here waits for a fade.
/// </para>
/// <para>
/// A pointer on a tile when the timer fires is checked again after the album lookup, which is asynchronous: a
/// pointer that left during it must not start a preview for a tile it is no longer on.
/// </para>
/// </remarks>
public sealed class HoverPreviewController : IDisposable
{
    /// <summary>How long the pointer has to rest on a tile before the preview starts.</summary>
    public static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(500);

    private readonly Func<IPreviewPlayer?> _player;
    private readonly ShellState _shell;
    private readonly ISettingsStore _settings;
    private readonly IAlbumRepository _albums;
    private readonly TimeProvider _clock;
    private readonly SynchronizationContext? _ui;
    private AlbumDto? _hovered;
    private AlbumDto? _playing;
    private ITimer? _timer;
    private int _generation;
    private bool _disposed;

    /// <param name="player">The session once audio is up; null before that, and then nothing previews.</param>
    /// <param name="shell">The mode: previews are Discovery's alone.</param>
    /// <param name="settings">Read for <c>ui.hoverPreview</c> at every hover, and watched so turning it off stops a preview.</param>
    /// <param name="albums">Turns a tile into the track it previews.</param>
    /// <param name="clock">Drives the dwell; a manual clock is how a test advances it.</param>
    /// <param name="ui">The XAML thread's context; null runs the timer's work inline, which is what the tests want.</param>
    public HoverPreviewController(
        Func<IPreviewPlayer?> player,
        ShellState shell,
        ISettingsStore settings,
        IAlbumRepository albums,
        TimeProvider clock,
        SynchronizationContext? ui)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(albums);
        ArgumentNullException.ThrowIfNull(clock);
        _player = player;
        _shell = shell;
        _settings = settings;
        _albums = albums;
        _clock = clock;
        _ui = ui;
        _shell.PropertyChanged += OnShellChanged;
        _settings.Changed += OnSettingChanged;
    }

    /// <summary>Whether a hover would preview right now: the setting is on and the shell is in Discovery.</summary>
    public bool Enabled =>
        !_disposed
        && _settings.GetValue(SettingsKeys.UiHoverPreview, SettingsKeys.Defaults.UiHoverPreview)
        && _shell.Mode == ShellMode.Discovery;

    /// <summary>The album being previewed, or null.</summary>
    public AlbumDto? Previewing => _playing;

    /// <summary>
    /// Raised once, ever, the first time the pointer rests on a tile in Discovery with previews off (Q-74, R-15's
    /// first-use prompt): the host shows the offer to turn them on. <c>ui.hoverPreviewOffered</c> is set before it is
    /// raised, so neither a second hover nor a second launch raises it again.
    /// </summary>
    public event EventHandler? OfferRequested;

    /// <summary>The pointer came to rest on <paramref name="album"/>'s tile.</summary>
    public void Enter(AlbumDto album)
    {
        ArgumentNullException.ThrowIfNull(album);
        CancelTimer();
        _hovered = album;
        int generation = _generation;
        if (!Enabled)
        {
            // Off: the same dwell, but what it earns is the one-time offer rather than a preview.
            if (ShouldOffer())
            {
                _timer = _clock.CreateTimer(
                    _ => Post(() => Offer(generation, album)), null, Dwell, Timeout.InfiniteTimeSpan);
            }

            return;
        }

        _timer = _clock.CreateTimer(
            _ => Post(() => StartAsync(generation, album).Forget("Hover preview")),
            null,
            Dwell,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>The pointer left <paramref name="album"/>'s tile.</summary>
    public void Leave(AlbumDto album)
    {
        ArgumentNullException.ThrowIfNull(album);
        if (_hovered?.Id != album.Id)
        {
            return;
        }

        LeaveAll();
    }

    /// <summary>Forgets the hover and stops any preview: a page going away, whatever its pointer last reported.</summary>
    public void LeaveAll()
    {
        _hovered = null;
        CancelTimer();
        StopPreview();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        LeaveAll();
        _disposed = true;
        _shell.PropertyChanged -= OnShellChanged;
        _settings.Changed -= OnSettingChanged;
    }

    private async Task StartAsync(int generation, AlbumDto album)
    {
        if (!StillWanted(generation, album) || _player() is not { } player)
        {
            return;
        }

        AlbumDetailDto? detail = await _albums.GetDetailAsync(album.Id).ConfigureAwait(true);
        TrackDto? first = detail?.Tracks.FirstOrDefault(t => !t.Missing);
        if (first is null || !StillWanted(generation, album))
        {
            return;
        }

        _playing = album;
        await player.PreviewAsync(first.Id).ConfigureAwait(true);
    }

    private bool ShouldOffer() =>
        !_disposed
        && _shell.Mode == ShellMode.Discovery
        && !_settings.GetValue(SettingsKeys.UiHoverPreview, SettingsKeys.Defaults.UiHoverPreview)
        && !_settings.GetValue(SettingsKeys.UiHoverPreviewOffered, SettingsKeys.Defaults.UiHoverPreviewOffered);

    private void Offer(int generation, AlbumDto album)
    {
        if (generation != _generation || _hovered?.Id != album.Id || !ShouldOffer())
        {
            return;
        }

        _settings.SetValue(SettingsKeys.UiHoverPreviewOffered, true);
        OfferRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool StillWanted(int generation, AlbumDto album) =>
        generation == _generation && _hovered?.Id == album.Id && Enabled;

    private void StopPreview()
    {
        if (_playing is null)
        {
            return;
        }

        _playing = null;
        _player()?.StopPreviewAsync().Forget("Stop hover preview");
    }

    private void CancelTimer()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellState.Mode) && !Enabled)
        {
            LeaveAll();
        }
    }

    private void OnSettingChanged(object? sender, string key)
    {
        if (key == SettingsKeys.UiHoverPreview && !Enabled)
        {
            LeaveAll();
        }
    }

    private void Post(Action action)
    {
        if (_ui is null)
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
