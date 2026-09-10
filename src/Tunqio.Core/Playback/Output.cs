using Tunqio.Core.Audio;

namespace Tunqio.Core.Playback;

/// <summary>
/// What the <c>output.*</c> settings say the user wants (E1-S6). <paramref name="DeviceId"/> is the stable WASAPI
/// endpoint id of <see cref="OutputDevice.Id"/>; null or empty means the system default device. <paramref name="BufferMs"/>
/// is null when the user has never set one, so the default for the mode applies.
/// </summary>
public readonly record struct OutputPreference(string? DeviceId, OutputMode Mode, int? BufferMs)
{
    /// <summary>The documented defaults: the system default device, shared mode, the mode's default buffer.</summary>
    public static OutputPreference Default => new(null, OutputMode.Shared, null);
}

/// <summary>
/// The device the preference resolved to. <see cref="Config"/> is what <see cref="IAudioEngine.InitializeAsync"/> takes;
/// <see cref="Device"/> is the enumerated device it names (null for the system default, which the engine resolves itself);
/// <see cref="RequestedDeviceMissing"/> is true when a device was named in the settings and is no longer present, which is
/// what the shell tells the user about before falling back to the default.
/// </summary>
public sealed record OutputSelection(OutputConfig Config, OutputDevice? Device, bool RequestedDeviceMissing);

/// <summary>
/// Turns the stored <c>output.deviceId</c>, <c>output.mode</c> and <c>output.bufferMs</c> into the
/// <see cref="OutputConfig"/> the engine opens (E1-S6). Pure, so the rules are testable without a device: a device is
/// remembered by its endpoint id rather than its index, because indices shift as devices come and go; a remembered
/// device that is gone falls back to the system default rather than failing to start; and a buffer the user never set
/// takes the default for the mode (shared is generous, exclusive is short, docs/solution-structure.md "Settings keys").
/// <para>
/// Exclusive mode is only ever a request. The core opens the device in shared mode and raises
/// <see cref="EngineEventType.Error"/> when a driver refuses exclusive, so the mode that was granted is read back from
/// <see cref="EngineStats.Exclusive"/>, never assumed from this selection.
/// </para>
/// </summary>
public static class OutputPolicy
{
    /// <summary>Parses <c>output.mode</c>; anything unrecognised is the documented default (shared).</summary>
    public static OutputMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "exclusive" => OutputMode.Exclusive,
        "shared" => OutputMode.Shared,
        _ => ParseMode(SettingsKeys.Defaults.OutputMode),
    };

    /// <summary>The value <c>output.mode</c> stores for <paramref name="mode"/>.</summary>
    public static string ModeName(OutputMode mode) => mode == OutputMode.Exclusive ? "exclusive" : "shared";

    /// <summary>The buffer to ask for when the user has not chosen one: shared 40 ms, exclusive 10 ms.</summary>
    public static int DefaultBufferMs(OutputMode mode) =>
        mode == OutputMode.Exclusive ? SettingsKeys.Defaults.OutputBufferMsExclusive : SettingsKeys.Defaults.OutputBufferMsShared;

    /// <summary>Reads the three <c>output.*</c> settings as a preference. A stored buffer of 0 or less means "the default".</summary>
    public static OutputPreference Read(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? deviceId = settings.GetValue<string?>(SettingsKeys.OutputDeviceId, null);
        OutputMode mode = ParseMode(settings.GetValue<string?>(SettingsKeys.OutputMode, null));
        int bufferMs = settings.GetValue(SettingsKeys.OutputBufferMs, 0);
        return new OutputPreference(string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim(), mode, bufferMs > 0 ? bufferMs : null);
    }

    /// <summary>Writes a preference back to the settings store (the caller flushes).</summary>
    public static void Write(ISettingsStore settings, OutputPreference preference)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.SetValue<string?>(SettingsKeys.OutputDeviceId, preference.DeviceId);
        settings.SetValue(SettingsKeys.OutputMode, ModeName(preference.Mode));
        settings.SetValue(SettingsKeys.OutputBufferMs, preference.BufferMs ?? 0);
    }

    /// <summary>
    /// The output to open for <paramref name="preference"/> given the devices the engine currently reports. The named
    /// device is matched by <see cref="OutputDevice.Id"/>, the WASAPI endpoint id, which is stable across reboots and
    /// across the index shifting as other devices come and go.
    /// </summary>
    public static OutputSelection Resolve(OutputPreference preference, IReadOnlyList<OutputDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        OutputDevice? device = Find(preference.DeviceId, devices);
        bool missing = preference.DeviceId is not null && device is null;
        int index = device?.Index ?? OutputConfig.DefaultDevice;
        int bufferMs = preference.BufferMs ?? DefaultBufferMs(preference.Mode);
        return new OutputSelection(new OutputConfig(index, preference.Mode, bufferMs), device, missing);
    }

    /// <summary>Convenience for the host: read the settings and resolve them against <paramref name="devices"/> in one step.</summary>
    public static OutputSelection Resolve(ISettingsStore settings, IReadOnlyList<OutputDevice> devices) =>
        Resolve(Read(settings), devices);

    private static OutputDevice? Find(string? deviceId, IReadOnlyList<OutputDevice> devices)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        foreach (OutputDevice device in devices)
        {
            if (string.Equals(device.Id, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return device;
            }
        }

        return null;
    }
}
