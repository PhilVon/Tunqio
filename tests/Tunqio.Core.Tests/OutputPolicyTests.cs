using Tunqio.Core.Audio;
using Tunqio.Core.Playback;

namespace Tunqio.Core.Tests;

/// <summary>
/// E1-S6: the stored <c>output.*</c> settings become the <see cref="OutputConfig"/> the engine opens. The rules that
/// matter are all about a device that is not where it was: remembered by endpoint id rather than index, and replaced by
/// the system default rather than a failure when it is gone.
/// </summary>
public class OutputPolicyTests
{
    private static readonly OutputDevice Realtek = new(0, "Speakers (Realtek)", "{0.0.0.00000000}.{realtek}", 48000, 2, TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(10), IsDefault: true);
    private static readonly OutputDevice Dac = new(1, "Topping E30", "{0.0.0.00000000}.{topping}", 44100, 2, TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(10), IsDefault: false);
    private static readonly OutputDevice[] Devices = [Realtek, Dac];

    [Theory]
    [InlineData("shared", OutputMode.Shared)]
    [InlineData("exclusive", OutputMode.Exclusive)]
    [InlineData(" Exclusive ", OutputMode.Exclusive)]
    [InlineData(null, OutputMode.Shared)]
    [InlineData("bit-perfect", OutputMode.Shared)]
    public void Mode_parses_the_setting_and_defaults_to_shared(string? value, OutputMode expected)
    {
        OutputPolicy.ParseMode(value).Should().Be(expected);
        SettingsKeys.Defaults.OutputMode.Should().Be("shared");
    }

    [Fact]
    public void Mode_round_trips_through_the_stored_name()
    {
        OutputPolicy.ParseMode(OutputPolicy.ModeName(OutputMode.Exclusive)).Should().Be(OutputMode.Exclusive);
        OutputPolicy.ParseMode(OutputPolicy.ModeName(OutputMode.Shared)).Should().Be(OutputMode.Shared);
    }

    [Fact]
    public void An_unset_buffer_takes_the_default_for_the_mode()
    {
        OutputPolicy.Resolve(new OutputPreference(null, OutputMode.Shared, null), Devices)
            .Config.BufferMs.Should().Be(SettingsKeys.Defaults.OutputBufferMsShared);
        OutputPolicy.Resolve(new OutputPreference(null, OutputMode.Exclusive, null), Devices)
            .Config.BufferMs.Should().Be(SettingsKeys.Defaults.OutputBufferMsExclusive);
    }

    [Fact]
    public void A_buffer_the_user_chose_is_kept_for_either_mode()
    {
        OutputPolicy.Resolve(new OutputPreference(null, OutputMode.Exclusive, 25), Devices).Config.BufferMs.Should().Be(25);
    }

    [Fact]
    public void No_stored_device_is_the_system_default()
    {
        OutputSelection selection = OutputPolicy.Resolve(OutputPreference.Default, Devices);
        selection.Config.DeviceIndex.Should().Be(OutputConfig.DefaultDevice);
        selection.Device.Should().BeNull();
        selection.RequestedDeviceMissing.Should().BeFalse();
    }

    [Fact]
    public void A_stored_device_resolves_to_its_current_index()
    {
        OutputSelection selection = OutputPolicy.Resolve(new OutputPreference(Dac.Id, OutputMode.Exclusive, null), Devices);
        selection.Config.DeviceIndex.Should().Be(Dac.Index);
        selection.Config.Mode.Should().Be(OutputMode.Exclusive);
        selection.Device.Should().Be(Dac);
        selection.RequestedDeviceMissing.Should().BeFalse();
    }

    [Fact]
    public void The_index_follows_the_device_when_the_list_changes_underneath_it()
    {
        // The DAC was index 1; another device appearing before it makes it index 2. The endpoint id is what was stored,
        // so the user still gets their DAC rather than whatever now sits at index 1.
        var bluetooth = new OutputDevice(1, "WH-1000XM4", "{0.0.0.00000000}.{sony}", 48000, 2, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), IsDefault: false);
        OutputDevice movedDac = Dac with { Index = 2 };
        OutputSelection selection = OutputPolicy.Resolve(new OutputPreference(Dac.Id, OutputMode.Shared, null), [Realtek, bluetooth, movedDac]);
        selection.Config.DeviceIndex.Should().Be(2);
        selection.Device.Should().Be(movedDac);
    }

    [Fact]
    public void A_device_that_is_gone_falls_back_to_the_default_and_says_so()
    {
        OutputSelection selection = OutputPolicy.Resolve(new OutputPreference(Dac.Id, OutputMode.Exclusive, null), [Realtek]);
        selection.Config.DeviceIndex.Should().Be(OutputConfig.DefaultDevice);
        selection.Device.Should().BeNull();
        selection.RequestedDeviceMissing.Should().BeTrue();
        // The mode the user asked for survives the device falling back: it is the device that went, not the preference.
        selection.Config.Mode.Should().Be(OutputMode.Exclusive);
    }

    [Fact]
    public void No_device_at_all_is_still_a_usable_config()
    {
        OutputSelection selection = OutputPolicy.Resolve(new OutputPreference(Dac.Id, OutputMode.Shared, null), []);
        selection.Config.DeviceIndex.Should().Be(OutputConfig.DefaultDevice);
        selection.RequestedDeviceMissing.Should().BeTrue();
    }

    [Fact]
    public void Settings_round_trip_through_the_store()
    {
        var settings = new InMemorySettings();
        var preference = new OutputPreference(Dac.Id, OutputMode.Exclusive, 12);
        OutputPolicy.Write(settings, preference);
        OutputPolicy.Read(settings).Should().Be(preference);

        settings.GetValue<string?>(SettingsKeys.OutputDeviceId, null).Should().Be(Dac.Id);
        settings.GetValue<string?>(SettingsKeys.OutputMode, null).Should().Be("exclusive");
        settings.GetValue(SettingsKeys.OutputBufferMs, 0).Should().Be(12);
    }

    [Fact]
    public void An_empty_store_reads_as_the_documented_defaults()
    {
        OutputPolicy.Read(new InMemorySettings()).Should().Be(OutputPreference.Default);
    }

    [Fact]
    public void A_blank_device_id_is_the_default_device_not_a_missing_one()
    {
        var settings = new InMemorySettings();
        settings.SetValue(SettingsKeys.OutputDeviceId, "   ");
        OutputPolicy.Read(settings).DeviceId.Should().BeNull();
        OutputPolicy.Resolve(settings, Devices).RequestedDeviceMissing.Should().BeFalse();
    }

    private sealed class InMemorySettings : ISettingsStore
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public event EventHandler<string>? Changed;

        public T GetValue<T>(string key, T defaultValue) => _values.TryGetValue(key, out object? value) && value is T typed ? typed : defaultValue;

        public void SetValue<T>(string key, T value)
        {
            _values[key] = value;
            Changed?.Invoke(this, key);
        }

        public bool Contains(string key) => _values.ContainsKey(key);

        public void Flush()
        {
        }

        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
