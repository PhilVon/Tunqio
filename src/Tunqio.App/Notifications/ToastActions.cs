using Tunqio.App.Activation;

namespace Tunqio.App.Notifications;

/// <summary>
/// The toast's activation arguments (E7-S4): what each button carries, and what a press turns into for
/// <see cref="CommandRouter"/>. The values are the <c>tunqio://</c> command names, so a toast press is routed exactly like
/// <c>tunqio://next</c> from the command line (E7-S1): Previous, Play/Pause and Next drive the session and leave the window
/// where it is, and only the toast's body brings the window forward.
/// </summary>
public static class ToastActions
{
    /// <summary>The argument naming the command.</summary>
    public const string ActionArgument = "action";

    /// <summary>
    /// The argument naming the data root, carried only when Tunqio runs on a <c>--data-root</c> (a harness's scratch profile), so
    /// a toast press that starts a second process finds that instance rather than the default profile's.
    /// </summary>
    public const string DataRootArgument = "dataRoot";

    /// <summary>The command line Windows App SDK gives a process it starts to receive a toast press.</summary>
    public const string ActivatedSwitch = "----AppNotificationActivated:";

    /// <summary>The value <paramref name="action"/> is carried as: the <c>tunqio://</c> command of the same meaning.</summary>
    public static string ValueOf(ToastAction action) => action switch
    {
        ToastAction.Previous => "previous",
        ToastAction.TogglePlayPause => "toggle",
        ToastAction.Next => "next",
        _ => "show",
    };

    /// <summary>
    /// The router's input for a press with <paramref name="arguments"/>: one <c>tunqio://</c> command. A press with no action
    /// is the toast's body, which shows the window; an action this build does not know is nothing, never a guess.
    /// </summary>
    public static IReadOnlyList<string> TokensFor(IReadOnlyDictionary<string, string>? arguments)
    {
        if (arguments is null || !arguments.TryGetValue(ActionArgument, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return [CommandRouter.SchemePrefix + "//" + ValueOf(ToastAction.Show)];
        }

        foreach (ToastAction action in Enum.GetValues<ToastAction>())
        {
            if (string.Equals(ValueOf(action), value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return [CommandRouter.SchemePrefix + "//" + ValueOf(action)];
            }
        }

        return [];
    }

    /// <summary>The data root a press names, or null.</summary>
    public static string? DataRootOf(IReadOnlyDictionary<string, string>? arguments) =>
        arguments is not null && arguments.TryGetValue(DataRootArgument, out string? root) && !string.IsNullOrWhiteSpace(root) ? root : null;

    /// <summary>True when Windows started this process to deliver a toast press.</summary>
    public static bool IsActivationLaunch(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return args.Any(arg => string.Equals(arg, ActivatedSwitch, StringComparison.OrdinalIgnoreCase));
    }
}
