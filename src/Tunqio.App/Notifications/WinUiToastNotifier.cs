using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Tunqio.App.Notifications;

/// <summary>
/// <see cref="IToastNotifier"/> over Windows App SDK app notifications (<c>Microsoft.Windows.AppNotifications</c>, ADR-006).
/// Every call runs on one queue on the thread pool, in order, so registering, showing and removing never block the snapshot
/// thread or the XAML thread and never interleave.
/// </summary>
/// <remarks>
/// Unpackaged, <see cref="AppNotificationManager.Register()"/> writes Tunqio's own registration under HKEY_CURRENT_USER (an
/// AppUserModelId entry and a COM activator CLSID whose LocalServer32 is this executable); packaged, it writes nothing and the
/// activator is the manifest's. <see cref="ToastActivation"/> owns the process-wide part: the press handler, which has to be in
/// place before the first Register, and whether this process is registered.
/// </remarks>
internal sealed class WinUiToastNotifier : IToastNotifier
{
    /// <summary>The longest shutdown waits for the queue to remove the toast.</summary>
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly string? _dataRoot;
    private readonly ILogger _log;
    private Task _tail = Task.CompletedTask;
    private bool _disposed;

    /// <param name="dataRoot">The <c>--data-root</c> this process runs on, carried in every press; null on the default profile.</param>
    /// <param name="log">Where posts and failures are recorded.</param>
    public WinUiToastNotifier(string? dataRoot, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _dataRoot = dataRoot;
        _log = log;
    }

    public void Register() => Enqueue("register", () =>
    {
        if (ToastActivation.Register(receivePresses: true))
        {
            _log.LogInformation("Toasts: AppNotificationManager registered (packaged {Packaged})", ToastActivation.IsPackaged);
        }
    });

    public void Unregister() => Enqueue("unregister", ToastActivation.Unregister);

    public void Show(TrackToast toast)
    {
        ArgumentNullException.ThrowIfNull(toast);
        Enqueue("show", () =>
        {
            AppNotification notification = Build(toast);
            AppNotificationManager.Default.Show(notification);
            // The payload is logged whole: tools/check-toasts.ps1 reads the buttons' arguments from it and presses them the way
            // the notification platform does.
            _log.LogInformation("Toasts: posted notification {Id}; payload {Payload}", notification.Id, notification.Payload);
        });
    }

    public void Remove() => Enqueue("remove", () =>
    {
        // On the queue, which is the pool: waiting keeps Remove and the Unregister after it in order, and the wait is capped.
#pragma warning disable VSTHRD002
        bool removed = AppNotificationManager.Default.RemoveByTagAndGroupAsync(IToastNotifier.Tag, IToastNotifier.Group).AsTask().Wait(DisposeWait);
#pragma warning restore VSTHRD002
        if (!removed)
        {
            _log.LogWarning("Toasts: removing the toast did not finish within {Wait}", DisposeWait);
        }
    });

    /// <summary>The app notification for <paramref name="toast"/>: silent, tagged and grouped so the next one replaces it.</summary>
    private AppNotification Build(TrackToast toast)
    {
        AppNotificationBuilder builder = new AppNotificationBuilder()
            .AddArgument(ToastActions.ActionArgument, ToastActions.ValueOf(ToastAction.Show));
        AddDataRoot(builder);
        builder.AddText(toast.Title);
        if (toast.Artist.Length > 0)
        {
            builder.AddText(toast.Artist);
        }

        if (toast.Album.Length > 0)
        {
            builder.AddText(toast.Album);
        }

        builder.SetAppLogoOverride(new Uri(toast.ImagePath), AppNotificationImageCrop.Default);
        builder.MuteAudio();
        foreach (ToastButton button in toast.Buttons)
        {
            var added = new AppNotificationButton(button.Label).AddArgument(ToastActions.ActionArgument, ToastActions.ValueOf(button.Action));
            if (_dataRoot is not null)
            {
                added.AddArgument(ToastActions.DataRootArgument, _dataRoot);
            }

            builder.AddButton(added);
        }

        AppNotification notification = builder.BuildNotification();
        notification.Tag = IToastNotifier.Tag;
        notification.Group = IToastNotifier.Group;
        return notification;
    }

    private void AddDataRoot(AppNotificationBuilder builder)
    {
        if (_dataRoot is not null)
        {
            builder.AddArgument(ToastActions.DataRootArgument, _dataRoot);
        }
    }

    private void Enqueue(string what, Action work)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _tail = _tail.ContinueWith(
                _ =>
                {
                    try
                    {
                        work();
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        _log.LogWarning(e, "Toasts: {What} failed", what);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    /// <summary>Lets what is queued (the controller's last Remove and Unregister) finish, for at most a few seconds.</summary>
    public void Dispose()
    {
        Task tail;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            tail = _tail;
        }

#pragma warning disable VSTHRD002 // Shutdown, on the XAML thread: the work is on the pool and the wait is capped.
        if (!tail.Wait(DisposeWait + DisposeWait))
#pragma warning restore VSTHRD002
        {
            _log.LogWarning("Toasts: the notification queue did not finish at shutdown");
        }
    }
}
