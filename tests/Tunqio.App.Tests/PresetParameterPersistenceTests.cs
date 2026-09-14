using System.Globalization;
using System.Reactive.Subjects;
using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Tests;

/// <summary>
/// T-157: the parameters moved on Settings › Visualization are stored as <c>viz.params.&lt;preset&gt;.&lt;name&gt;</c> and put
/// back whenever that preset starts drawing. The host here behaves as the renderer does in the one respect that makes
/// this a feature at all: every switch returns the preset's parameters to their manifest defaults, and a value outside
/// the declared range is clamped. The relaunch through the real window, the real settings.json and the real renderer is
/// <c>tools/check-visualization-settings.ps1</c>.
/// </summary>
public sealed class PresetParameterPersistenceTests : IDisposable
{
    private const string Bars = "spectrum-bars";
    private const string Wave = "waveform";
    private const string Glow = "ambient-glow";

    private static readonly PresetParameter BarsCount =
        new("bars", "Bars", string.Empty, 8f, 128f, 64f, 1f, Hidden: false, []);

    private static readonly PresetParameter Gain =
        new("gain", "Gain", string.Empty, 0f, 4f, 1f, 0f, Hidden: false, []);

    private static readonly PresetParameter Colour =
        new("colour", "Colour source", string.Empty, 0f, 2f, 0f, 1f, Hidden: false, ["Position", "Loudness", "Spectral centroid"]);

    private static readonly PresetParameter Thickness =
        new("thickness", "Thickness", "px", 1f, 8f, 2.5f, 0f, Hidden: false, []);

    private static readonly PresetParameter GlowAmount =
        new("glow", "Glow", string.Empty, 0f, 1f, 0.5f, 0f, Hidden: false, []);

    private static readonly PresetParameter ArtPrimary =
        new("art_primary", "art_primary", string.Empty, -1f, 16777215f, -1f, 0f, Hidden: true, []);

    private readonly FakeSettings _settings = new();
    private readonly RendererLikeHost _host = new();

    public PresetParameterPersistenceTests()
    {
        _host.PresetList = [new(Bars, "Spectrum Bars"), new(Wave, "Waveform"), new(Glow, "Ambient Glow")];
        _host.Declared[Bars] = [BarsCount, Gain, Colour];
        _host.Declared[Wave] = [Thickness];
        _host.Declared[Glow] = [GlowAmount, ArtPrimary];
    }

    public void Dispose() => _host.Dispose();

    private PresetParameterMemory Memory() => new(_host, _settings);

    private VisualizationSettingsViewModel Page(PresetParameterMemory memory)
    {
        var vm = new VisualizationSettingsViewModel(_host, _settings, new ScratchPaths(), memory);
        vm.Load();
        return vm;
    }

    private void Store(string preset, string name, float value) => _settings.SetValue(SettingsKeys.VizParam(preset, name), value);

    private float? Stored(string preset, string name) =>
        _settings.Contains(SettingsKeys.VizParam(preset, name)) ? _settings.GetValue(SettingsKeys.VizParam(preset, name), float.NaN) : null;

    private IReadOnlyList<string> StoredKeys() => _settings.KeysStartingWith(SettingsKeys.VizParamsPrefix);

    // ---- AC-461: the storage shape, written as the control moves ------------------------------------------------------

    [Fact]
    public void The_key_is_viz_params_then_the_preset_then_the_parameter()
    {
        SettingsKeys.VizParam("spectrum-bars", "bars").Should().Be("viz.params.spectrum-bars.bars");
        SettingsKeys.VizParams("spectrum-bars").Should().Be("viz.params.spectrum-bars.");
    }

    [Fact]
    public void Moving_a_control_stores_its_value_as_it_changes()
    {
        using PresetParameterMemory memory = Memory();
        _host.Attach();
        VisualizationSettingsViewModel vm = Page(memory);

        vm.Parameters.Single(p => p.Name == "bars").Value = 96.4;
        Stored(Bars, "bars").Should().Be(96f, "what is stored is what the renderer was given, snapped to the step");

        vm.Parameters.Single(p => p.Name == "gain").Value = 2.0;
        Stored(Bars, "gain").Should().Be(2f);

        vm.Parameters.Single(p => p.Name == "colour").SelectedChoice = 1;
        Stored(Bars, "colour").Should().Be(1f);

        StoredKeys().Should().BeEquivalentTo(
            "viz.params.spectrum-bars.bars", "viz.params.spectrum-bars.gain", "viz.params.spectrum-bars.colour");
    }

