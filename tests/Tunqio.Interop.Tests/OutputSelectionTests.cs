using Tunqio.Core;
using Tunqio.Core.Audio;
using Tunqio.Core.Playback;

namespace Tunqio.Interop.Tests;

/// <summary>
/// E1-S6 end to end over the real mpcore.dll: what <see cref="OutputPolicy"/> resolves from the settings is what the
/// engine opens, and the mode that was granted is read back from <see cref="EngineStats.Exclusive"/> rather than assumed.
/// Everything that needs a sound device is tolerated (not asserted) when the machine has none, as a CI runner does not.
/// The fallback from a refused exclusive mode cannot be provoked on willing hardware; it is forced and asserted in the
/// native suite (native/mpcore.tests/src/test_output.cpp), which links the core with a test seam this DLL does not have.
/// </summary>
[Collection("native engine")]
public class OutputSelectionTests
{
    private static bool TryOpen(NativeEngine engine, OutputConfig config)
    {
        try
        {
            engine.SetOutput(config);
            return true;
        }
        catch (NativeException ex) when (ex.Result is MpResult.Device or MpResult.Bass)
        {
            return false; // no output device on this machine
        }
    }

    [Fact]
    public void A_remembered_device_opens_the_device_that_was_remembered()
    {
        using NativeEngine engine = NativeEngine.Create();
        IReadOnlyList<OutputDevice> devices = engine.EnumerateDevices();
        if (devices.Count == 0)
        {
            return; // no output device on this machine
        }

        OutputDevice remembered = devices.FirstOrDefault(d => d.IsDefault) ?? devices[0];
        OutputSelection selection = OutputPolicy.Resolve(new OutputPreference(remembered.Id, OutputMode.Shared, null), devices);
        selection.RequestedDeviceMissing.Should().BeFalse();
        selection.Config.DeviceIndex.Should().Be(remembered.Index);

        if (!TryOpen(engine, selection.Config))
        {
            return;
        }

        EngineStats stats = engine.GetStats();
        stats.OutputStarted.Should().BeTrue();
        stats.Exclusive.Should().BeFalse("shared mode was asked for");
        stats.OutputSampleRate.Should().Be(remembered.MixSampleRate, "shared mode runs at the device's mix format");
    }

    [Fact]
    public void A_device_that_is_gone_still_opens_something_to_play_through()
    {
        using NativeEngine engine = NativeEngine.Create();
        IReadOnlyList<OutputDevice> devices = engine.EnumerateDevices();
        if (devices.Count == 0)
        {
            return;
        }

        // The DAC in the settings was unplugged since it was chosen: the user gets the default device, not silence.
        OutputSelection selection = OutputPolicy.Resolve(new OutputPreference("{0.0.0.00000000}.{a-device-that-left}", OutputMode.Shared, null), devices);
        selection.RequestedDeviceMissing.Should().BeTrue();
        selection.Config.DeviceIndex.Should().Be(OutputConfig.DefaultDevice);

        if (!TryOpen(engine, selection.Config))
        {
            return;
        }

        engine.GetStats().OutputStarted.Should().BeTrue();
    }

    [Fact]
    public void Exclusive_mode_is_a_request_and_the_stats_say_what_was_granted()
    {
        // The mixer is created at 96 kHz on purpose: an exclusive output must follow the device, not the mixer.
        using NativeEngine engine = NativeEngine.Create(96_000, 2);
        IReadOnlyList<OutputDevice> devices = engine.EnumerateDevices();
        OutputDevice? defaultDevice = devices.FirstOrDefault(d => d.IsDefault) ?? (devices.Count > 0 ? devices[0] : null);
        if (defaultDevice is null)
        {
            return;
        }

        OutputSelection selection = OutputPolicy.Resolve(new OutputPreference(null, OutputMode.Exclusive, null), devices);
        selection.Config.Mode.Should().Be(OutputMode.Exclusive);
        selection.Config.BufferMs.Should().Be(SettingsKeys.Defaults.OutputBufferMsExclusive);

        if (!TryOpen(engine, selection.Config))
        {
            return;
        }

        // Whether exclusive was granted is the driver's call; either way there is an output, and it runs at the
        // device's own rate rather than the 96 kHz the mixer was created at.
        EngineStats stats = engine.GetStats();
        stats.OutputStarted.Should().BeTrue();
        stats.OutputSampleRate.Should().Be(defaultDevice.MixSampleRate);
    }
}
