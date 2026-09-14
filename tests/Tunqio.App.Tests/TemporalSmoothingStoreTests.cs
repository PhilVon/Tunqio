using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Core.Visualization;

namespace Tunqio.App.Tests;

/// <summary>
/// T-184: <c>viz.temporalSmoothing</c>, <c>viz.temporalAttackMs</c> and <c>viz.temporalDecayMs</c>, and what the renderer
/// is given for them. What the envelope then does to a picture is mpcore.tests <c>[smoothing]</c>'s to prove.
/// </summary>
public class TemporalSmoothingStoreTests
{
    private readonly FakeSettings _settings = new();

    [Fact]
    public void A_first_run_is_off_and_draws_what_the_analysis_publishes()
    {
        TemporalSmoothingStore.ReadEnabled(_settings).Should().BeFalse();
        TemporalSmoothingStore.Read(_settings).Should().Be(TemporalSmoothing.Off);
        TemporalSmoothing.Off.IsOff.Should().BeTrue();
    }

    [Fact]
    public void Switched_on_it_is_the_stored_rise_and_fall_or_the_documented_defaults()
    {
        _settings.SetValue(SettingsKeys.VizTemporalSmoothing, true);
        TemporalSmoothingStore.Read(_settings).Should().Be(new TemporalSmoothing(20f, 300f));

        _settings.SetValue(SettingsKeys.VizTemporalAttackMs, 45f);
        _settings.SetValue(SettingsKeys.VizTemporalDecayMs, 900f);
        TemporalSmoothingStore.Read(_settings).Should().Be(new TemporalSmoothing(45f, 900f));
    }

    [Fact]
    public void Switched_off_the_numbers_are_kept_but_nothing_is_eased()
    {
        _settings.SetValue(SettingsKeys.VizTemporalAttackMs, 45f);
        _settings.SetValue(SettingsKeys.VizTemporalDecayMs, 900f);
        _settings.SetValue(SettingsKeys.VizTemporalSmoothing, false);

        TemporalSmoothingStore.Read(_settings).IsOff.Should().BeTrue();
        TemporalSmoothingStore.ReadAttackMs(_settings).Should().Be(45f, "turning it on again gives back what was chosen");
    }

    [Theory]
    [InlineData(-5f, 900f, 0f, 900f)]
    [InlineData(10_000f, 90_000f, 250f, 2000f)]
    [InlineData(float.NaN, float.PositiveInfinity, 20f, 300f)]
    public void A_hand_edited_value_outside_the_page_is_brought_back_into_it(float attack, float decay, float expectedAttack, float expectedDecay)
    {
        _settings.SetValue(SettingsKeys.VizTemporalSmoothing, true);
        _settings.SetValue(SettingsKeys.VizTemporalAttackMs, attack);
        _settings.SetValue(SettingsKeys.VizTemporalDecayMs, decay);

        TemporalSmoothingStore.Read(_settings).Should().Be(new TemporalSmoothing(expectedAttack, expectedDecay));
    }

    [Fact]
    public void The_default_rise_costs_at_most_one_refresh_at_60_hz()
    {
        // ADR-012: a transient within one display refresh. A rise reaches half height after attack * ln 2.
        (SettingsKeys.Defaults.VizTemporalAttackMs * Math.Log(2)).Should().BeLessThan(1000.0 / 60.0);
        SettingsKeys.Defaults.VizTemporalSmoothing.Should().BeFalse();
    }
}
