using System.Diagnostics;
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
/// E7-S4: a process Windows starts to deliver a toast press (<see cref="ToastActions.ActivatedSwitch"/>) is a trampoline. It
/// registers for app notifications, as the SDK requires before the activation can be read, reads the press, and starts
/// Tunqio.exe again with the press as a <c>tunqio://</c> command (and the press's <c>--data-root</c>), then exits with 0. That
/// launch is an ordinary one: it redirects to the running instance through the key above, or starts the app. The press process
/// cannot redirect itself: it is a COM activation, which a relaunch would lose, and COM starts it from the lowercase path the
/// SDK registered. It starts Tunqio.exe from the true spelling of its path, as the step below would. <c>--unregister-notifications</c>
/// removes Tunqio's app notification registration and exits.
/// </para>
/// <para>
/// T-192: before the key, an unpackaged launch from any other spelling of Tunqio.exe's path (letter case, above all) starts
/// Tunqio.exe again from the file system's spelling and exits, because AppInstance scopes keys by the exact module path
/// (<see cref="ExecutablePath"/>). Only then is the key found or registered.
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
    /// This launch's own activation input for <see cref="CommandRouter"/>: the command line, or the files or URI a packaged
    /// activation carried. Empty when <see cref="Main"/> did not run (a test host).
    /// </summary>
    public static IReadOnlyList<string> LaunchTokens { get; private set; } = [];

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        LaunchTokens = args;
        if (args.Contains(UnregisterNotificationsSwitch, StringComparer.Ordinal))
        {
            return UnregisterNotifications(args);
        }

        if (ToastActions.IsActivationLaunch(args))
        {
            DeliverToastPress();
            return 0;
        }

        if (RelaunchedFromTruePath(args))
        {
            return 0;
        }

        if (RedirectedToRunningInstance(args))
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

    /// <summary>
    /// The toast press trampoline (E7-S4): read the press and hand it to an ordinary launch of Tunqio.exe. A press that cannot
    /// be read starts nothing; with no data root to trust, it is written to the debugger only.
    /// </summary>
    private static void DeliverToastPress()
    {
        IReadOnlyList<string> tokens;
        string? dataRoot;
        try
        {
            // No handler: this process receives the one press it was started for and no other.
            ToastActivation.Register(receivePresses: false);
            AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            if (activation.Kind != ExtendedActivationKind.AppNotification || activation.Data is not AppNotificationActivatedEventArgs press)
            {
                Debug.WriteLine($"Toast press: the activation was {activation.Kind}, not a press; nothing started");
                return;
            }

            tokens = ToastActivation.TokensOf(press);
            dataRoot = ToastActivation.DataRootOf(press);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Debug.WriteLine($"Toast press could not be read; nothing started: {e}");
            return;
        }

        Log.Logger = AppLogging.Create(dataRoot is null ? new AppPaths() : new AppPaths(dataRoot), Guid.NewGuid());
        try
        {
            if (tokens.Count == 0)
            {
                Log.Warning("Toasts: a press carried no command this build knows; nothing started");
                return;
            }

            string exe = ExecutablePath.WithTrueCase(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Tunqio.Core.Identity.ExecutableName + ".exe"));
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            if (dataRoot is not null)
            {
                start.ArgumentList.Add(DataRootSwitch.Name);
                start.ArgumentList.Add(dataRoot);
            }

            foreach (string token in tokens)
            {
                start.ArgumentList.Add(token);
            }

            using Process? launched = Process.Start(start);
            bool allowed = launched is not null && NativeWindowing.AllowFor((uint)launched.Id);
            Log.Information(
                "Toasts: a press started this process; handed {Tokens} to {Exe} (pid {Pid}, foreground passed on {Allowed}); this process exits",
                string.Join(' ', tokens), exe, launched?.Id, allowed);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error(e, "Toasts: a press could not be handed on");
        }
        finally
        {
            Log.CloseAndFlush();
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

    /// <summary>
    /// T-192: true when this unpackaged process was started from a spelling of Tunqio.exe other than the file system's own
    /// (a lowercase path, a shortcut, a script) and has started Tunqio.exe again from the true spelling with the same
    /// arguments, so it must exit. AppInstance scopes instance keys by a hash of the exact module path, so without this the
    /// process would miss the running instance and open a second one on the same data root; <see cref="ExecutablePath"/>
    /// has the measurement. Anything that goes wrong here carries on in this process, as before.
    /// </summary>
    private static bool RelaunchedFromTruePath(string[] args)
    {
        if (!InstanceKey.Applies(args))
        {
            return false;
        }

        bool relaunched = Environment.GetEnvironmentVariable(ExecutablePath.RelaunchedVariable) is not null;
        if (relaunched)
        {
            // Not passed on to anything this instance starts later (a toast press's own relaunch, for one).
            Environment.SetEnvironmentVariable(ExecutablePath.RelaunchedVariable, null);
            return false;
        }

        string? processPath = Environment.ProcessPath;
        string? target;
        try
        {
            target = processPath is null
                ? null
                : ExecutablePath.RelaunchTarget(processPath, ExecutablePath.WithTrueCase(processPath), ToastActivation.IsPackaged, relaunched);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Debug.WriteLine($"Single instance: the executable's true path could not be read, carrying on: {e}");
            return false;
        }

        if (target is null)
        {
            return false;
        }

        string? dataRoot = DataRootSwitch.Path(args);
        Log.Logger = AppLogging.Create(dataRoot is null ? new AppPaths() : new AppPaths(dataRoot), Guid.NewGuid());
        try
        {
            var start = new ProcessStartInfo(target) { UseShellExecute = false };
            start.Environment[ExecutablePath.RelaunchedVariable] = "1";
            foreach (string arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using Process? launched = Process.Start(start);
            if (launched is null)
            {
                Log.Warning("Single instance: started as {ProcessPath}; relaunching from {TruePath} started nothing, so this process carries on", processPath, target);
                return false;
            }

            bool allowed = NativeWindowing.AllowFor((uint)launched.Id);
            Log.Information(
                "Single instance: started as {ProcessPath}, not the file system's spelling {TruePath} that AppInstance keys instances by; relaunched as pid {Pid} (foreground passed on {Allowed}); this process exits",
                processPath, target, launched.Id, allowed);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error(e, "Single instance: started as {ProcessPath}; relaunching from {TruePath} failed, so this process carries on", processPath, target);
            return false;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>True when this process handed its activation to the running instance and must exit.</summary>
    private static bool RedirectedToRunningInstance(string[] args)
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
            key = InstanceKey.For(dataRoot);
            activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            LaunchTokens = OwnTokens(activation, args);
            keyed = AppInstance.FindOrRegisterForKey(key);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Debug.WriteLine($"Single instance unavailable, starting unshared: {e}");
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
            Inbox.Post(tokens);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Warning(e, "Single instance: a redirected activation could not be read and was ignored");
        }
    }

    /// <summary>This process's own input: the files or URI of a packaged activation, else the command line.</summary>
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
        _ => null,
    };
}
