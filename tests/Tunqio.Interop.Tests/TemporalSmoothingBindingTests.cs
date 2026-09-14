using Tunqio.Core.Visualization;

namespace Tunqio.Interop.Tests;

/// <summary>
/// T-184, ABI 0.19: <c>mp_renderer_set_temporal_smoothing</c> and <c>mp_renderer_get_temporal_smoothing</c> through the
/// managed binding against the built mpcore.dll, the way T-144 asked for an export to be checked: against the real
/// core rather than a header the test and the core happen to share. <see cref="HeaderCoverageTests"/> proves both are
/// bound and exported; this proves the two floats cross in the right order and come back as the core clamped them.
/// What the envelope does to a picture is mpcore.tests <c>[smoothing]</c>'s.
/// </summary>
[Collection("native renderer")]
public class TemporalSmoothingBindingTests
{
    private static RendererConfig Config => new(64, 64, ForceWarp: true, VSync: false, Headless: true);

    [Fact]
    public void Off_until_set_and_read_back_as_given_in_attack_then_decay_order()
    {
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(Config, nint.Zero);
        renderer.GetTemporalSmoothing().Should().Be(TemporalSmoothing.Off);

        // Two different numbers, so a swapped pair of arguments reads back swapped.
        renderer.SetTemporalSmoothing(20f, 300f);
        renderer.GetTemporalSmoothing().Should().Be(new TemporalSmoothing(20f, 300f));

        renderer.SetTemporalSmoothing(0f, 0f);
        renderer.GetTemporalSmoothing().IsOff.Should().BeTrue();
    }

    [Fact]
    public void Values_past_the_cores_limits_are_clamped_and_nonsense_is_refused_without_changing_anything()
    {
        using NativeRenderer renderer = NativeRenderer.CreateHeadless(Config, nint.Zero);
        renderer.SetTemporalSmoothing(1e6f, 1e6f);
        renderer.GetTemporalSmoothing().Should().Be(new TemporalSmoothing(1000f, 5000f));

        renderer.SetTemporalSmoothing(40f, 400f);
        FluentActions.Invoking(() => renderer.SetTemporalSmoothing(-1f, 400f))
            .Should().Throw<NativeException>().WithMessage("*negative*");
        FluentActions.Invoking(() => renderer.SetTemporalSmoothing(40f, float.NaN))
            .Should().Throw<NativeException>().WithMessage("*finite*");
        renderer.GetTemporalSmoothing().Should().Be(new TemporalSmoothing(40f, 400f));
    }

    [Fact]
    public async Task The_host_forwards_it_to_the_renderer_it_built_and_refuses_while_detached()
    {
        using var host = new VisualizationHost();
        FluentActions.Invoking(() => host.SetTemporalSmoothing(new TemporalSmoothing(1f, 1f)))
            .Should().Throw<InvalidOperationException>();

        await host.AttachAsync(nint.Zero, nint.Zero, Config);
        host.SetTemporalSmoothing(new TemporalSmoothing(35f, 700f));

        host.AttachedRenderer!.GetTemporalSmoothing().Should().Be(new TemporalSmoothing(35f, 700f));
    }
}
