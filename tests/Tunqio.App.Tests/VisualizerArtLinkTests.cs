using System.Reactive.Linq;
using FluentAssertions;
using Tunqio.App.Shell;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;
using Tunqio.Core.Tests.Playback;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Tests;

/// <summary>
/// T-147: the join between Now Playing and the visualizer's colours. <c>AmbientGlowPaletteTests</c> proves the
/// arithmetic and <c>ArtCacheTests</c> proves the palette is stored; what is here is the thing that had no
/// caller - that a track change actually reaches <see cref="IVisualizationHost.SetParameter"/>, and that the
/// three moments it has to reach it at are all covered.
/// </summary>
#pragma warning disable CA1001 // xunit's IAsyncLifetime owns the teardown; DisposeAsync below is what runs it.
public sealed class VisualizerArtLinkTests : IAsyncLifetime
{
    private const string Glow = AmbientGlowPalette.PresetId;

    private readonly FakeAudioEngine _engine = new();
    private readonly FakeTrackRepository _tracks = new();
    private readonly FakeSettings _settings = new();
    private readonly FakePlayHistory _history = new();
    private readonly FakeQueueStore _queues = new();
    private readonly StubSessionSource _source = new();
    private readonly PaletteCache _art = new();
    private readonly RecordingVisualizer _host = new();
    private PlaybackSession _session = null!;
    private VisualizerArtLink _link = null!;

    public Task InitializeAsync()
    {
        // Two tracks off one album (one art_hash) and one off another, which is what makes "keyed on the art
        // and not on the track" a statement rather than a claim.
        _tracks.Rows.AddRange([
            Rows.Track(11, "Wide Awake", albumTitle: "City Lights", artHash: "aaaa1111"),
            Rows.Track(12, "Second Light", albumTitle: "City Lights", artHash: "aaaa1111"),
            Rows.Track(13, "Red Sleeve", albumTitle: "Ember", artHash: "bbbb2222"),
            Rows.Track(14, "Untagged", albumId: null, albumTitle: null, artHash: null),
        ]);
        _art.Palettes["aaaa1111"] = Palette(Colour(40, 90, 160, 0.6), Colour(20, 30, 40, 0.3), Colour(210, 220, 240, 0.1));
        _art.Palettes["bbbb2222"] = Palette(Colour(190, 40, 30, 0.7), Colour(60, 10, 10, 0.2), Colour(240, 200, 190, 0.1));

        _session = new PlaybackSession(_engine, _tracks, _history, _queues, _settings, autoPoll: false);
        _source.Session = _session;
        _link = new VisualizerArtLink(_source, _host, _art);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _link.Dispose();
        await _session.DisposeAsync();
        await _engine.DisposeAsync();
    }

    private static PaletteColor Colour(byte r, byte g, byte b, double population) =>
        new(r, g, b, population, PaletteColor.RelativeLuminance(r, g, b));

    private static ArtPalette Palette(params PaletteColor[] colours) => new(colours);

    private async Task PlayAsync(params long[] ids)
    {
        await _session.PlayNowAsync(ids);
        await _session.PollAsync();
    }

    private IReadOnlyList<float> Values() => [.. _host.Parameters.Select(p => p.Value)];

    /// <summary>
    /// The panel loading, with the push that causes forgotten. Attaching is itself one of the three moments -
    /// the real host raises <c>PresetChanged</c> for the preset it starts on - so every case below would
    /// otherwise open with three parameters already recorded.
    /// </summary>
    private void Attach(string preset)
    {
        _host.Attach(preset);
        _host.Parameters.Clear();
        _pushesAtAttach = _link.Pushes;
    }

    private long Pushed => _link.Pushes - _pushesAtAttach;

    private long _pushesAtAttach;

    // ---- AC-292: the album's colours reach the preset, and change with the album ---------------------------------

    [Fact]
    public async Task The_album_art_colours_reach_the_preset_when_the_track_starts_Async()
    {
        Attach(Glow);
        await PlayAsync(11);

        // The three parameters ambient-glow declares, in the order it declares them, packed as it takes them.
        _host.Parameters.Select(p => p.Name).Should().Equal(AmbientGlowPalette.ParameterNames);
        Values().Should().Equal(
            AmbientGlowPalette.Pack(Colour(40, 90, 160, 0.6)),
            AmbientGlowPalette.Pack(Colour(20, 30, 40, 0.3)),
            AmbientGlowPalette.Pack(Colour(210, 220, 240, 0.1)));
        Pushed.Should().Be(1, "this is the call that had no caller before T-147");
    }

