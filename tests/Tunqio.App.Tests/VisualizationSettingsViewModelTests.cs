using System.Reactive.Subjects;
using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Tests;

/// <summary>
/// E4-S9 and T-151: Settings › Visualization over a fake visualizer, so every claim about what the page offers
/// is asserted without a GPU. What cannot be asserted here - whether the page is reachable, whether the controls
/// reach the automation tree, whether a preset dropped in on disk appears - is
/// <c>tools/check-visualization-settings.ps1</c>, which reads the running window's UIA tree (T-116's lesson:
/// a settings page's on-screen criteria need a harness, not a screenshot of a WinUI window, which captures
/// black).
/// </summary>
public sealed class VisualizationSettingsViewModelTests
{
    private static readonly PresetParameter Bars =
        new("bars", "Bars", string.Empty, 8f, 128f, 64f, 1f, Hidden: false, []);

    private static readonly PresetParameter Smoothing =
        new("smoothing", "Smoothing", string.Empty, 0f, 1f, 0.35f, 0f, Hidden: false, []);

    private static readonly PresetParameter Colour =
        new("colour", "Colour source", string.Empty, 0f, 2f, 0f, 1f, Hidden: false, ["Position", "Loudness", "Spectral centroid"]);

    private static readonly PresetParameter Thickness =
        new("thickness", "Thickness", "px", 1f, 8f, 2.5f, 0f, Hidden: false, []);

    private static readonly PresetParameter ArtPrimary =
        new("art_primary", "art_primary", string.Empty, -1f, 16777215f, -1f, 0f, Hidden: true, []);

    private static VisualizationSettingsViewModel Build(FakeVisualizer host, FakeSettings? settings = null) =>
        new(host, settings ?? new FakeSettings(), new FakePaths());

    private static FakeVisualizer Attached() => new()
    {
        IsAttached = true,
        PresetList =
        [
            new PresetInfo("spectrum-bars", "Spectrum Bars"),
            new PresetInfo("waveform", "Waveform"),
            new PresetInfo("ambient-glow", "Ambient Glow"),
        ],
        ActivePresetId = "spectrum-bars",
        Declared =
        {
            ["spectrum-bars"] = [Bars, Smoothing, Colour],
            ["waveform"] = [Thickness, Colour],
            ["ambient-glow"] = [Smoothing, ArtPrimary],
        },
    };

    [Fact]
    public void A_detached_visualizer_leaves_the_page_saying_so_rather_than_empty()
    {
        var host = new FakeVisualizer { IsAttached = false };
        VisualizationSettingsViewModel vm = Build(host);
        vm.Load();

        vm.IsAvailable.Should().BeFalse();
        vm.Presets.Should().BeEmpty();
        vm.Parameters.Should().BeEmpty();

        // And Refresh says why instead of throwing at the person who pressed it.
        vm.Refresh();
        vm.HasNotice.Should().BeTrue();
        vm.NoticeIsError.Should().BeTrue();
    }

    [Fact]
    public void The_list_is_the_catalogue_and_the_selection_is_what_is_drawing()
    {
        FakeVisualizer host = Attached();
        VisualizationSettingsViewModel vm = Build(host);
        vm.Load();

        vm.IsAvailable.Should().BeTrue();
        vm.Presets.Select(p => p.Id).Should().Equal("spectrum-bars", "waveform", "ambient-glow");
        vm.SelectedPreset!.Id.Should().Be("spectrum-bars");
        // Loading must not switch anything: the renderer is already on this preset.
        host.Switched.Should().BeEmpty();
    }

    [Fact]
    public void A_preset_row_reads_as_its_name_and_not_as_a_dto()
    {
        // T-122: the Tracks rows had no automation name and Narrator read the whole DTO out, file path included.
        var shipped = new PresetRow("spectrum-bars", "Spectrum Bars", IsUser: false);
        shipped.AutomationName.Should().Be("Spectrum Bars");
        shipped.AutomationName.Should().NotContain("spectrum-bars");

        // A preset the user wrote says so, because "which of these did I put here" is the question they will ask.
        new PresetRow("mine", "My Preset", IsUser: true).AutomationName.Should().Be("My Preset, your preset");
    }

