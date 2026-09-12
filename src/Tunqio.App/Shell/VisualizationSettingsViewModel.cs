using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.Core;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Shell;

/// <summary>One preset in the switcher.</summary>
/// <param name="Id">What <c>viz.preset</c> stores and <c>mp_renderer_set_preset</c> takes.</param>
/// <param name="Name">The preset's own display name.</param>
/// <param name="IsUser">True when it came from the user's preset directory rather than from the app.</param>
public sealed record PresetRow(string Id, string Name, bool IsUser)
{
    /// <summary>
    /// What Narrator reads for this row, and deliberately not the record's ToString. T-122 was filed because the
    /// Tracks rows had no automation name and a whole DTO - file path and ReplayGain included - was read out.
    /// </summary>
    public string AutomationName => IsUser ? $"{Name}, your preset" : Name;
}

/// <summary>
/// One control on the page, over one parameter the active preset declares (T-142). The range, the step, the
/// label and whether it is a slider or a list all come off <see cref="PresetParameter"/>, which came off the
/// preset's own manifest - so a preset this build has never seen, including one a user wrote, gets the controls
/// it asked for and no others.
/// </summary>
public sealed partial class PresetParameterRow : ObservableObject
{
    private readonly Action<string, float> _apply;

    public PresetParameterRow(PresetParameter declared, Action<string, float> apply)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(apply);
        Declared = declared;
        _apply = apply;
        Value = declared.Default;
    }

    public PresetParameter Declared { get; }

    public string Name => Declared.Name;

    public string Label => Declared.Label;

    public double Minimum => Declared.Minimum;

    public double Maximum => Declared.Maximum;

    /// <summary>What the slider steps by. A parameter that declares none is given a hundredth of its range,
    /// because a WinUI <c>Slider</c> has to be told something and 0 would be no movement at all.</summary>
    public double StepFrequency => Declared.Step > 0f ? Declared.Step : Math.Max((Declared.Maximum - Declared.Minimum) / 100.0, 0.001);

    /// <summary>Named modes for a choice parameter, in value order; empty for a quantity.</summary>
    public IReadOnlyList<string> Choices => Declared.Choices;

    public bool IsChoice => Declared.IsChoice;

    public bool IsSlider => !Declared.IsChoice;

    /// <summary>The current value, applied to the running preset as it moves.</summary>
    [ObservableProperty]
    public partial double Value { get; set; }

    /// <summary>Which choice is selected, as an index into <see cref="Choices"/>.</summary>
    public int SelectedChoice
    {
        get => (int)Math.Round(Math.Clamp(Value - Declared.Minimum, 0, Math.Max(Choices.Count - 1, 0)));
        set
        {
            if (value >= 0 && value < Choices.Count)
            {
                Value = Declared.Minimum + value;
            }
        }
    }

    /// <summary>"64", "2.5 px", "Loudness" - the value as the person reading it would say it.</summary>
    public string Display => IsChoice
        ? (SelectedChoice < Choices.Count ? Choices[SelectedChoice] : Value.ToString(CultureInfo.CurrentCulture))
        : Declared.Step >= 1f
            ? Math.Round(Value).ToString("0", CultureInfo.CurrentCulture) + Suffix
            : Value.ToString("0.##", CultureInfo.CurrentCulture) + Suffix;

    /// <summary>What Narrator says for the control: the label, the value and the unit, and nothing else.</summary>
    public string AutomationName => $"{Label}, {Display}";

    private string Suffix => Declared.Unit.Length == 0 ? string.Empty : " " + Declared.Unit;

    /// <summary>Puts the parameter back to the default its manifest declares.</summary>
    public void Reset() => Value = Declared.Default;

    partial void OnValueChanged(double value)
    {
        // Whole numbers are snapped here rather than left to the control: a Slider with a StepFrequency still
        // reports the value the pointer landed on when the range does not divide by the step.
        float applied = Declared.Step >= 1f ? (float)Math.Round(value) : (float)value;
        _apply(Declared.Name, applied);
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(SelectedChoice));
    }
}

/// <summary>
/// Settings › Visualization (E4-S9): the preset switcher, the parameters the chosen preset declares, and the
/// two audio-reactive theming settings E4-S6 shipped without any UI (T-151).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing about a preset is known here.</b> The list comes from <c>mp_renderer_enum_presets</c> and every
/// control from <c>mp_renderer_enum_preset_params</c> (T-142), so a preset a user drops into their own directory
/// gets exactly the controls its manifest declares - and Ambient Glow's three <c>art_*</c> parameters, which are
/// set by code from the album art palette and carry packed sRGB integers, get none, because the manifest says
/// they are hidden. A page that hard-coded the ranges could not have done either.
/// </para>
/// <para>
/// <b>Refresh is a button and not a watcher.</b> The core reads its preset roots when the renderer is created
/// (T-126), so a preset dropped in while the app runs needs a rescan; doing that from a <c>FileSystemWatcher</c>
/// would mean trying to compile a manifest that is still being written, and it would put a shader compile on a
/// filesystem notification thread. What is drawing keeps drawing across a rescan, whatever it finds.
/// </para>
/// <para>
/// <b>Parameter values are the preset's, not the settings file's.</b> The ABI returns every parameter to its
/// default on a preset switch, and this page follows that rather than fighting it: only the chosen preset
/// (<c>viz.preset</c>) is remembered between launches. Persisting per-preset parameters is a real feature and a
/// separate one.
/// </para>
/// </remarks>
public sealed partial class VisualizationSettingsViewModel : ObservableObject
{
    private readonly IVisualizationHost _host;
    private readonly ISettingsStore _settings;
    private readonly IAppPaths _paths;
    private bool _seeding;

