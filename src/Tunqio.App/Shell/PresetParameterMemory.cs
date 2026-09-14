using Tunqio.App.Controls;
using Tunqio.Core;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Shell;

/// <summary>
/// The preset parameters a person has moved, kept in <c>viz.params.&lt;preset&gt;.&lt;name&gt;</c> and put back on the
/// renderer whenever that preset starts drawing (T-157).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reapplied on <see cref="IVisualizationHost.PresetChanged"/>, and nowhere else.</b> The renderer's own rule is that
/// <c>mp_renderer_set_param</c> values go back to their manifest defaults on a preset switch, so a stored value has to be
/// set again after every switch, not only after the one the settings page makes. The host raises that event for the
/// preset <see cref="IVisualizationHost.AttachAsync"/> starts on, for the remembered preset the window restores, and for
/// every <see cref="IVisualizationHost.SetPresetAsync"/> from any caller. Listening there covers every route at once,
/// which listening on the page would not. The one condition is that this object exists before the window attaches the
/// renderer, which is why App resolves it before it builds the window.
/// </para>
/// <para>
/// <b>The stored value and the picture agree.</b> A value outside the range the manifest now declares is clamped here,
/// the same clamp <c>mp_renderer_set_param</c> applies, and the clamped value is written back. A slider built from
/// <see cref="ValueFor"/> therefore shows what the renderer is drawing with.
/// </para>
/// <para>
/// <b>A stored name the manifest no longer declares is ignored,</b> and logged once for each key per process, so a
/// preset that dropped a parameter still loads with everything else it had. Reset (<see cref="Forget"/>) removes such a
/// key along with the rest.
/// </para>
/// <para>
/// <b>Hidden parameters are never stored.</b> Ambient Glow's <c>art_primary</c>/<c>art_secondary</c>/<c>art_accent</c>
/// are set from the album art by <see cref="VisualizerArtLink"/>, straight on the host. Storing them would pin one
/// album's colours on every later launch.
/// </para>
/// </remarks>
public sealed class PresetParameterMemory : IDisposable
{
    private readonly IVisualizationHost _host;
    private readonly ISettingsStore _settings;
    private readonly HashSet<string> _reportedUnknown = new(StringComparer.Ordinal);
    private bool _disposed;

    public PresetParameterMemory(IVisualizationHost host, ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(settings);
        _host = host;
        _settings = settings;
        _host.PresetChanged += OnPresetChanged;
    }

    /// <summary>How many stored values have reached the renderer since construction. Diagnostics and tests.</summary>
    public long Reapplied { get; private set; }

    /// <summary>The stored keys that named nothing the preset declares, each counted once. Diagnostics and tests.</summary>
    public IReadOnlyCollection<string> IgnoredKeys => _reportedUnknown;

    /// <summary><paramref name="value"/> inside the range <paramref name="declared"/> gives; the default when it is not a number.</summary>
    public static float Clamp(PresetParameter declared, float value)
    {
        ArgumentNullException.ThrowIfNull(declared);
        if (!float.IsFinite(value))
        {
            return declared.Default;
        }

        float min = declared.Minimum;
        float max = Math.Max(declared.Minimum, declared.Maximum);
        return Math.Clamp(value, min, max);
    }

    /// <summary>
    /// What a control for <paramref name="declared"/> should start at: the stored value, clamped, or the manifest's
    /// default when nothing is stored or the parameter is hidden.
    /// </summary>
    public float ValueFor(string presetId, PresetParameter declared)
    {
        ArgumentException.ThrowIfNullOrEmpty(presetId);
        ArgumentNullException.ThrowIfNull(declared);
        string key = SettingsKeys.VizParam(presetId, declared.Name);
        if (declared.Hidden || !_settings.Contains(key))
        {
            return declared.Default;
        }

        return Clamp(declared, _settings.GetValue(key, declared.Default));
    }