    [Fact]
    public void Choosing_a_preset_switches_the_renderer_and_is_remembered()
    {
        FakeVisualizer host = Attached();
        var settings = new FakeSettings();
        VisualizationSettingsViewModel vm = Build(host, settings);
        vm.Load();

        vm.SelectedPreset = vm.Presets.Single(p => p.Id == "waveform");

        host.Switched.Should().Equal("waveform");
        settings.GetValue(SettingsKeys.VizPreset, "unset").Should().Be("waveform");
        // And the controls followed the preset rather than staying on the last one's.
        vm.Parameters.Select(p => p.Name).Should().Equal("thickness", "colour");
    }

    [Fact]
    public void A_preset_that_does_not_compile_shows_the_compilers_words_and_puts_the_selection_back()
    {
        FakeVisualizer host = Attached();
        host.FailsToCompile.Add("waveform");
        VisualizationSettingsViewModel vm = Build(host);
        vm.Load();

        vm.SelectedPreset = vm.Presets.Single(p => p.Id == "waveform");

        vm.HasNotice.Should().BeTrue();
        vm.NoticeIsError.Should().BeTrue();
        // Not "MP_E_D3D": the person who can fix this is the person who wrote the shader, and the file and line
        // are the only thing that tells them where.
        vm.Notice.Should().Contain("waveform.hlsl(22,12-52)");
        vm.Notice.Should().Contain("error X3004");
        // The preset that was drawing is still drawing, so the selection is put back to it.
        vm.SelectedPreset!.Id.Should().Be("spectrum-bars");
        vm.Parameters.Select(p => p.Name).Should().Equal("bars", "smoothing", "colour");
    }

    [Fact]
    public void Only_the_parameters_a_person_may_move_get_a_control()
    {
        FakeVisualizer host = Attached();
        host.ActivePresetId = "ambient-glow";
        VisualizationSettingsViewModel vm = Build(host);
        vm.Load();

        // T-142's whole point. art_primary carries a packed sRGB colour set from the album art palette; a page
        // that listed every declared parameter would offer a number between -1 and 16777215 to type.
        vm.Parameters.Select(p => p.Name).Should().Equal("smoothing");
        vm.HasParameters.Should().BeTrue();
    }

    [Fact]
    public void A_control_takes_its_range_step_and_kind_from_the_preset_rather_than_from_this_page()
    {
        FakeVisualizer host = Attached();
        VisualizationSettingsViewModel vm = Build(host);
        vm.Load();

        PresetParameterRow bars = vm.Parameters.Single(p => p.Name == "bars");
        bars.Label.Should().Be("Bars");
        bars.Minimum.Should().Be(8);
        bars.Maximum.Should().Be(128);
        bars.StepFrequency.Should().Be(1);
        bars.IsSlider.Should().BeTrue();
        bars.Value.Should().Be(64);
        bars.Display.Should().Be("64");

        PresetParameterRow colour = vm.Parameters.Single(p => p.Name == "colour");
        colour.IsChoice.Should().BeTrue();
        colour.Choices.Should().Equal("Position", "Loudness", "Spectral centroid");
        colour.Display.Should().Be("Position");

        // A parameter that declares no step is continuous, and a Slider still needs a number: a hundredth of
        // the range, not zero, which would be a control that cannot move.
        vm.Parameters.Single(p => p.Name == "smoothing").StepFrequency.Should().BeApproximately(0.01, 1e-9);
    }

