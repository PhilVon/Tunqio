using Tunqio.App.Playback;
using Tunqio.App.Shell;
using Tunqio.Core;

namespace Tunqio.App.Tests;

/// <summary>
/// E5-S1: the shell's mode. Persisted in <c>ui.mode</c> (AC-408), Esc back to the mode Focus was entered from
/// (AC-135, flow 7), and F11 in both directions (AC-410).
/// </summary>
public class ShellStateTests
{
    [Fact]
    public void A_first_run_opens_in_discovery()
    {
        new ShellState(new FakeSettings()).Mode.Should().Be(ShellMode.Discovery);
    }

    [Theory]
    [InlineData("discovery", ShellMode.Discovery)]
    [InlineData("focus", ShellMode.Focus)]
    [InlineData("curation", ShellMode.Curation)]
    [InlineData(" Curation ", ShellMode.Curation)]
    [InlineData("kiosk", ShellMode.Discovery)]
    public void A_launch_opens_in_the_stored_mode_and_anything_unrecognised_is_discovery(string stored, ShellMode expected)
    {
        var settings = new FakeSettings();
        settings.SetValue(SettingsKeys.UiMode, stored);

        new ShellState(settings).Mode.Should().Be(expected);
    }

    [Fact]
    public void A_switch_is_written_back_so_the_next_launch_opens_there()
    {
        var settings = new FakeSettings();
        var state = new ShellState(settings);

        state.Select(ShellMode.Curation).Should().BeTrue();

        settings.GetValue<string?>(SettingsKeys.UiMode, null).Should().Be("curation");
        new ShellState(settings).Mode.Should().Be(ShellMode.Curation, "a relaunch reads what the switch wrote");
    }

    [Fact]
    public void Selecting_the_mode_already_showing_changes_nothing_and_leaves_the_key_unhandled()
    {
        var settings = new FakeSettings();
        var state = new ShellState(settings);
        int notifications = 0;
        state.PropertyChanged += (_, _) => notifications++;

        state.Select(ShellMode.Discovery).Should().BeFalse();

        notifications.Should().Be(0);
        settings.Contains(SettingsKeys.UiMode).Should().BeFalse();
    }

    [Theory]
    [InlineData(ShellMode.Discovery)]
    [InlineData(ShellMode.Curation)]
    public void Esc_leaves_focus_for_the_mode_it_was_entered_from(ShellMode from)
    {
        var state = new ShellState(new FakeSettings());
        state.Select(from);

        state.Select(ShellMode.Focus);
        state.LeaveFocus().Should().BeTrue();

        state.Mode.Should().Be(from, "flow 7: Esc returns to the previous mode, not to the default one");
    }

    [Theory]
    [InlineData(ShellMode.Discovery)]
    [InlineData(ShellMode.Curation)]
    public void Esc_outside_focus_is_left_for_whatever_else_wants_it(ShellMode mode)
    {
        var state = new ShellState(new FakeSettings());
        state.Select(mode);

        state.LeaveFocus().Should().BeFalse("a search box clears on Esc and a flyout closes on it");
        state.Mode.Should().Be(mode);
    }

    [Fact]
    public void F11_goes_into_focus_and_back_out_to_where_it_came_from()
    {
        var state = new ShellState(new FakeSettings());
        state.Select(ShellMode.Curation);

        state.ToggleFocus().Should().BeTrue();
        state.Mode.Should().Be(ShellMode.Focus);
        state.ToggleFocus().Should().BeTrue();
        state.Mode.Should().Be(ShellMode.Curation);
    }

    [Fact]
    public void Focus_entered_from_focus_by_another_route_still_remembers_the_mode_before_it()
    {
        var state = new ShellState(new FakeSettings());
        state.Select(ShellMode.Curation);
        state.Select(ShellMode.Focus);

        state.Select(ShellMode.Focus).Should().BeFalse();
        state.LeaveFocus();

        state.Mode.Should().Be(ShellMode.Curation);
    }

    [Fact]
    public void A_launch_that_opens_in_focus_leaves_it_for_discovery()
    {
        var settings = new FakeSettings();
        settings.SetValue(SettingsKeys.UiMode, "focus");
        var state = new ShellState(settings);

        state.LeaveFocus().Should().BeTrue();

        state.Mode.Should().Be(ShellMode.Discovery, "the documented default is where Focus goes when nothing came before it");
    }

    /// <summary>
    /// AC-134, the half that can be pinned without a window: the object that switches modes has no route to the
    /// session. Playback continuing across a switch in the running app is the harness's to show.
    /// </summary>
    [Fact]
    public void The_mode_cannot_reach_playback()
    {
        Type[] dependencies =
        [
            .. typeof(ShellState).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType),
            .. typeof(ShellState).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Select(f => f.FieldType),
        ];

        dependencies.Should().NotContain(
            t => t == typeof(IPlaybackSessionSource) || t.Namespace == "Tunqio.Core.Playback" || t.Namespace == "Tunqio.App.Playback",
            "switching modes never interrupts playback, and the object that switches them has no way to try");
    }
}
