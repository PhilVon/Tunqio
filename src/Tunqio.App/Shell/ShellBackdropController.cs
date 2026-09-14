namespace Tunqio.App.Shell;

/// <summary>What the shell draws its base with: a system material, or its own solid surface for <see cref="ShellBackdrop.Kind.None"/>.</summary>
public interface IShellSurface
{
    /// <summary>Shows <paramref name="which"/>. Called on the UI thread; may throw, which the controller contains.</summary>
    void Show(ShellBackdrop.Kind which);
}

/// <summary>
/// Keeps the shell's backdrop in step with high contrast while the window runs (E7-S6, AC-522): the solid surface while
/// a high-contrast theme is in force, the machine's material again as soon as it is not, without a restart.
/// </summary>
/// <remarks>
/// <para>
/// Driven by the same <see cref="IAccessibilitySignals"/> reactive theming stops on, in the same two ways: the change
/// event, marshalled onto the UI thread through <c>post</c>, and a poll the window runs, so a lost notification costs one
/// poll interval rather than the rest of the session. Both call <see cref="Evaluate"/>, which does nothing unless the
/// answer changed, so a poll is a single <c>SystemParametersInfo</c> read.
/// </para>
/// <para>
/// Nothing leaves <see cref="Evaluate"/>: a surface that throws is logged once for that change and not retried until the
/// answer changes again, because retrying a failed backdrop once a second would fill the log with one fault.
/// </para>
/// </remarks>
public sealed class ShellBackdropController : IDisposable
{
    private readonly IAccessibilitySignals _signals;
    private readonly IShellSurface _surface;
    private readonly Action<Action> _post;
    private readonly Action<string> _info;
    private readonly Action<string> _warn;
    private ShellBackdrop.Kind? _attempted;
    private bool _disposed;

    /// <param name="signals">Where high contrast is read, live.</param>
    /// <param name="supported">What <see cref="ShellBackdrop.Probe"/> found on this machine.</param>
    /// <param name="surface">The window's base.</param>
    /// <param name="post">Runs an action on the UI thread; the change event is not raised on it.</param>
    /// <param name="info">One line per change of backdrop.</param>
    /// <param name="warn">One line per failed change.</param>
    public ShellBackdropController(
        IAccessibilitySignals signals,
        ShellBackdrop.Kind supported,
        IShellSurface surface,
        Action<Action> post,
        Action<string> info,
        Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(warn);
        _signals = signals;
        _surface = surface;
        _post = post;
        _info = info;
        _warn = warn;
        Supported = supported;
        _signals.Changed += OnSignalsChanged;
    }

    /// <summary>What the machine supports.</summary>
    public ShellBackdrop.Kind Supported { get; }

    /// <summary>What the shell is showing now: set only when <see cref="IShellSurface.Show"/> returned.</summary>
    public ShellBackdrop.Kind? Current { get; private set; }

    /// <summary>Why the last change failed, or null when it did not.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Reads high contrast and shows what it calls for, if that is not what was last asked for. Returns what the shell
    /// is showing. On the UI thread.
    /// </summary>
    public ShellBackdrop.Kind? Evaluate()
    {
        if (_disposed)
        {
            return Current;
        }

        bool highContrast;
        try
        {
            highContrast = _signals.HighContrast;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A read that fails is not a reason to change what is on screen.
            Report($"Shell backdrop: high contrast could not be read ({e.GetType().Name}: {e.Message}); left as it is");
            return Current;
        }

        ShellBackdrop.Kind wanted = ShellBackdrop.ForSurface(Supported, highContrast);
        if (_attempted == wanted)
        {
            return Current;
        }

        _attempted = wanted;
        bool first = Current is null;
        try
        {
            _surface.Show(wanted);
            Current = wanted;
            Problem = null;
            bool forHighContrast = highContrast && Supported != ShellBackdrop.Kind.None;
            // The first choice is logged by whoever applied it (App's "Shell backdrop: ..." line) unless high contrast
            // explains it, which that line cannot; every later change is this controller's to report.
            if (forHighContrast)
            {
                _info($"Shell backdrop: solid surface while high contrast is on ({Supported} supported)");
            }
            else if (!first)
            {
                _info($"Shell backdrop: {wanted} restored, high contrast is off");
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Report($"Shell backdrop: showing {wanted} failed ({e.GetType().Name}: {e.Message}); the window keeps what it had");
        }

        return Current;
    }

    /// <summary>Stops following the signals.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _signals.Changed -= OnSignalsChanged;
    }

    private void Report(string problem)
    {
        if (string.Equals(Problem, problem, StringComparison.Ordinal))
        {
            return;
        }

        Problem = problem;
        _warn(problem);
    }

    private void OnSignalsChanged(object? sender, EventArgs e) => _post(() => Evaluate());
}
