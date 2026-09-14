using Tunqio.Core;

namespace Tunqio.Library.Tests;

/// <summary>E0-S6: a setting written before exit is read back on the next launch; corrupt files do not block start-up.</summary>
public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunqio-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppPaths Paths() => new(_root);

    [Fact]
    public async Task Values_survive_a_relaunch_Async()
    {
        var paths = Paths();
        await using (var first = new JsonSettingsStore(paths))
        {
            first.SetValue(SettingsKeys.UiTheme, "dark");
            first.SetValue(SettingsKeys.OutputBufferMs, 20);
            first.SetValue(SettingsKeys.UiReactiveSmoothing, 0.25f);
            first.SetValue(SettingsKeys.AppLastLaunchUtc, new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));
            await first.FlushAsync();
        }

        var second = new JsonSettingsStore(paths); // "next launch"
        second.GetValue(SettingsKeys.UiTheme, "system").Should().Be("dark");
        second.GetValue(SettingsKeys.OutputBufferMs, 40).Should().Be(20);
        second.GetValue(SettingsKeys.UiReactiveSmoothing, 0f).Should().Be(0.25f);
        second.GetValue(SettingsKeys.AppLastLaunchUtc, DateTimeOffset.MinValue).Year.Should().Be(2026);
        second.GetValue(SettingsKeys.PlaybackGapless, true).Should().BeTrue("absent keys return the default");
    }

    [Fact]
    public async Task Preset_parameters_are_stored_as_flat_viz_params_keys_and_listed_by_prefix_Async()
    {
        // T-157: the storage shape as it lands in settings.json, and the prefix listing Reset uses to find a preset's keys.
        var paths = Paths();
        await using (var first = new JsonSettingsStore(paths))
        {
            first.SetValue(SettingsKeys.VizParam("spectrum-bars", "bars"), 96f);
            first.SetValue(SettingsKeys.VizParam("spectrum-bars", "gain"), 2.5f);
            first.SetValue(SettingsKeys.VizParam("waveform", "thickness"), 4f);
            first.SetValue(SettingsKeys.VizPreset, "spectrum-bars");
            await first.FlushAsync();
        }

        File.ReadAllText(paths.SettingsPath).Should().Contain("\"viz.params.spectrum-bars.bars\": 96");

        var second = new JsonSettingsStore(paths);
        second.KeysStartingWith(SettingsKeys.VizParams("spectrum-bars")).Should().BeEquivalentTo(
            "viz.params.spectrum-bars.bars", "viz.params.spectrum-bars.gain");
        second.GetValue(SettingsKeys.VizParam("spectrum-bars", "gain"), 0f).Should().Be(2.5f);

        second.SetValue<float?>(SettingsKeys.VizParam("spectrum-bars", "bars"), null);
        second.KeysStartingWith(SettingsKeys.VizParamsPrefix).Should().BeEquivalentTo(
            "viz.params.spectrum-bars.gain", "viz.params.waveform.thickness");
    }

    [Fact]
    public void Synchronous_flush_and_dispose_persist_pending_changes()
    {
        var paths = Paths();
        using (var store = new JsonSettingsStore(paths))
        {
            store.SetValue(SettingsKeys.VizPreset, "radial");
        }

        File.Exists(paths.SettingsPath).Should().BeTrue();
        new JsonSettingsStore(paths).GetValue(SettingsKeys.VizPreset, "").Should().Be("radial");
        File.Exists(paths.SettingsPath + ".tmp").Should().BeFalse("the temp file is replaced atomically");
    }

    [Fact]
    public async Task Setting_null_removes_the_key_and_raises_Changed_Async()
    {
        var store = new JsonSettingsStore(Paths());
        var changed = new List<string>();
        store.Changed += (_, key) => changed.Add(key);

        store.SetValue(SettingsKeys.OutputDeviceId, "usb-1");
        store.Contains(SettingsKeys.OutputDeviceId).Should().BeTrue();
        store.SetValue<string?>(SettingsKeys.OutputDeviceId, null);
        store.Contains(SettingsKeys.OutputDeviceId).Should().BeFalse();
        changed.Should().Equal(SettingsKeys.OutputDeviceId, SettingsKeys.OutputDeviceId);
        await store.FlushAsync();
    }

    [Fact]
    public void Wrong_type_falls_back_to_the_default()
    {
        var store = new JsonSettingsStore(Paths());
        store.SetValue(SettingsKeys.OutputBufferMs, "not a number");
        store.GetValue(SettingsKeys.OutputBufferMs, 40).Should().Be(40);
    }

    [Fact]
    public void A_corrupt_file_is_moved_aside_and_the_store_starts_empty()
    {
        var paths = Paths();
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsPath, "{ this is not json");

        var store = new JsonSettingsStore(paths);
        store.Count.Should().Be(0);
        Directory.GetFiles(paths.DataRoot, "settings.json.corrupt-*").Should().HaveCount(1);
    }

    [Fact]
    public void Flush_without_changes_writes_nothing()
    {
        var paths = Paths();
        var store = new JsonSettingsStore(paths);
        store.Flush();
        File.Exists(paths.SettingsPath).Should().BeFalse();
    }
}