    public VisualizationSettingsViewModel(IVisualizationHost host, ISettingsStore settings, IAppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        _host = host;
        _settings = settings;
        _paths = paths;
        _seeding = true;
        try
        {
            ReactiveTheming = settings.GetValue(SettingsKeys.UiReactiveTheming, SettingsKeys.Defaults.UiReactiveTheming);
            ReactiveSmoothing = settings.GetValue(SettingsKeys.UiReactiveSmoothing, SettingsKeys.Defaults.UiReactiveSmoothing);
        }
        finally
        {
            _seeding = false;
        }
    }

    /// <summary>Where a user's own presets go. Shown on the page, because "drop one in" needs a path.</summary>
    public string UserPresetDirectory => _paths.PresetsDirectory;

    /// <summary>True when a renderer is attached; false leaves the page explaining itself rather than empty.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PresetRow> Presets { get; set; } = [];

    [ObservableProperty]
    public partial PresetRow? SelectedPreset { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PresetParameterRow> Parameters { get; set; } = [];

    /// <summary>True when the chosen preset declares at least one parameter a person may move.</summary>
    [ObservableProperty]
    public partial bool HasParameters { get; set; }

    /// <summary>The last thing that happened, for the page's <c>InfoBar</c>. Empty when nothing has.</summary>
    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNotice { get; set; }

    /// <summary>True when <see cref="Notice"/> is a failure rather than a report.</summary>
    [ObservableProperty]
    public partial bool NoticeIsError { get; set; }

    /// <summary><c>ui.reactiveTheming</c> (T-151), written as it changes; the controller watches the store.</summary>
    [ObservableProperty]
    public partial bool ReactiveTheming { get; set; }

    /// <summary><c>ui.reactiveSmoothing</c> (T-151), 0 to 1.</summary>
    [ObservableProperty]
    public partial float ReactiveSmoothing { get; set; }

    /// <summary>
    /// What the smoothing setting means in seconds, which is the point of showing it at all: 0 is a half-second
    /// time constant and 1 is four seconds, and one time constant is 63% of the way to the colour the music is
    /// asking for. A bare 0..1 slider would be a number with no meaning outside the source.
    /// </summary>
    public string SmoothingDescription
    {
        get
        {
            TimeSpan tau = new ReactiveThemeOptions(true, ReactiveSmoothing).TimeConstant;
            return string.Format(
                CultureInfo.CurrentCulture,
                "{0:0.0} s to move 63% of the way to the colour the music is asking for (0.5 s at the left, 4.0 s at the right).",
                tau.TotalSeconds);
        }
    }

    /// <summary>
    /// Reads the catalogue and the chosen preset off the running renderer. Called when the page appears, so a
    /// page opened after the renderer came up is not empty and one opened before it says why.
    /// </summary>
    public void Load()
    {
        IsAvailable = _host.IsAttached;
        if (!IsAvailable)
        {
            Presets = [];
            Parameters = [];
            HasParameters = false;
            return;
        }

        LoadCatalogue(_host.Presets);
    }

    /// <summary>
    /// Rereads both preset roots. This is AC-133's "refresh": a preset dropped into the user directory while the
    /// app was running is in the list afterwards, and the preset that was drawing is still drawing.
    /// </summary>
    public void Refresh()
    {
        if (!_host.IsAttached)
        {
            IsAvailable = false;
            ShowNotice("The visualizer is not running, so there is nothing to refresh.", error: true);
            return;
        }

        int before = Presets.Count;
        IReadOnlyList<PresetInfo> found = _host.RefreshPresets();
        LoadCatalogue(found);
        int added = found.Count - before;
        ShowNotice(added switch
        {
            > 0 => $"{added} preset{(added == 1 ? string.Empty : "s")} found. {found.Count} in the list.",
            < 0 => $"{-added} preset{(added == -1 ? string.Empty : "s")} gone. {found.Count} in the list.",
            _ => $"No change. {found.Count} preset{(found.Count == 1 ? string.Empty : "s")} in the list.",
        });
    }

    /// <summary>Every parameter of the chosen preset back to the default its manifest declares.</summary>
    public void ResetParameters()
    {
        foreach (PresetParameterRow row in Parameters)
        {
            row.Reset();
        }

        ShowNotice(Parameters.Count == 0
            ? "This preset has nothing to reset."
            : $"{SelectedPreset?.Name} back to its defaults.");
    }

    /// <summary>Dismisses the <c>InfoBar</c>.</summary>
    public void ClearNotice()
    {
        HasNotice = false;
        Notice = string.Empty;
        NoticeIsError = false;
    }

    private void LoadCatalogue(IReadOnlyList<PresetInfo> presets)
    {
        string userRoot = _paths.PresetsDirectory;
        var rows = new List<PresetRow>(presets.Count);
        foreach (PresetInfo preset in presets)
        {
            rows.Add(new PresetRow(preset.Id, preset.Name, IsUserPreset(preset.Id, userRoot)));
        }

        Presets = rows;
        string? active = _host.ActivePresetId;
        _seeding = true;
        try
        {
            SelectedPreset = rows.FirstOrDefault(r => r.Id == active) ?? rows.FirstOrDefault();
        }
        finally
        {
            _seeding = false;
        }

        LoadParameters(SelectedPreset?.Id);
    }

    /// <summary>
    /// Whether a preset came out of the user's own directory. Asked of the filesystem rather than carried on the
    /// ABI: which root a preset was found in is not something the core has ever been asked, and one existence
    /// check per preset on a page the user just opened is cheaper than a field on a published struct.
    /// </summary>
    private static bool IsUserPreset(string id, string userRoot)
    {
        try
        {
            return userRoot.Length > 0 && File.Exists(Path.Combine(userRoot, id, "preset.json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private void LoadParameters(string? presetId)
    {
        if (presetId is null || !_host.IsAttached)
        {
            Parameters = [];
            HasParameters = false;
            return;
        }

        IReadOnlyList<PresetParameter> declared;
        try
        {
            declared = _host.GetPresetParameters(presetId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Serilog.Log.Warning(ex, "The parameters of preset {Preset} could not be read", presetId);
            Parameters = [];
            HasParameters = false;
            return;
        }

        // Hidden is the whole point of T-142's flag: art_primary / art_secondary / art_accent are set from the
        // album art palette and carry packed sRGB integers, so offering them would be offering three raw numbers
        // between -1 and 16777215 to type.
        var rows = declared
            .Where(p => !p.Hidden)
            .Select(p => new PresetParameterRow(p, ApplyParameter))
            .ToList();
        Parameters = rows;
        HasParameters = rows.Count > 0;
    }

    private void ApplyParameter(string name, float value)
    {
        if (!_host.IsAttached)
        {
            return;
        }

        try
        {
            _host.SetParameter(name, value);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Serilog.Log.Warning(ex, "The parameter {Parameter} could not be set", name);
        }
    }

    private void ShowNotice(string text, bool error = false)
    {
        Notice = text;
        NoticeIsError = error;
        HasNotice = true;
    }

    partial void OnSelectedPresetChanged(PresetRow? value)
    {
        if (_seeding || value is null)
        {
            return;
        }

        // Fire-and-forget by the repository's no-async-void rule, and synchronous in fact: the native call
        // compiles and swaps on the calling thread, so SetPresetAsync hands back a completed task and the body
        // below has already run by the time this returns.
        SelectPresetAsync(value).Forget("Switch preset");
    }

    private async Task SelectPresetAsync(PresetRow value)
    {
        try
        {
            await _host.SetPresetAsync(value.Id).ConfigureAwait(true);
        }
        catch (PresetCompilationException ex)
        {
            // AC-117's payoff, and the only place the compiler's words ever reach a person: the preset that was
            // drawing is still drawing, so the selection is put back to it rather than left on a preset that is
            // not on screen.
            ShowNotice(ex.CompilerMessage, error: true);
            _seeding = true;
            try
            {
                SelectedPreset = Presets.FirstOrDefault(r => r.Id == _host.ActivePresetId);
            }
            finally
            {
                _seeding = false;
            }

            LoadParameters(SelectedPreset?.Id);
            return;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            ShowNotice("That preset could not be loaded: " + ex.Message, error: true);
            return;
        }

        _settings.SetValue(SettingsKeys.VizPreset, value.Id);
        _settings.FlushAsync().Forget("Save the chosen preset");
        ClearNotice();
        LoadParameters(value.Id);
    }

    partial void OnReactiveThemingChanged(bool value)
    {
        if (_seeding)
        {
            return;
        }

        _settings.SetValue(SettingsKeys.UiReactiveTheming, value);
        _settings.FlushAsync().Forget("Save reactive theming");
    }

    partial void OnReactiveSmoothingChanged(float value)
    {
        OnPropertyChanged(nameof(SmoothingDescription));
        if (_seeding)
        {
            return;
        }

        _settings.SetValue(SettingsKeys.UiReactiveSmoothing, value);
        _settings.FlushAsync().Forget("Save reactive smoothing");
    }
}
