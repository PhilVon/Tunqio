using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Tunqio.App.Shell;

/// <summary>
/// Focus mode's controls (E5-S2, docs/ui-screens-and-flows.md): hidden after 3 s with no pointer movement and no key,
/// back the moment either happens, and never hidden in Discovery or Curation. The decision only; the window applies it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reappearing is not timed, and that is how AC-136's 100 ms is met.</b> <see cref="Activity"/> sets
/// <see cref="ControlsVisible"/> synchronously, so the window's opacity change happens inside the pointer or key
/// handler that reported it. Only hiding waits on a clock.
/// </para>
/// <para>
/// <b>One timer, not one per pointer move.</b> Pointer movement arrives dozens of times a second, so an input only
/// stamps the time. The timer runs to its due time, measures how long the input has really been idle, and either
/// hides the controls or waits out the remainder.
/// </para>
/// <para>
/// <b>Pins.</b> The controls must not vanish from under the pointer, from around keyboard focus (the accessibility
/// contract keeps a focusable transport so a keyboard user is never stranded), or from under the queue flyout. Each is
/// a named pin, and they are independent, because focus inside the bar and the pointer over it end at different times.
/// </para>
/// </remarks>
public sealed class FocusChrome : ObservableObject, IDisposable
{
    /// <summary>How long Focus waits with no input before hiding the controls.</summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(3);

    /// <summary>The pointer is over the controls panel.</summary>
    public const string PointerOverControls = "pointer-over-controls";

    /// <summary>Keyboard focus is inside the controls panel.</summary>
    public const string KeyboardInControls = "keyboard-in-controls";

    /// <summary>A flyout opened from the controls is showing.</summary>
    public const string FlyoutOpen = "flyout-open";

    private readonly ShellState _shell;
    private readonly TimeProvider _clock;
    private readonly SynchronizationContext? _ui;
    private readonly HashSet<string> _pins = new(StringComparer.Ordinal);
    private DateTimeOffset _lastActivity;
    private ITimer? _timer;
    private bool _visible = true;
    private bool _disposed;

    /// <param name="shell">The mode, which decides whether anything hides at all.</param>
    /// <param name="clock">Drives the idle wait; a manual clock is how a test advances it.</param>
    /// <param name="ui">The XAML thread's context; null evaluates inline, which is what the tests want.</param>
    public FocusChrome(ShellState shell, TimeProvider clock, SynchronizationContext? ui)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(clock);
        _shell = shell;
        _clock = clock;
        _ui = ui;
        _shell.PropertyChanged += OnShellChanged;
        Activity();
    }

    /// <summary>Whether the controls are showing.</summary>
    public bool ControlsVisible
    {
        get => _visible;
        private set => SetProperty(ref _visible, value);
    }

    /// <summary>Track changes are announced in Focus (accessibility contract), where the metadata may be all that is on screen.</summary>
    public bool AnnouncesTrackChanges => _shell.Mode == ShellMode.Focus;

    /// <summary>
    /// Whether focus arriving in the controls bar should hold it on screen: keyboard focus only. A click on the mode
    /// switcher moves focus into the bar too, and a pin taken on that would keep the controls up for as long as the
    /// switcher kept focus - in the Focus mode that click just entered. The contract this pin exists for is the keyboard
    /// user's (a focusable transport they are never stranded from), not the pointer's, which has its own pin.
    /// </summary>
    public static bool PinsOnFocus(Microsoft.UI.Xaml.FocusState state) => state == Microsoft.UI.Xaml.FocusState.Keyboard;

    /// <summary>When the last input (or a pin letting go) was seen, so a hide can say how long it really waited.</summary>
    public DateTimeOffset LastActivity => _lastActivity;

    /// <summary>True while anything is holding the controls on screen.</summary>
    public bool IsPinned => _pins.Count > 0;

    /// <summary>A pointer moved or a key went down: show the controls now, and start the idle wait again.</summary>
    public void Activity()
    {
        _lastActivity = _clock.GetUtcNow();
        ControlsVisible = true;
        if (_timer is null && Hides())
        {
            Schedule(IdleDelay);
        }
    }

    /// <summary>Holds the controls on screen for <paramref name="reason"/>, or lets go of it.</summary>
    public void Pin(string reason, bool on)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        if (on)
        {
            _pins.Add(reason);
            ControlsVisible = true;
            return;
        }

        if (_pins.Remove(reason) && _pins.Count == 0)
        {
            // Letting go counts as input: the controls do not vanish the instant the pointer leaves them.
            Activity();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shell.PropertyChanged -= OnShellChanged;
        _timer?.Dispose();
        _timer = null;
    }

    private bool Hides() => !_disposed && _shell.Mode == ShellMode.Focus && _pins.Count == 0;

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellState.Mode))
        {
            return;
        }

        OnPropertyChanged(nameof(AnnouncesTrackChanges));
        if (_shell.Mode == ShellMode.Focus)
        {
            Activity();
            return;
        }

        _timer?.Dispose();
        _timer = null;
        ControlsVisible = true;
    }

    private void Schedule(TimeSpan due)
    {
        _timer?.Dispose();
        _timer = _clock.CreateTimer(static state => ((FocusChrome)state!).OnTimer(), this, due, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        if (_ui is null)
        {
            Evaluate();
        }
        else
        {
            // No JoinableTaskFactory in this app; the context is the XAML thread's DispatcherQueue one and Post never blocks the caller.
#pragma warning disable VSTHRD001
            _ui.Post(static state => ((FocusChrome)state!).Evaluate(), this);
#pragma warning restore VSTHRD001
        }
    }

    private void Evaluate()
    {
        _timer?.Dispose();
        _timer = null;
        if (!Hides())
        {
            return;
        }

        TimeSpan idle = _clock.GetUtcNow() - _lastActivity;
        if (idle >= IdleDelay)
        {
            ControlsVisible = false;
        }
        else
        {
            Schedule(IdleDelay - idle);
        }
    }
}
