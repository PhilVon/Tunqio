using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Library;
using Tunqio.App.Playback;
using Tunqio.Core.Library;
using Tunqio.Core.Playback;

namespace Tunqio.App.Shell;

/// <summary>
/// What a notice is about, which is also what makes two of them the same notice (E2-S7). A second scan report
/// replaces the first rather than stacking under it; a device coming back replaces the bar that said it had gone.
/// </summary>
public enum NoticeKind
{
    /// <summary>Something start-up had to say: a recovered database, an output that would not open.</summary>
    Startup,

    /// <summary>The state of the output — the sticky one, and the only kind that carries an action.</summary>
    Output,

    /// <summary>An engine failure, told once.</summary>
    EngineError,

    /// <summary>What the last scan did.</summary>
    Scan,

    /// <summary>The last tag edit, and the offer to undo it (E3-S10, flow 8).</summary>
    TagEdit,

    /// <summary>The one-time offer to turn hover previews on (E5-S5, Q-74).</summary>
    HoverPreview,
}

/// <summary>One bar in the shell's notice area.</summary>
public sealed partial class ShellNotice : ObservableObject
{
    internal ShellNotice(
        NoticeKind kind,
        string title,
        string message,
        StartupNoticeSeverity severity,
        string? actionText = null,
        Func<Task>? action = null,
        bool sticky = false)
    {
        Kind = kind;
        Title = title;
        Message = message;
        Severity = severity;
        ActionText = actionText;
        Action = action;
        IsSticky = sticky;
    }

    /// <summary>What it is about; at most one notice of each kind is shown at a time.</summary>
    public NoticeKind Kind { get; }

    public string Title { get; }

    public string Message { get; }

    public StartupNoticeSeverity Severity { get; }

    /// <summary>The action button's label, or null for a bar that only says something.</summary>
    public string? ActionText { get; }

    /// <summary>What the action button does.</summary>
    public Func<Task>? Action { get; }

    /// <summary>True when the bar stays until the thing it is about has changed, rather than timing out.</summary>
    public bool IsSticky { get; }

    /// <summary>Whether the bar offers an action, for the binding that shows the button.</summary>
    public bool HasAction => Action is not null && !string.IsNullOrEmpty(ActionText);
}

/// <summary>
/// The shell's error surfaces (E2-S7, docs/ui-screens-and-flows.md flow 4): the bars the window shows, and the
/// rules about which of them stay. Everything it says comes from somewhere else — <see cref="PlaybackSnapshot"/>
/// for the output, <see cref="PlaybackSession.Errors"/> for engine failures, the scan coordinator for scans — and
/// like every other view model in the shell it holds no state those owners do not already have.
/// </summary>
/// <remarks>
/// <para>
/// The sticky / transient split is not a property of the message, it is a property of what the message is about. A
/// disconnected device is a state the user is still in a minute later, and a bar that timed out would leave silence
/// with no explanation, which is the failure flow 4 exists to prevent — so it comes off the snapshot and stays until
/// the snapshot says otherwise. An engine error and a finished scan are events: they happened, they are told once,
/// and they go away on their own.
/// </para>
/// <para>
/// At most one notice per <see cref="NoticeKind"/>. Without that, an engine that fails on every buffer would stack
/// bars until the window was nothing else, and a scan that runs twice would leave the first report on screen saying
/// something no longer true.
/// </para>
/// </remarks>
public sealed partial class ShellNotices : ObservableObject, IDisposable
{
    /// <summary>How long a notice about something that has already happened stays on the screen.</summary>
    public static readonly TimeSpan TransientLifetime = TimeSpan.FromSeconds(8);

    private readonly SynchronizationContext? _ui;
    private readonly IPlaybackSessionSource? _source;
    private readonly LibraryScanCoordinator? _scans;
    private readonly TimeProvider _time;
    private readonly Dictionary<NoticeKind, ITimer> _expiries = [];
    private IDisposable? _snapshots;
    private IDisposable? _errors;
    private PlaybackSession? _session;
    private OutputStatus _output = OutputStatus.Ok;
    private DateTimeOffset? _reported;
    private bool _disposed;

