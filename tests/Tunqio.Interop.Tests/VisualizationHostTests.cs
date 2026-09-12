using Tunqio.Core.Visualization;

namespace Tunqio.Interop.Tests;

/// <summary>
/// <see cref="VisualizationHost"/> against the real core, headless on WARP: the attach/detach lifetime, the
/// preset catalogue, and what a preset that will not compile does to a host that was already drawing.
/// </summary>
[Collection("native renderer")]
public class VisualizationHostTests
{
    private static RendererConfig Headless => new(64, 64, ForceWarp: true, VSync: false, Headless: true);

    [Fact]
    public async Task A_detached_host_answers_rather_than_throwing_null()
    {
        using var host = new VisualizationHost();
        host.IsAttached.Should().BeFalse();
        host.Presets.Should().BeEmpty();
        host.ActivePresetId.Should().BeNull();
        FluentActions.Invoking(() => host.SetVisible(true)).Should().Throw<InvalidOperationException>();
        await FluentActions.Awaiting(() => host.SetPresetAsync("builtin-bars")).Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// T-147: the preset changing is a fact more than one thing needs, because the preset decides which
    /// parameters mean anything. Both moves raise it - attaching, which starts the catalogue's first entry, and
    /// switching - and a preset that will not compile raises nothing, because nothing changed.
    /// </summary>
    [Fact]
    public async Task Both_ways_the_active_preset_can_move_are_announced_and_a_failed_switch_is_not()
    {
        using var root = new PresetRootScope(PresetRootScope.Fixtures);
        using var host = new VisualizationHost();
        List<string> announced = [];
        host.PresetChanged += (_, id) => announced.Add(id);

        await host.AttachAsync(nint.Zero, Headless);
        // Attaching starts a preset, and that is a change: the catalogue's first entry is now drawing.
        announced.Should().Equal(["builtin-bars"]);

        await host.SetPresetAsync("solid-green");
        announced.Should().Equal("builtin-bars", "solid-green");

        await FluentActions.Awaiting(() => host.SetPresetAsync("broken-shader")).Should().ThrowAsync<PresetCompilationException>();
        announced.Should().Equal("builtin-bars", "solid-green");
        host.ActivePresetId.Should().Be("solid-green");
    }

    [Fact]
    public async Task Attaching_scans_the_catalogue_and_starts_on_the_built_in_preset()
    {
        using var root = new PresetRootScope(PresetRootScope.Fixtures);
        using var host = new VisualizationHost();
        await host.AttachAsync(nint.Zero, Headless);

        host.IsAttached.Should().BeTrue();
        host.Presets.Select(p => p.Id).Should().Contain(["builtin-bars", "solid-green"]);
        host.ActivePresetId.Should().Be("builtin-bars");

        await host.SetPresetAsync("solid-green");
        host.ActivePresetId.Should().Be("solid-green");
        host.SetParameter("level", 0.5f);
        host.Resize(128, 96, 1.5f, 1.5f);
        host.SetVisible(false);
        host.SetVisible(true);

        host.Detach();
        host.IsAttached.Should().BeFalse();
        host.Presets.Should().BeEmpty();
        host.ActivePresetId.Should().BeNull();
        FluentActions.Invoking(host.Detach).Should().NotThrow(); // idempotent
    }

    // AC-117 at the level the preset switcher sees: the exception carries the message to show, and the host is
    // still attached, still on the preset it was on, and still rendering.
    [Fact]
    public async Task A_preset_that_does_not_compile_leaves_the_previous_one_running()
    {
        using var root = new PresetRootScope(PresetRootScope.Fixtures);
        using var host = new VisualizationHost();
        await host.AttachAsync(nint.Zero, Headless);
        await host.SetPresetAsync("solid-green");

        PresetCompilationException thrown = (await FluentActions.Awaiting(() => host.SetPresetAsync("broken-shader"))
            .Should().ThrowAsync<PresetCompilationException>()).Which;
        thrown.PresetId.Should().Be("broken-shader");
        thrown.CompilerMessage.Should().Contain("error X");
        thrown.Message.Should().Contain("broken-shader");

        host.ActivePresetId.Should().Be("solid-green");
        host.IsAttached.Should().BeTrue();

        var seen = new List<RenderStats>();
        using (host.Stats.Subscribe(seen.Add))
        {
            await Task.Delay(VisualizationHost.StatsInterval * 3);
        }

        seen.Should().NotBeEmpty();
        seen[^1].DeviceLost.Should().BeFalse();
        seen[^1].Frames.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Attaching_twice_replaces_the_renderer_rather_than_leaking_it()
    {
        using var root = new PresetRootScope(PresetRootScope.Fixtures);
        using var host = new VisualizationHost();
        await host.AttachAsync(nint.Zero, Headless);
        await host.SetPresetAsync("solid-blue");
        await host.AttachAsync(nint.Zero, Headless with { Width = 128, Height = 128 });

        host.IsAttached.Should().BeTrue();
        host.ActivePresetId.Should().Be("builtin-bars"); // a new renderer starts on the built-in again
        await Task.Delay(VisualizationHost.StatsInterval * 2);
        host.Pushed.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Disposing_detaches_and_refuses_further_use()
    {
        using var root = new PresetRootScope(PresetRootScope.Fixtures);
        var host = new VisualizationHost();
        await host.AttachAsync(nint.Zero, Headless);
        host.Dispose();

        host.IsAttached.Should().BeFalse();
        FluentActions.Invoking(() => host.SetVisible(true)).Should().Throw<ObjectDisposedException>();
        await FluentActions.Awaiting(() => host.AttachAsync(nint.Zero, Headless)).Should().ThrowAsync<ObjectDisposedException>();
        FluentActions.Invoking(host.Dispose).Should().NotThrow();
    }

    [Fact]
    public async Task The_theme_is_forwarded_and_taken_and_quality_still_names_its_story()
    {
        using var host = new VisualizationHost();
        await host.AttachAsync(nint.Zero, Headless);

        // E4-S6: mp_renderer_set_theme is implemented, so what reached MP_E_STATE now reaches b0. What the four
        // colours do to the picture is mpcore.tests [theme]'s to prove; what this proves is that the managed
        // struct crosses the boundary intact - a channel out of range is clamped rather than refused, and a
        // channel that is not a number is refused rather than written.
        var colors = new ThemeColors([1, 0, 0, 1], [0, 1, 0, 1], [0, 0, 1, 1], [0, 0, 0, 1]);
        FluentActions.Invoking(() => host.SetThemeColors(colors)).Should().NotThrow();
        FluentActions.Invoking(() => host.SetThemeColors(new ThemeColors([4, -2, 0.5f, 1], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0])))
            .Should().NotThrow();
        FluentActions.Invoking(() => host.SetThemeColors(new ThemeColors([float.NaN, 0, 0, 1], [0, 0, 0, 0], [0, 0, 0, 0], [0, 0, 0, 0])))
            .Should().Throw<NativeException>().WithMessage("*finite*");

        FluentActions.Invoking(() => host.SetQualityPolicy(QualityPolicy.Auto)).Should().Throw<NativeException>().WithMessage("*E4-S7*");
    }

    [Fact]
    public async Task A_palette_the_reactive_theming_produced_is_one_the_renderer_takes()
    {
        // The join between the two halves of E4-S6: the colours the shell is painting its own gradient from are
        // the ones handed to every preset, through the same record, without a conversion in between.
        using var host = new VisualizationHost();
        await host.AttachAsync(nint.Zero, Headless);

        var engine = new ReactiveThemeEngine(true, ReactiveThemeOptions.Default);
        ReactiveThemePalette palette = engine.Advance(
            new AnalysisFrame(1, 0, 0, default, default, 0.25f, 0.3f, 4000f, 0.8f, default, false, 0),
            TimeSpan.FromSeconds(1));

        FluentActions.Invoking(() => host.SetThemeColors(palette.ToThemeColors())).Should().NotThrow();
    }
}