    [Fact]
    public void Moving_a_control_reaches_the_renderer_and_says_what_it_reads()
    {
        FakeVisualizer host = Attached();
        VisualizationSettingsViewModel vm = Build(host);
        vm.Load();

        PresetParameterRow bars = vm.Parameters.Single(p => p.Name == "bars");
        bars.Value = 96.4; // a pointer landing between two steps
        host.Applied.Should().Contain(("bars", 96f), "a whole-numbered parameter is snapped, not sent as 96.4");
        bars.Display.Should().Be("96");
        bars.AutomationName.Should().Be("Bars, 96");

        PresetParameterRow colour = vm.Parameters.Single(p => p.Name == "colour");
        colour.SelectedChoice = 2;
        host.Applied.Should().Contain(("colour", 2f));
        colour.Display.Should().Be("Spectral centroid");
        colour.AutomationName.Should().Be("Colour source, Spectral centroid");
    }

    [Fact]
    public void A_unit_is_shown_beside_the_number_it_measures()
    {
        FakeVisualizer host = Attached();
        host.ActivePresetId = "waveform";
        VisualizationSettingsViewModel vm = Build(host);
        vm.Load();

        PresetParameterRow thickness = vm.Parameters.Single(p => p.Name == "thickness");
        thickness.Display.Should().Be("2.5 px");
        thickness.AutomationName.Should().Be("Thickness, 2.5 px");
    }

    [Fact]
    public void Reset_puts_every_parameter_back_to_the_default_its_manifest_declares()
    {
        FakeVisualizer host = Attached();
        VisualizationSettingsViewModel vm = Build(host);
        vm.Load();
        vm.Parameters.Single(p => p.Name == "bars").Value = 12;
        vm.Parameters.Single(p => p.Name == "smoothing").Value = 0.9;
        host.Applied.Clear();

        vm.ResetParameters();

        vm.Parameters.Single(p => p.Name == "bars").Value.Should().Be(64);
        vm.Parameters.Single(p => p.Name == "smoothing").Value.Should().BeApproximately(0.35, 1e-6);
        host.Applied.Should().Contain(("bars", 64f));
        vm.HasNotice.Should().BeTrue();
    }

    [Fact]
    public void Refresh_rereads_the_catalogue_and_reports_what_it_found()
    {
        FakeVisualizer host = Attached();
        VisualizationSettingsViewModel vm = Build(host);
        vm.Load();
        vm.Presets.Should().HaveCount(3);

        // AC-133: dropped in while the app was running.
        host.PresetList = [.. host.PresetList, new PresetInfo("mine", "My Preset")];
        host.Declared["mine"] = [Bars];

        vm.Refresh();

        host.Refreshes.Should().Be(1);
        vm.Presets.Select(p => p.Id).Should().Contain("mine");
        vm.Notice.Should().Contain("1 preset found");
        vm.NoticeIsError.Should().BeFalse();
        // The preset that was drawing is still selected: a refresh is not a switch.
        vm.SelectedPreset!.Id.Should().Be("spectrum-bars");
        host.Switched.Should().BeEmpty();
    }

    [Fact]
    public void Refresh_that_finds_nothing_new_says_so_rather_than_nothing()
    {
        FakeVisualizer host = Attached();
        VisualizationSettingsViewModel vm = Build(host);
        vm.Load();

        vm.Refresh();

        vm.HasNotice.Should().BeTrue();
        vm.Notice.Should().Contain("No change");
        vm.Notice.Should().Contain("3 presets");
    }

    [Fact]
    public void The_two_reactive_theming_settings_are_read_and_written()
    {
        var settings = new FakeSettings();
        settings.SetValue(SettingsKeys.UiReactiveTheming, false);
        settings.SetValue(SettingsKeys.UiReactiveSmoothing, 0.75f);

        VisualizationSettingsViewModel vm = Build(Attached(), settings);
        vm.ReactiveTheming.Should().BeFalse();
        vm.ReactiveSmoothing.Should().Be(0.75f);

        vm.ReactiveTheming = true;
        vm.ReactiveSmoothing = 0.25f;

        settings.GetValue(SettingsKeys.UiReactiveTheming, false).Should().BeTrue();
        settings.GetValue(SettingsKeys.UiReactiveSmoothing, 0f).Should().Be(0.25f);
    }