    /// <param name="source">Where the session comes from; it may not exist yet, and may never.</param>
    /// <param name="scans">The library's scans, for the report bar; null leaves scans unreported.</param>
    /// <param name="ui">The XAML thread's context. Null runs updates inline, which is what the tests want.</param>
    /// <param name="clock">Times the transient bars out; a fake clock is how a test watches one go.</param>
    /// <remarks>
    /// <paramref name="source"/> and <paramref name="scans"/> are required and positional, with no default
    /// (T-180): both are subscriptions, and a notices object that quietly subscribed to nothing looks exactly
    /// like one with nothing to report. Still nullable — the tag editor's tests want a bar wired to neither —
    /// but the caller has to say so.
    /// </remarks>
    public ShellNotices(
        IPlaybackSessionSource? source,
        LibraryScanCoordinator? scans,
        SynchronizationContext? ui = null,
        TimeProvider? clock = null)
    {
        _source = source;
        _scans = scans;
        _ui = ui;
        _time = clock ?? TimeProvider.System;

        if (source is not null)
        {
            if (source.Session is { } ready)
            {
                Attach(ready);
            }
            else
            {
                source.SessionReady += OnSessionReady;
            }
        }

        if (scans is not null)
        {
            scans.StateChanged += OnScanStateChanged;
        }
    }

    /// <summary>The bars on the screen, newest last.</summary>
    public ObservableCollection<ShellNotice> Items { get; } = [];

    /// <summary>Whether there is anything to show, for the host that would otherwise reserve the space.</summary>
    public bool Any => Items.Count > 0;

    /// <summary>
    /// Shows a start-up notice (a recovered database, an output that would not open). Sticky: it is about the state
    /// the app came up in, and the user may not be looking when it appears.
    /// </summary>
    public void Show(StartupNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        Put(new ShellNotice(NoticeKind.Startup, notice.Title, notice.Message, notice.Severity, sticky: true));
    }