    [Fact]
    public async Task A_new_album_repaints_the_preset_and_a_second_track_off_the_same_one_does_not_Async()
    {
        Attach(Glow);
        await PlayAsync(11);
        _host.Parameters.Clear();

        // Same art_hash: nothing new to say, and no second decode.
        await PlayAsync(12);
        _host.Parameters.Should().BeEmpty("two tracks off one album share one palette");
        _art.Loads.Should().Be(1);

        await PlayAsync(13);
        Values().Should().Equal(
            AmbientGlowPalette.Pack(Colour(190, 40, 30, 0.7)),
            AmbientGlowPalette.Pack(Colour(60, 10, 10, 0.2)),
            AmbientGlowPalette.Pack(Colour(240, 200, 190, 0.1)));
        _art.Loads.Should().Be(2);
    }

    // ---- AC-293: no art is its own colour, not the last track's -------------------------------------------------

    [Fact]
    public async Task A_track_with_no_art_puts_the_preset_back_on_its_own_defaults_Async()
    {
        Attach(Glow);
        await PlayAsync(11);
        _host.Parameters.Clear();

        await PlayAsync(14);
        Values().Should().Equal(AmbientGlowPalette.NoArt, AmbientGlowPalette.NoArt, AmbientGlowPalette.NoArt);
        _link.Palette.Should().BeNull();
    }

    [Fact]
    public async Task Art_the_cache_no_longer_holds_is_no_art_rather_than_the_last_track_s_Async()
    {
        Attach(Glow);
        await PlayAsync(11);
        _host.Parameters.Clear();

        // Settings > Library > Regenerate art, under a playing track.
        _art.Palettes.Clear();
        await PlayAsync(13);
        Values().Should().Equal(AmbientGlowPalette.NoArt, AmbientGlowPalette.NoArt, AmbientGlowPalette.NoArt);
    }

    // ---- AC-294: the preset arriving late, and the renderer arriving late ---------------------------------------

    [Fact]
    public async Task Choosing_Ambient_Glow_mid_track_colours_it_at_once_Async()
    {
        // The case a person actually meets: some other preset is drawing, a track is playing, and Ambient Glow
        // is picked in Settings. Without this the glow would draw its declared defaults until the next track.
        Attach("spectrum-bars");
        await PlayAsync(13);
        _host.Parameters.Should().BeEmpty("the core refuses a parameter the active preset does not declare");

        await _host.SetPresetAsync(Glow);
        Values().Should().Equal(
            AmbientGlowPalette.Pack(Colour(190, 40, 30, 0.7)),
            AmbientGlowPalette.Pack(Colour(60, 10, 10, 0.2)),
            AmbientGlowPalette.Pack(Colour(240, 200, 190, 0.1)));
    }

    [Fact]
    public async Task A_renderer_that_attaches_after_the_music_started_is_told_on_Reapply_Async()
    {
        // The shipped order: the window shows, audio starts, and the SwapChainPanel loads after both.
        await PlayAsync(11);
        _host.Parameters.Should().BeEmpty("there was no preset to tell");
        _link.Pushes.Should().Be(0);

        Attach(Glow);
        _link.Reapply();
        Values().Should().Equal(
            AmbientGlowPalette.Pack(Colour(40, 90, 160, 0.6)),
            AmbientGlowPalette.Pack(Colour(20, 30, 40, 0.3)),
            AmbientGlowPalette.Pack(Colour(210, 220, 240, 0.1)));
    }

    // ---- AC-295: the reactive theming gets the same palette -----------------------------------------------------

    [Fact]
    public async Task Every_load_is_announced_including_the_one_that_finds_nothing_Async()
    {
        List<ArtPalette?> seen = [];
        _link.PaletteChanged += (_, p) => seen.Add(p);

        await PlayAsync(11);
        await PlayAsync(14);

        // The null matters as much as the palette: it is what tells the theming to stop tinting the hue with
        // the last album's colour.
        seen.Should().HaveCount(2);
        seen[0]!.Dominant.Should().Be(Colour(40, 90, 160, 0.6));
        seen[1].Should().BeNull();
    }

    // ---- nothing here is allowed to be an error ------------------------------------------------------------------