    [Fact]
    public void Opening_the_page_stores_nothing_and_sets_nothing()
    {
        using PresetParameterMemory memory = Memory();
        _host.Attach();
        _host.Calls.Clear();

        VisualizationSettingsViewModel vm = Page(memory);

        vm.Parameters.Should().HaveCount(3);
        StoredKeys().Should().BeEmpty("a control that starts at a value has not been moved to it");
        _host.Calls.Should().BeEmpty("the renderer already holds what the controls show");
    }

    // ---- AC-462: reapplied after the preset loads at launch, and after every switch back -----------------------------

    [Fact]
    public void Stored_values_reach_the_renderer_after_the_preset_it_attaches_with_has_loaded()
    {
        Store(Bars, "bars", 96f);
        using PresetParameterMemory memory = Memory();

        _host.Attach();

        _host.Current["bars"].Should().Be(96f);
        _host.Calls.Should().Equal("preset spectrum-bars", "bars=96");
    }

    [Fact]
    public async Task The_launch_restores_the_remembered_preset_and_then_its_values_Async()
    {
        // MainWindow's order: AttachAsync starts the catalogue's first preset, then RestoreVisualizationSettingsAsync
        // switches to viz.preset. The switch returns everything to its defaults, so the values have to come after it.
        Store(Bars, "bars", 96f);
        Store(Wave, "thickness", 4f);
        using PresetParameterMemory memory = Memory();

        _host.Attach();
        await _host.SetPresetAsync(Wave);

        _host.Calls.Should().Equal("preset spectrum-bars", "bars=96", "preset waveform", "thickness=4");
        _host.Current["thickness"].Should().Be(4f);

        VisualizationSettingsViewModel vm = Page(memory);
        vm.SelectedPreset!.Id.Should().Be(Wave);
        vm.Parameters.Single().AutomationName.Should().Be("Thickness, 4 px", "a relaunch shows the same sliders");
    }

    [Fact]
    public async Task A_switch_that_did_not_come_from_the_page_gets_the_stored_values_back_Async()
    {
        using PresetParameterMemory memory = Memory();
        _host.Attach();
        VisualizationSettingsViewModel vm = Page(memory);
        vm.Parameters.Single(p => p.Name == "bars").Value = 96;

        // Straight on the host, as a shortcut or any other caller would switch.
        await _host.SetPresetAsync(Wave);
        _host.Current.Should().NotContainKey("bars");
        await _host.SetPresetAsync(Bars);

        _host.Current["bars"].Should().Be(96f, "the renderer put it back to 64 on the switch and the memory set it again");
        vm.Load();
        vm.Parameters.Single(p => p.Name == "bars").AutomationName.Should().Be("Bars, 96");
    }

    [Fact]
    public void A_switch_from_the_page_gets_the_stored_values_and_the_controls_show_them()
    {
        Store(Wave, "thickness", 4f);
        using PresetParameterMemory memory = Memory();
        _host.Attach();
        VisualizationSettingsViewModel vm = Page(memory);
        vm.Parameters.Single(p => p.Name == "bars").Value = 96;

        vm.SelectedPreset = vm.Presets.Single(p => p.Id == Wave);
        _host.Current["thickness"].Should().Be(4f);
        vm.Parameters.Single().Value.Should().Be(4);

        vm.SelectedPreset = vm.Presets.Single(p => p.Id == Bars);
        _host.Current["bars"].Should().Be(96f);
        vm.Parameters.Single(p => p.Name == "bars").Value.Should().Be(96);
    }

    // ---- AC-463: clamp, and a name the manifest no longer has ------------------------------------------------------

