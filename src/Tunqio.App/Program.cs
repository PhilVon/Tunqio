using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Serilog;
using Tunqio.App.Activation;
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
/// Nothing here may stop Tunqio from launching: a key that cannot be registered (a Windows App SDK failure) is written to
/// the debugger and the app starts as a plain, unshared instance. Measurement modes skip single instance altogether
/// (<see cref="InstanceKey.Applies"/>).
/// </remarks>
public static class Program
{
    /// <summary>The longest a second instance waits for the running one to accept its activation.</summary>
    private static readonly TimeSpan RedirectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Redirected activations, held until the window attaches a handler.</summary>
    public static ActivationInbox Inbox { get; } = new();

    /// <summary>
    /// This launch's own activation input for <see cref="CommandRouter"/>: the command line, or the files or URI a
    /// packaged activation carried. Empty when <see cref="Main"/> did not run (a test host).
    /// </summary>
    public static IReadOnlyList<string> LaunchTokens { get; private set; } = [];

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        LaunchTokens = args;
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
