using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Serilog;
using Tunqio.App.Activation;
using Tunqio.App.Notifications;
using Tunqio.App.Shell;
using Tunqio.Library;
using Windows.ApplicationModel.Activation;

namespace Tunqio.App;

/// <summary>
/// The entry point (E7-S1; the XAML-generated <c>Main</c> is off, DISABLE_XAML_GENERATED_MAIN in the project). Start-up
/// step 2a of docs/solution-structure.md runs here, before the XAML app exists: find or register this data root's
/// instance key, and when another process already holds it, redirect this activation to that process and exit with 0.
/// Otherwise subscribe to the activations later processes redirect here, and start the app as the generated
/// <c>Main</c> did.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here may stop Tunqio from launching: a key that cannot be registered (a Windows App SDK failure) is written to
/// the debugger and the app starts as a plain, unshared instance. Measurement modes skip single instance altogether
/// (<see cref="InstanceKey.Applies"/>).
/// </para>
/// <para>
/// E7-S4: a process Windows starts to deliver a toast press (<see cref="ToastActions.ActivatedSwitch"/>) registers for app
/// notifications before it reads its activation, as the SDK requires, and takes its data root from the press. A press that
/// cannot be read ends the process with 0 rather than starting a player nobody asked for. <c>--unregister-notifications</c>
/// removes Tunqio's app notification registration and exits.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>Removes the user-level app notification registration (the SDK's UnregisterAll) and exits.</summary>
    public const string UnregisterNotificationsSwitch = "--unregister-notifications";

    /// <summary>The longest a second instance waits for the running one to accept its activation.</summary>
    private static readonly TimeSpan RedirectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Redirected activations, and toast presses, held until the window attaches a handler.</summary>
    public static ActivationInbox Inbox { get; } = new();

    /// <summary>
    /// This launch's own activation input for <see cref="CommandRouter"/>: the command line, or the files, URI or toast press a
    /// packaged or notification activation carried. Empty when <see cref="Main"/> did not run (a test host).
    /// </summary>
    public static IReadOnlyList<string> LaunchTokens { get; private set; } = [];

    /// <summary>The data root a toast press named, for a process Windows started to deliver it; null otherwise.</summary>
    public static string? ActivationDataRoot { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        LaunchTokens = args;
        if (args.Contains(UnregisterNotificationsSwitch, StringComparer.Ordinal))
        {
            return UnregisterNotifications(args);
        }

        bool toastPress = ToastActions.IsActivationLaunch(args);
        if (toastPress && !RegisterForToastPress())
        {
            return 0;
        }

        if (RedirectedToRunningInstance(args, toastPress))
        {
            return 0;
        }

        Application.Start(callback =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>Registers before the activation is read: the press reaches this process through the COM activator.</summary>
    private static bool RegisterForToastPress()
    {
        try
        {
            ToastActivation.Register();
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine($"Toast press: registering for app notifications failed, so the press cannot be read; exiting: {e}");
            return false;
        }
    }

    /// <summary><c>--unregister-notifications</c>: the SDK's UnregisterAll, logged to the data root's log. 0 when it ran.</summary>
    private static int UnregisterNotifications(string[] args)
    {
        string? dataRoot = DataRootSwitch.Path(args);
        Log.Logger = AppLogging.Create(dataRoot is null ? new AppPaths() : new AppPaths(dataRoot), Guid.NewGuid());
        try
        {
            ToastActivation.UnregisterAll();
            Log.Information("Toasts: {Switch} removed Tunqio's app notification registration", UnregisterNotificationsSwitch);
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error(e, "Toasts: {Switch} failed (HRESULT 0x{HResult:X8}); nothing may have been registered", UnregisterNotificationsSwitch, e.HResult);
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>True when this process handed its activation to the running instance, or has nothing to start with, and must exit.</summary>
    private static bool RedirectedToRunningInstance(string[] args, bool toastPress)
    {
        if (!InstanceKey.Applies(args))
        {
            return false;
        }

        string? dataRoot = DataRootSwitch.Path(args);
        AppActivationArguments activation;
        AppInstance keyed;
        string key;
        try
        {
            activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (toastPress)
            {
                if (activation.Kind != ExtendedActivationKind.AppNotification || activation.Data is not AppNotificationActivatedEventArgs press)
                {
                    throw new InvalidOperationException($"the toast press did not arrive (activation kind {activation.Kind})");
                }

                dataRoot = ToastActivation.DataRootOf(press) ?? dataRoot;
                ActivationDataRoot = dataRoot;
            }

            LaunchTokens = OwnTokens(activation, args);
            key = InstanceKey.For(dataRoot);
            keyed = AppInstance.FindOrRegisterForKey(key);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (toastPress)
            {
                // No data root can be trusted without the press, so nothing is written anywhere: a debugger line, and exit.
                System.Diagnostics.Debug.WriteLine($"Toast press could not be read; exiting without starting: {e}");
                return true;
            }

            System.Diagnostics.Debug.WriteLine($"Single instance unavailable, starting unshared: {e}");
            return false;
        }

        if (keyed.IsCurrent)
        {
            keyed.Activated += OnRedirectedActivation;
            return false;
        }

        Log.Logger = AppLogging.Create(dataRoot is null ? new AppPaths() : new AppPaths(dataRoot), Guid.NewGuid());
        try
        {
            bool allowed = NativeWindowing.AllowFor(keyed.ProcessId);
            Task redirect = Task.Run(() => keyed.RedirectActivationToAsync(activation).AsTask());
#pragma warning disable VSTHRD002 // Main, before any app exists; the redirect runs on the pool and the wait is capped.
            bool delivered = redirect.Wait(RedirectTimeout);
#pragma warning restore VSTHRD002
            if (delivered)
            {
                Log.Information(
                    "Single instance: {Kind} activation redirected to the running instance (pid {Pid}, key {Key}, foreground passed on {Allowed}); this process exits",
                    activation.Kind, keyed.ProcessId, key, allowed);
            }
            else
            {
                Log.Warning(
                    "Single instance: the running instance (pid {Pid}) did not accept the activation within {Timeout}; this process exits anyway",
                    keyed.ProcessId, RedirectTimeout);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error(e, "Single instance: redirecting to the running instance (pid {Pid}) failed; this process exits", keyed.ProcessId);
        }
        finally
        {
            Log.CloseAndFlush();
        }

        return true;
    }

    /// <summary>A later process's activation, on a Windows thread: turned into tokens here and held for the window.</summary>
    private static void OnRedirectedActivation(object? sender, AppActivationArguments activation)
    {
        try
        {
            IReadOnlyList<string> tokens = RedirectedTokens(activation);
            Log.Information("Single instance: received a redirected {Kind} activation with {Count} argument(s)", activation.Kind, tokens.Count);
            if (activation.Kind == ExtendedActivationKind.AppNotification && tokens.Count == 0)
            {
                // A press this build cannot read is nothing: an empty redirect would otherwise mean Show, and a toast button
                // must never bring the window forward.
                Log.Warning("Toasts: a redirected press carried no command Tunqio knows and was ignored");
                return;
            }

            Inbox.Post(tokens);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warning(e, "Single instance: a redirected activation could not be read and was ignored");
        }
    }

    /// <summary>This process's own input: the files, URI or toast press of the activation, else the command line.</summary>
    private static IReadOnlyList<string> OwnTokens(AppActivationArguments activation, string[] args) => DataTokens(activation) ?? args;

    /// <summary>
    /// A redirected activation's input. A launch carries one string: packaged, the arguments alone; unpackaged, the
    /// whole command line with the program first, which <see cref="CommandLineText.WithoutExecutable"/> drops.
    /// </summary>
    private static IReadOnlyList<string> RedirectedTokens(AppActivationArguments activation) =>
        DataTokens(activation)
        ?? (activation.Data is ILaunchActivatedEventArgs launch
            ? CommandLineText.WithoutExecutable(CommandLineText.Split(launch.Arguments))
            : []);

    private static IReadOnlyList<string>? DataTokens(AppActivationArguments activation) => activation.Kind switch
    {
        ExtendedActivationKind.File when activation.Data is IFileActivatedEventArgs file => [.. file.Files.Select(item => item.Path)],
        ExtendedActivationKind.Protocol when activation.Data is IProtocolActivatedEventArgs protocol => [protocol.Uri.OriginalString],
        ExtendedActivationKind.AppNotification when activation.Data is AppNotificationActivatedEventArgs press => ToastActivation.TokensOf(press),
        ExtendedActivationKind.AppNotification => [],
        _ => null,
    };
}