    [Fact]
    public void A_stored_value_outside_the_range_is_clamped_and_the_store_and_the_control_agree()
    {
        // A manifest that narrowed its range under a stored value, or a hand-edited settings.json.
        Store(Bars, "bars", 500f);
        Store(Bars, "gain", -3f);
        using PresetParameterMemory memory = Memory();

        _host.Attach();

        _host.Current["bars"].Should().Be(128f);
        _host.Current["gain"].Should().Be(0f);
        Stored(Bars, "bars").Should().Be(128f, "the stored value is rewritten to what was applied");
        Stored(Bars, "gain").Should().Be(0f);
        Page(memory).Parameters.Single(p => p.Name == "bars").Value.Should().Be(128);
    }

    [Fact]
    public async Task A_stored_name_the_manifest_no_longer_has_is_ignored_logged_once_and_does_not_stop_the_preset_Async()
    {
        Store(Bars, "segments", 12f);
        Store(Bars, "bars", 96f);
        using PresetParameterMemory memory = Memory();

        _host.Attach();

        _host.ActivePresetId.Should().Be(Bars);
        _host.Current["bars"].Should().Be(96f, "the names it does have are still applied");
        _host.Calls.Should().NotContain(c => c.StartsWith("segments", StringComparison.Ordinal));
        memory.IgnoredKeys.Should().Equal("viz.params.spectrum-bars.segments");

        await _host.SetPresetAsync(Wave);
        await _host.SetPresetAsync(Bars);
        memory.IgnoredKeys.Should().HaveCount(1, "it is reported once, not on every switch");
        Stored(Bars, "segments").Should().Be(12f, "ignored is not deleted: a later manifest may declare it again");
    }

    [Fact]
    public void A_renderer_that_is_not_attached_reapplies_nothing_and_does_not_throw()
    {
        Store(Bars, "bars", 96f);
        using PresetParameterMemory memory = Memory();

        memory.Reapply(Bars).Should().Be(0);
        _host.Calls.Should().BeEmpty();
    }

    // ---- AC-464: Reset, and hidden parameters ---------------------------------------------------------------------

    [Fact]
    public void Reset_returns_the_manifest_defaults_and_removes_that_presets_keys_and_no_others()
    {
        Store(Bars, "bars", 96f);
        Store(Bars, "gain", 2f);
        Store(Bars, "segments", 12f);
        Store(Wave, "thickness", 4f);
        // A preset whose id extends this one owns its own keys.
        _settings.SetValue("viz.params.spectrum-bars.v2.bars", 100f);
        using PresetParameterMemory memory = Memory();
        _host.Attach();
        VisualizationSettingsViewModel vm = Page(memory);

        vm.ResetParameters();

        _host.Current["bars"].Should().Be(64f);
        _host.Current["gain"].Should().Be(1f);
        vm.Parameters.Single(p => p.Name == "bars").Value.Should().Be(64);
        _settings.KeysStartingWith(SettingsKeys.VizParams(Bars)).Should().Equal("viz.params.spectrum-bars.v2.bars");
        Stored(Wave, "thickness").Should().Be(4f, "Reset is per preset");

        // And the next launch starts on the defaults.
        using var relaunched = new RendererLikeHost { PresetList = _host.PresetList };
        foreach (KeyValuePair<string, IReadOnlyList<PresetParameter>> declared in _host.Declared)
        {
            relaunched.Declared[declared.Key] = declared.Value;
        }

        using var next = new PresetParameterMemory(relaunched, _settings);
        relaunched.Attach();
        relaunched.Current["bars"].Should().Be(64f);
    }

    [Fact]
    public void Hidden_parameters_are_never_stored()
    {
        using PresetParameterMemory memory = Memory();
        _host.Attach();
        VisualizationSettingsViewModel vm = Page(memory);
        vm.SelectedPreset = vm.Presets.Single(p => p.Id == Glow);

        vm.Parameters.Single(p => p.Name == "glow").Value = 0.8;
        // What VisualizerArtLink does with the album art: straight on the host, not through the page or the memory.
        _host.SetParameter("art_primary", 123456f);
        // And a caller that tries anyway.
        memory.Remember(Glow, ArtPrimary, 654321f);

        StoredKeys().Should().Equal("viz.params.ambient-glow.glow");
    }