    /// <summary>
    /// Stores <paramref name="value"/>, clamped, as the value of <paramref name="declared"/> on
    /// <paramref name="presetId"/>, and returns what was stored. A hidden parameter is not stored.
    /// </summary>
    public float Remember(string presetId, PresetParameter declared, float value)
    {
        ArgumentException.ThrowIfNullOrEmpty(presetId);
        ArgumentNullException.ThrowIfNull(declared);
        float clamped = Clamp(declared, value);
        if (declared.Hidden)
        {
            return clamped;
        }

        _settings.SetValue(SettingsKeys.VizParam(presetId, declared.Name), clamped);
        _settings.FlushAsync().Forget("Save a preset parameter");
        return clamped;
    }

    /// <summary>Removes every stored parameter of <paramref name="presetId"/>, including names its manifest no longer has.</summary>
    public void Forget(string presetId)
    {
        ArgumentException.ThrowIfNullOrEmpty(presetId);
        bool removed = false;
        foreach (string key in StoredKeys(presetId))
        {
            _settings.SetValue<float?>(key, null);
            removed = true;
        }

        if (removed)
        {
            _settings.FlushAsync().Forget("Forget a preset's parameters");
        }
    }

    /// <summary>
    /// Sets every stored parameter of <paramref name="presetId"/> on the renderer, clamped. Called on
    /// <see cref="IVisualizationHost.PresetChanged"/>; public for a caller that knows the renderer lost its values some
    /// other way. Returns how many values were set.
    /// </summary>
    public int Reapply(string presetId)
    {
        if (_disposed || string.IsNullOrEmpty(presetId) || !_host.IsAttached)
        {
            return 0;
        }

        IReadOnlyList<string> keys = StoredKeys(presetId);
        if (keys.Count == 0)
        {
            return 0;
        }

        IReadOnlyList<PresetParameter> declared;
        try
        {
            declared = _host.GetPresetParameters(presetId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ObjectDisposedException)
        {
            Serilog.Log.Warning(ex, "The parameters of preset {Preset} could not be read, so its stored values were not reapplied", presetId);
            return 0;
        }

        var byName = new Dictionary<string, PresetParameter>(StringComparer.Ordinal);
        foreach (PresetParameter parameter in declared)
        {
            byName.TryAdd(parameter.Name, parameter);
        }

        string prefix = SettingsKeys.VizParams(presetId);
        int applied = 0;
        bool rewritten = false;
        foreach (string key in keys)
        {
            string name = key[prefix.Length..];
            if (!byName.TryGetValue(name, out PresetParameter? parameter) || parameter.Hidden)
            {
                if (_reportedUnknown.Add(key))
                {
                    Serilog.Log.Warning(
                        "Stored parameter {Key} is not a parameter preset {Preset} offers, so it is ignored", key, presetId);
                }

                continue;
            }

            float stored = _settings.GetValue(key, float.NaN);
            float value = Clamp(parameter, stored);
            if (!value.Equals(stored))
            {
                Serilog.Log.Information(
                    "Stored parameter {Key} = {Stored} is outside {Min}..{Max}; clamped to {Value}",
                    key, stored, parameter.Minimum, parameter.Maximum, value);
                _settings.SetValue(key, value);
                rewritten = true;
            }

            try
            {
                _host.SetParameter(name, value);
                applied++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ObjectDisposedException)
            {
                // Detached between the check above and the call, or a core that refuses the name. Costs this value only.
                Serilog.Log.Debug(ex, "Stored parameter {Key} could not be given to the visualizer", key);
            }
        }

        if (rewritten)
        {
            _settings.FlushAsync().Forget("Save clamped preset parameters");
        }

        Reapplied += applied;
        Serilog.Log.Debug("Reapplied {Count} stored parameter(s) to preset {Preset}", applied, presetId);
        return applied;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _host.PresetChanged -= OnPresetChanged;
    }

    /// <summary>
    /// The stored keys of one preset. A remainder containing a dot is left out: it belongs to a preset whose id extends
    /// this one (<c>glow</c> and <c>glow.v2</c>), not to a parameter of this one.
    /// </summary>
    private List<string> StoredKeys(string presetId)
    {
        string prefix = SettingsKeys.VizParams(presetId);
        var keys = new List<string>();
        foreach (string key in _settings.KeysStartingWith(prefix))
        {
            if (key.Length > prefix.Length && key.IndexOf('.', prefix.Length) < 0)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    private void OnPresetChanged(object? sender, string id) => Reapply(id);
}