    [Fact]
    public void Seeding_the_two_settings_from_the_store_does_not_write_them_back()
    {
        var settings = new FakeSettings();
        Build(Attached(), settings);
        settings.Contains(SettingsKeys.UiReactiveTheming).Should().BeFalse();
        settings.Contains(SettingsKeys.UiReactiveSmoothing).Should().BeFalse();
    }

    /// <summary>
    /// T-151: the smoothing slider says what the person is choosing in seconds. The numbers here are
    /// <see cref="ReactiveThemeOptions.TimeConstant"/>'s, so the label cannot drift from the engine.
    /// </summary>
    [Theory]
    [InlineData(0f, "0.5 s")]
    [InlineData(0.15f, "1.0 s")]
    [InlineData(0.5f, "2.3 s")]
    [InlineData(1f, "4.0 s")]
    public void The_smoothing_label_states_the_time_constant_it_maps_to(float smoothing, string expected)
    {
        VisualizationSettingsViewModel vm = Build(Attached());
        vm.ReactiveSmoothing = smoothing;

        vm.SmoothingDescription.Should().StartWith(expected);
        // And what a time constant is, or the number is a second opaque thing beside the first.
        vm.SmoothingDescription.Should().Contain("63%");
        vm.SmoothingDescription.Should().Contain("0.5 s at the left, 4.0 s at the right");

        // The claim itself, against the engine that will do the smoothing.
        new ReactiveThemeOptions(true, smoothing).TimeConstant.TotalSeconds
            .Should().BeApproximately(double.Parse(expected[..^2], System.Globalization.CultureInfo.InvariantCulture), 0.05);
    }

    private sealed class FakePaths : IAppPaths
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

    private sealed class FakeVisualizer : IVisualizationHost
    {
        public bool IsAttached { get; set; }

        public IReadOnlyList<PresetInfo> PresetList { get; set; } = [];

        public Dictionary<string, IReadOnlyList<PresetParameter>> Declared { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FailsToCompile { get; } = new(StringComparer.Ordinal);

        public List<string> Switched { get; } = [];

        public List<(string Name, float Value)> Applied { get; } = [];

        public List<string> UserRoots { get; } = [];

        public int Refreshes { get; private set; }

        public IReadOnlyList<PresetInfo> Presets => PresetList;

        public string? ActivePresetId { get; set; }

        public IObservable<RenderStats> Stats { get; } = new Subject<RenderStats>();

        public event EventHandler<string>? PresetChanged;

        public Task AttachAsync(nint swapChainPanelNative, RendererConfig config)
        {
            IsAttached = true;
            return Task.CompletedTask;
        }

        public void Detach() => IsAttached = false;

        public void Resize(int width, int height, float scaleX, float scaleY)
        {
        }

        public void SetVisible(bool visible)
        {
        }

        public Task SetPresetAsync(string id)
        {
            if (FailsToCompile.Contains(id))
            {
                // The real diagnostic, as E4-S3's broken-shader fixture produces it.
                throw new PresetCompilationException(
                    id,
                    "preset 'waveform': waveform.hlsl(22,12-52): error X3004: undeclared identifier 'colour_that_was_never_declared'");
            }

            Switched.Add(id);
            ActivePresetId = id;
            PresetChanged?.Invoke(this, id);
            return Task.CompletedTask;
        }

        public void SetParameter(string name, float value) => Applied.Add((name, value));

        public IReadOnlyList<PresetParameter> GetPresetParameters(string presetId) =>
            Declared.TryGetValue(presetId, out IReadOnlyList<PresetParameter>? p) ? p : [];

        public void SetUserPresetRoot(string path) => UserRoots.Add(path);

        public IReadOnlyList<PresetInfo> RefreshPresets()
        {
            Refreshes++;
            return PresetList;
        }

        public RenderStats? TryGetStats() => null;

        public void SetThemeColors(ThemeColors colors)
        {
        }

        public void SetQualityPolicy(QualityPolicy policy) => throw new NotSupportedException("E4-S7");

        public void Dispose()
        {
        }
    }
}