    [Fact]
    public async Task A_hidden_parameter_written_into_settings_by_hand_is_not_applied_Async()
    {
        Store(Glow, "art_primary", 777f);
        using PresetParameterMemory memory = Memory();
        _host.Attach();

        await _host.SetPresetAsync(Glow);

        _host.Current["art_primary"].Should().Be(-1f, "the album art decides this one");
        memory.IgnoredKeys.Should().Contain("viz.params.ambient-glow.art_primary");
    }

    private sealed class ScratchPaths : IAppPaths
    {
        public string DataRoot => @"C:\scratch\Tunqio";

        public string DatabasePath => Path.Combine(DataRoot, "library.db");

        public string SettingsPath => Path.Combine(DataRoot, "settings.json");

        public string LogsDirectory => Path.Combine(DataRoot, "logs");

        public string ArtDirectory => Path.Combine(DataRoot, "art");

        public string PresetsDirectory => Path.Combine(DataRoot, "presets");

        public string ExportsDirectory => Path.Combine(DataRoot, "exports");

        public void EnsureCreated()
        {
        }
    }

    /// <summary>
    /// A visualizer that keeps parameter values the way <c>mp_renderer</c> does: a preset starting (attach or switch)
    /// puts every parameter at its manifest default, a value is clamped to the declared range, and a name the active
    /// preset does not declare is refused. <see cref="Calls"/> records switches and sets in order.
    /// </summary>
    private sealed class RendererLikeHost : IVisualizationHost
    {
        public Dictionary<string, IReadOnlyList<PresetParameter>> Declared { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<PresetInfo> PresetList { get; set; } = [];

        public Dictionary<string, float> Current { get; } = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        public bool IsAttached { get; private set; }

        public bool HasAudioSource => false;

        public IReadOnlyList<PresetInfo> Presets => PresetList;

        public string? ActivePresetId { get; private set; }

        public IObservable<RenderStats> Stats { get; } = new Subject<RenderStats>();

        public event EventHandler<string>? PresetChanged;

        /// <summary>What the real host does on attach: the catalogue's first preset starts drawing.</summary>
        public void Attach()
        {
            IsAttached = true;
            Start(PresetList[0].Id);
        }

        public Task AttachAsync(nint swapChainPanelNative, nint audioEngineNative, RendererConfig config)
        {
            Attach();
            return Task.CompletedTask;
        }

        public void Detach()
        {
            IsAttached = false;
            ActivePresetId = null;
            Current.Clear();
        }

        public Task SetPresetAsync(string id)
        {
            if (!IsAttached)
            {
                throw new InvalidOperationException("The visualization host is not attached.");
            }

            Start(id);
            return Task.CompletedTask;
        }

        public void SetParameter(string name, float value)
        {
            if (!IsAttached || ActivePresetId is null)
            {
                throw new InvalidOperationException("The visualization host is not attached.");
            }

            PresetParameter declared = Declared[ActivePresetId].FirstOrDefault(p => p.Name == name)
                ?? throw new InvalidOperationException("MP_E_INVALID_ARG: " + name);
            float clamped = Math.Clamp(value, declared.Minimum, declared.Maximum);
            Current[name] = clamped;
            Calls.Add(name + "=" + clamped.ToString(CultureInfo.InvariantCulture));
        }

        public IReadOnlyList<PresetParameter> GetPresetParameters(string presetId) =>
            Declared.TryGetValue(presetId, out IReadOnlyList<PresetParameter>? declared)
                ? declared
                : throw new ArgumentException("No preset has the id " + presetId, nameof(presetId));

        public void Resize(int width, int height, float scaleX, float scaleY)
        {
        }

        public void SetVisible(bool visible)
        {
        }

        public void SetUserPresetRoot(string path)
        {
        }

        public IReadOnlyList<PresetInfo> RefreshPresets() => PresetList;

        public RenderStats? TryGetStats() => null;

        public void SetThemeColors(ThemeColors colors)
        {
        }

        public void SetQualityPolicy(QualityPolicy policy)
        {
        }

        public void Dispose()
        {
        }

        private void Start(string id)
        {
            ActivePresetId = id;
            Current.Clear();
            foreach (PresetParameter parameter in Declared[id])
            {
                Current[parameter.Name] = parameter.Default;
            }

            Calls.Add("preset " + id);
            PresetChanged?.Invoke(this, id);
        }
    }
}
