using Microsoft.Windows.AppNotifications;
using Tunqio.App.Activation;

namespace Tunqio.App.Notifications;

/// <summary>
/// The process-wide half of app notifications (E7-S4): <see cref="AppNotificationManager.Default"/> is one per process, a press
/// handler can only be added before the first Register, and Register may happen in <see cref="Program"/> (a process Windows
/// started to deliver a press) before the app exists. So this class owns the handler and the registered state, and a press
/// goes where every other activation goes: <see cref="Program.Inbox"/>, then <see cref="CommandRouter"/> (E7-S1).
/// </summary>
/// <remarks>
/// A press on a running Tunqio that is registered reaches <see cref="OnInvoked"/> in that process: the SDK registers the COM
/// activator for many uses when a handler is present, so no second process starts. A press when no Tunqio is registered makes
/// Windows start <c>Tunqio.exe ----AppNotificationActivated:</c>; <see cref="Program"/> registers first, reads the press from
/// the activation, and hands it to an ordinary launch of Tunqio.exe, which redirects to a running instance or starts.
/// </remarks>
internal static class ToastActivation
{
    private static readonly object Gate = new();
    private static bool _handlerAdded;
    private static bool _registered;

    /// <summary>Whether this process has package identity (the COM activator is the manifest's).</summary>
    public static bool IsPackaged
    {
        get
        {
            try
            {
                return Windows.ApplicationModel.Package.Current is not null;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Registers the process, adding the press handler first when <paramref name="receivePresses"/> is true. False when it was
    /// already registered. Without the handler the SDK takes the one activation this process was started for and no more,
    /// which is what the press trampoline in <see cref="Program"/> wants.
    /// </summary>
    public static bool Register(bool receivePresses)
    {
        lock (Gate)
        {
            if (_registered)
            {
                return false;
            }

            AppNotificationManager manager = AppNotificationManager.Default;
            if (receivePresses && !_handlerAdded)
            {
                // Before Register, or the SDK refuses it ("Must register event handlers before calling Register()").
                manager.NotificationInvoked += OnInvoked;
                _handlerAdded = true;
            }

            manager.Register();
            _registered = true;
            return true;
        }
    }

    /// <summary>Stops this process receiving presses (the SDK's Unregister: in-process only, the registry is left as it is).</summary>
    public static void Unregister()
    {
        lock (Gate)
        {
            if (!_registered)
            {
                return;
            }

            AppNotificationManager.Default.Unregister();
            _registered = false;
        }
    }

    /// <summary>
    /// The SDK's UnregisterAll: in this process, and unpackaged also Tunqio's user-level registration (its AppUserModelId entry,
    /// its COM activator CLSID and the icon the SDK cached). For <c>Tunqio.exe --unregister-notifications</c>.
    /// </summary>
    public static void UnregisterAll()
    {
        lock (Gate)
        {
            AppNotificationManager.Default.UnregisterAll();
            _registered = false;
        }
    }

    /// <summary>A press on a toast while this process is registered, on a Windows thread.</summary>
    private static void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            IReadOnlyList<string> tokens = TokensOf(args);
            Serilog.Log.Information("Toasts: pressed ({Argument}), routed as {Tokens}", args.Argument, string.Join(' ', tokens));
            if (tokens.Count > 0)
            {
                Program.Inbox.Post(tokens);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Serilog.Log.Warning(e, "Toasts: a press could not be read and was ignored");
        }
    }

    /// <summary>The router's input for a press.</summary>
    public static IReadOnlyList<string> TokensOf(AppNotificationActivatedEventArgs args) => ToastActions.TokensFor(Arguments(args));

    /// <summary>The data root a press names, or null.</summary>
    public static string? DataRootOf(AppNotificationActivatedEventArgs args) => ToastActions.DataRootOf(Arguments(args));

    private static Dictionary<string, string> Arguments(AppNotificationActivatedEventArgs args) =>
        args.Arguments is { } arguments
            ? arguments.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            : [];
}
