using Tunqio.App.Shell;

namespace Tunqio.App.Tests;

/// <summary>
/// E5-S2: Focus mode's controls hide after 3 s without input and come back at once (AC-136), never hide outside Focus,
/// and stay while the pointer, keyboard focus or a flyout is on them.
/// </summary>
public sealed class FocusChromeTests : IDisposable
{
    private readonly ManualClock _clock = new();
    private readonly ShellState _shell = new(new FakeSettings());
    private readonly FocusChrome _chrome;

    public FocusChromeTests() => _chrome = new FocusChrome(_shell, _clock, ui: null);

    public void Dispose() => _chrome.Dispose();

    private void Wait(double seconds) => _clock.Advance(TimeSpan.FromSeconds(seconds));

    [Theory]
    [InlineData(ShellMode.Discovery)]
    [InlineData(ShellMode.Curation)]
    public void Outside_focus_the_controls_never_hide(ShellMode mode)
    {
        _shell.Select(mode);

        Wait(60);

        _chrome.ControlsVisible.Should().BeTrue();
    }

    [Fact]
    public void In_focus_the_controls_hide_after_three_seconds_without_input()
    {
        _shell.Select(ShellMode.Focus);

        Wait(2.9);
        _chrome.ControlsVisible.Should().BeTrue("three seconds have not passed");
        Wait(0.2);
        _chrome.ControlsVisible.Should().BeFalse();
    }

    [Fact]
    public void Input_during_the_wait_starts_it_again()
    {
        _shell.Select(ShellMode.Focus);

        Wait(2);
        _chrome.Activity();
        Wait(2);
        _chrome.ControlsVisible.Should().BeTrue("the pointer moved two seconds ago");
        Wait(1.1);
        _chrome.ControlsVisible.Should().BeFalse();
    }

    /// <summary>AC-136: the return is not on a clock at all, so it cannot take 100 ms.</summary>
    [Fact]
    public void Input_brings_hidden_controls_back_without_waiting_on_the_clock()
    {
        _shell.Select(ShellMode.Focus);
        Wait(3.1);
        _chrome.ControlsVisible.Should().BeFalse();

        _chrome.Activity();

        _chrome.ControlsVisible.Should().BeTrue("shown inside the pointer or key handler, with no time passing");
    }

    [Theory]
    [InlineData(FocusChrome.PointerOverControls)]
    [InlineData(FocusChrome.KeyboardInControls)]
    [InlineData(FocusChrome.FlyoutOpen)]
    public void A_pin_holds_the_controls_and_letting_go_waits_the_full_delay(string pin)
    {
        _shell.Select(ShellMode.Focus);
        _chrome.Pin(pin, on: true);

        Wait(30);
        _chrome.ControlsVisible.Should().BeTrue("the controls must not vanish from under {0}", pin);

        _chrome.Pin(pin, on: false);
        Wait(2.9);
        _chrome.ControlsVisible.Should().BeTrue("letting go counts as input");
        Wait(0.2);
        _chrome.ControlsVisible.Should().BeFalse();
    }

    [Theory]
    [InlineData(Microsoft.UI.Xaml.FocusState.Keyboard, true)]
    [InlineData(Microsoft.UI.Xaml.FocusState.Pointer, false)]
    [InlineData(Microsoft.UI.Xaml.FocusState.Programmatic, false)]
    [InlineData(Microsoft.UI.Xaml.FocusState.Unfocused, false)]
    public void Only_keyboard_focus_in_the_bar_holds_it(Microsoft.UI.Xaml.FocusState state, bool pins)
    {
        // A click on the mode switcher puts focus in the bar as well; pinned on that, Focus entered by the switcher
        // would never hide its controls.
        FocusChrome.PinsOnFocus(state).Should().Be(pins);
    }

    [Fact]
    public void Pins_are_independent()
    {
        _shell.Select(ShellMode.Focus);
        _chrome.Pin(FocusChrome.KeyboardInControls, on: true);
        _chrome.Pin(FocusChrome.PointerOverControls, on: true);

        _chrome.Pin(FocusChrome.PointerOverControls, on: false);
        Wait(10);

        _chrome.ControlsVisible.Should().BeTrue("keyboard focus is still inside the bar after the pointer left it");
    }

    [Fact]
    public void A_pin_brings_hidden_controls_back()
    {
        _shell.Select(ShellMode.Focus);
        Wait(3.1);

        _chrome.Pin(FocusChrome.KeyboardInControls, on: true);

        _chrome.ControlsVisible.Should().BeTrue("Tab into a hidden bar has to show it");
    }

    [Fact]
    public void Leaving_focus_shows_the_controls_and_they_stay()
    {
        _shell.Select(ShellMode.Focus);
        Wait(3.1);

        _shell.LeaveFocus();
        Wait(60);

        _chrome.ControlsVisible.Should().BeTrue();
    }

    [Fact]
    public void Track_changes_are_announced_in_focus_only()
    {
        _chrome.AnnouncesTrackChanges.Should().BeFalse();
        _shell.Select(ShellMode.Focus);
        _chrome.AnnouncesTrackChanges.Should().BeTrue();
        _shell.Select(ShellMode.Curation);
        _chrome.AnnouncesTrackChanges.Should().BeFalse();
    }

    [Fact]
    public void A_disposed_chrome_stops_listening_to_the_mode()
    {
        _chrome.Dispose();
        int notifications = 0;
        _chrome.PropertyChanged += (_, _) => notifications++;

        _shell.Select(ShellMode.Focus);
        Wait(10);

        notifications.Should().Be(0);
    }
}