    /// <summary>
    /// The bar a finished tag edit leaves behind, carrying the undo (docs/ui-screens-and-flows.md flow 8: "Undo
    /// available from the sidebar <c>InfoBar</c> for the session"). Sticky, unlike the other bars about things
    /// that have already happened, because this one is not only a report: it is the only way to reach the undo,
    /// and a bar that timed out after eight seconds would take the undo with it. It goes when the user dismisses
    /// it, when they use it, or when the next edit replaces it.
    /// </summary>
    /// <param name="undo">Runs the undo; the bar takes itself down afterwards.</param>
    public void ShowTagEdit(TagEditReport report, Func<Task> undo)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(undo);
        bool failed = report.Failed > 0 || report.Error is not null;
        Put(new ShellNotice(
            NoticeKind.TagEdit,
            report.IsUndo ? "Tag edit undone" : report.Description,
            report.Error is { } error ? report.Summary() + " " + error : report.Summary(),
            failed ? StartupNoticeSeverity.Warning : StartupNoticeSeverity.Informational,
            report.IsUndo || report.Written == 0 ? null : "Undo",
            report.IsUndo || report.Written == 0 ? null : async () =>
            {
                await undo().ConfigureAwait(true);
                Post(() => Remove(NoticeKind.TagEdit));
            },
            sticky: true));
    }

    /// <summary>
    /// The first-hover offer (E5-S5, Q-74, R-15's first-use prompt): the pointer has rested on an album in Discovery
    /// with previews off, so the user is told the feature exists where they would use it. Sticky, because it is a
    /// question rather than a report; it goes when they turn previews on or close the bar, and it is only ever
    /// raised once.
    /// </summary>
    /// <param name="turnOn">Turns previews on; the bar takes itself down afterwards.</param>
    public void ShowHoverPreviewOffer(Action turnOn)
    {
        ArgumentNullException.ThrowIfNull(turnOn);
        Put(new ShellNotice(
            NoticeKind.HoverPreview,
            "Preview albums on hover?",
            "Rest the pointer on an album for half a second to hear a few seconds of it, quietly. You can turn this off again in Settings › Library.",
            StartupNoticeSeverity.Informational,
            "Turn on",
            () =>
            {
                turnOn();
                Post(() => Remove(NoticeKind.HoverPreview));
                return Task.CompletedTask;
            },
            sticky: true));
    }

    /// <summary>Dismisses <paramref name="notice"/> — what the bar's own close button does.</summary>
    public void Dismiss(ShellNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        Remove(notice.Kind);
    }

    // ---- what the notices are made of ---------------------------------------------------------------------------

    private void OnSessionReady(object? sender, PlaybackSession session)
    {
        if (_source is not null)
        {
            _source.SessionReady -= OnSessionReady;
        }

        Post(() => Attach(session));
    }

    private void Attach(PlaybackSession session)
    {
        if (_disposed)
        {
            return;
        }

        _session = session;
        _snapshots = session.Snapshots.Subscribe(s => Post(() => ApplyOutput(s.Output)));
        _errors = session.Errors.Subscribe(message => Post(() => ShowError(message)));
    }

    /// <summary>
    /// The output bar. Driven by the difference between two snapshots rather than by every snapshot, so the bar is
    /// not replaced ten times a second — and so that dismissing it does not simply bring it back on the next one.
    /// </summary>
    private void ApplyOutput(OutputStatus status)
    {
        if (status == _output)
        {
            return;
        }

        _output = status;
        switch (status.Health)
        {
            case OutputHealth.Lost:
                Put(new ShellNotice(
                    NoticeKind.Output,
                    "Output device disconnected",
                    "Playback is paused where it was. Choose another output, or plug the device back in.",
                    StartupNoticeSeverity.Warning,
                    "Use default device",
                    () => _session?.UseSystemDefaultOutputAsync() ?? Task.FromResult(false),
                    sticky: true));
                break;
            case OutputHealth.Returned:
                Put(new ShellNotice(
                    NoticeKind.Output,
                    "Output device reconnected",
                    Name(status) + " is available again.",
                    StartupNoticeSeverity.Informational,
                    "Switch back",
                    () => _session?.SwitchToReturnedOutputAsync() ?? Task.FromResult(false),
                    sticky: true));
                break;
            default:
                Remove(NoticeKind.Output);
                break;
        }
    }

    private static string Name(OutputStatus status) =>
        string.IsNullOrWhiteSpace(status.DeviceName) ? "The device that was disconnected" : status.DeviceName;

    private void ShowError(string message) => Put(new ShellNotice(
        NoticeKind.EngineError, "Playback problem", message, StartupNoticeSeverity.Warning));

    /// <summary>
    /// A finished scan, reported once — and only when it has something to report. The coordinator raises
    /// <c>StateChanged</c> for progress as well as for the report, so a report is recognised by when it landed
    /// rather than by what it contains: a scan that changed nothing is still a scan that finished, and the
    /// library's version does not move for one.
    /// </summary>
    /// <remarks>
    /// A scan that found nothing new says nothing. The launch scan runs every time the app starts and usually finds
    /// exactly what it found last time, so reporting it would put "Scan finished · 0 files" on the screen at every
    /// launch — which trains the user to ignore the bar that the *next* story wants them to read. The settings page
    /// shows the last report whether or not it changed anything (E3-S12); that is where a scan that did nothing
    /// belongs.
    /// </remarks>
    private void OnScanStateChanged(object? sender, EventArgs e) => Post(() =>
    {
        if (_scans is not { IsScanning: false, LastReport: { } report, LastReportAt: { } at } || at == _reported)
        {
            return;
        }

        _reported = at;
        bool failed = report.Failed > 0 || report.Error is not null;
        if (!failed && !LibraryScanCoordinator.ChangedRows(report))
        {
            return;
        }

        Put(new ShellNotice(
            NoticeKind.Scan,
            failed ? "Scan finished with problems" : "Scan finished",
            LibraryScanCoordinator.Describe(report),
            failed ? StartupNoticeSeverity.Warning : StartupNoticeSeverity.Informational));
    });

    // ---- the collection ------------------------------------------------------------------------------------------

    /// <summary>
    /// Puts <paramref name="notice"/> up, replacing whatever was there about the same thing, and starts the clock
    /// on it when it is one of the ones that goes away by itself.
    /// </summary>
    private void Put(ShellNotice notice)
    {
        Remove(notice.Kind);
        Items.Add(notice);
        OnPropertyChanged(nameof(Any));
        if (!notice.IsSticky)
        {
            _expiries[notice.Kind] = _time.CreateTimer(
                _ => Post(() => Remove(notice.Kind)), null, TransientLifetime, Timeout.InfiniteTimeSpan);
        }
    }

    private void Remove(NoticeKind kind)
    {
        if (_expiries.Remove(kind, out ITimer? timer))
        {
            timer.Dispose();
        }

        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].Kind == kind)
            {
                Items.RemoveAt(i);
            }
        }

        OnPropertyChanged(nameof(Any));
    }

    private void Post(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
            return;
        }

        // No JoinableTaskFactory in this app; the context is the XAML thread's DispatcherQueue one and Post never blocks the caller.
#pragma warning disable VSTHRD001
        _ui.Post(_ => action(), null);
#pragma warning restore VSTHRD001
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_source is not null)
        {
            _source.SessionReady -= OnSessionReady;
        }

        if (_scans is not null)
        {
            _scans.StateChanged -= OnScanStateChanged;
        }

        foreach (ITimer timer in _expiries.Values)
        {
            timer.Dispose();
        }

        _expiries.Clear();
        _snapshots?.Dispose();
        _errors?.Dispose();
        _session = null;
    }
}