    [Fact]
    public async Task A_detached_renderer_costs_the_colours_and_nothing_else_Async()
    {
        // ActivePresetId is null while detached, which is the same case as "some other preset is drawing".
        await FluentActions.Awaiting(() => PlayAsync(11)).Should().NotThrowAsync();
        _link.Pushes.Should().Be(0);
        _link.Palette.Should().NotBeNull("the palette is loaded anyway, so it is there when the renderer arrives");
    }

    [Fact]
    public async Task A_renderer_that_refuses_the_parameter_does_not_take_the_track_change_down_Async()
    {
        Attach(Glow);
        _host.Refuses = new InvalidOperationException("the visualization host is not attached");
        await FluentActions.Awaiting(() => PlayAsync(11)).Should().NotThrowAsync();
        Pushed.Should().Be(0);
    }

    [Fact]
    public async Task A_cache_that_throws_is_a_track_with_no_art_Async()
    {
        Attach(Glow);
        _art.Throws = new IOException("the art directory has gone");
        await FluentActions.Awaiting(() => PlayAsync(11)).Should().NotThrowAsync();
        Values().Should().Equal(AmbientGlowPalette.NoArt, AmbientGlowPalette.NoArt, AmbientGlowPalette.NoArt);
    }

    [Fact]
    public async Task Nothing_is_said_after_Dispose_Async()
    {
        Attach(Glow);
        await PlayAsync(11);
        _host.Parameters.Clear();
        _link.Dispose();

        await PlayAsync(13);
        _host.Parameters.Should().BeEmpty();
    }

    /// <summary>An <see cref="IArtCache"/> that holds the palettes a test names, and counts what was asked for.</summary>
    private sealed class PaletteCache : IArtCache
    {
        public Dictionary<string, ArtPalette> Palettes { get; } = new(StringComparer.Ordinal);

        public int Loads { get; private set; }

        public Exception? Throws { get; set; }

        public Task<ArtHashes> StoreAsync(EmbeddedPicture? picture, string audioPath, CancellationToken ct = default) =>
            Task.FromResult(ArtHashes.None);

        public string? PathFor(string? hash, ArtSize size) => null;

        public Task<ArtPalette?> LoadPaletteAsync(string? hash, CancellationToken ct = default)
        {
            Loads++;
            if (Throws is { } problem)
            {
                throw problem;
            }

            return Task.FromResult(hash is not null && Palettes.TryGetValue(hash, out ArtPalette? p) ? p : null);
        }

        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A host that records parameters and raises <see cref="PresetChanged"/> the way the real one does.</summary>
    private sealed class RecordingVisualizer : IVisualizationHost
    {
        public List<(string Name, float Value)> Parameters { get; } = [];

        public Exception? Refuses { get; set; }

        public bool IsAttached { get; private set; }

        public bool HasAudioSource { get; private set; }

        public IReadOnlyList<PresetInfo> Presets => [];

        public string? ActivePresetId { get; private set; }

        public IObservable<RenderStats> Stats => Observable.Empty<RenderStats>();

        public event EventHandler<string>? PresetChanged;

        /// <summary>What the panel loading does: a device, a catalogue and its first preset drawing.</summary>
        public void Attach(string preset)
        {
            IsAttached = true;
            ActivePresetId = preset;
            PresetChanged?.Invoke(this, preset);
        }

        public Task AttachAsync(nint swapChainPanelNative, nint audioEngineNative, RendererConfig config)
        {
            Attach("spectrum-bars");
            return Task.CompletedTask;
        }

        public void Detach()
        {
            IsAttached = false;
            ActivePresetId = null;
        }

        public void Dispose()
        {
        }

        public IReadOnlyList<PresetParameter> GetPresetParameters(string presetId) => [];

        public IReadOnlyList<PresetInfo> RefreshPresets() => [];

        public void Resize(int width, int height, float scaleX, float scaleY)
        {
        }

        public void SetParameter(string name, float value)
        {
            if (Refuses is { } problem)
            {
                throw problem;
            }

            Parameters.Add((name, value));
        }

        public Task SetPresetAsync(string id)
        {
            ActivePresetId = id;
            PresetChanged?.Invoke(this, id);
            return Task.CompletedTask;
        }

        public void SetQualityPolicy(QualityPolicy policy) => throw new NotSupportedException("E4-S7");

        public void SetThemeColors(ThemeColors colors)
        {
        }

        public void SetUserPresetRoot(string path)
        {
        }

        public void SetVisible(bool visible)
        {
        }

        public RenderStats? TryGetStats() => null;
    }
}
