using System.Collections.Frozen;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.Core;
using Tunqio.Core.Library;

namespace Tunqio.App.Activation;

/// <summary>What one piece of activation input asks for.</summary>
public enum RoutedCommandKind
{
    /// <summary>One audio file from Explorer or the command line: play it now, inserted at the current position (flow 2).</summary>
    PlayFile,

    /// <summary>Replace the queue with these files and folders and play (<c>tunqio://play</c>, several files, a folder).</summary>
    PlayPaths,

    /// <summary>Append these files and folders to the queue (<c>tunqio://queue</c>).</summary>
    QueuePaths,

    /// <summary>Play / pause.</summary>
    TogglePlayPause,

    /// <summary>Next track.</summary>
    Next,

    /// <summary>Previous track (or restart, per the session's rule).</summary>
    Previous,

    /// <summary>Bring the main window to the foreground.</summary>
    Show,
}

/// <summary>One command the router will run, with the full paths it concerns.</summary>
public sealed record RoutedCommand(RoutedCommandKind Kind, IReadOnlyList<string> Paths)
{
    /// <summary>A command with no paths.</summary>
    public static RoutedCommand Of(RoutedCommandKind kind) => new(kind, []);

    /// <summary>Play and show bring the window forward; queue and the transport commands leave it where it is.</summary>
    public bool BringsWindowForward => Kind is RoutedCommandKind.PlayFile or RoutedCommandKind.PlayPaths or RoutedCommandKind.Show;

    public override string ToString() => Paths.Count == 0
        ? Kind.ToString()
        : string.Create(CultureInfo.InvariantCulture, $"{Kind} ({Paths.Count} path(s), first {Paths[0]})");
}

/// <summary>What the router made of the input: the commands it ran and one reason per item it refused.</summary>
public sealed record RouteResult(IReadOnlyList<RoutedCommand> Commands, IReadOnlyList<string> Refusals);

/// <summary>
/// E7-S1 (AC-473): turns activation input into session commands. The input is always a list of strings, whichever way it
/// came: a launch's command line, the files Explorer activated with, or a <c>tunqio://</c> URI (docs/identity.md,
/// "URI scheme"). The first launch's own arguments come through here too, so a second instance's redirected activation
/// and a cold start from Explorer are one code path.
/// </summary>
/// <remarks>
/// <para>
/// Rules. A <c>--switch</c> the app knows is skipped, with its value where it takes one (<c>--data-root PATH</c>). A
/// <c>tunqio:</c> token is a URI command. Anything else is a path. The paths in one activation become one command: a single
/// file plays at the current position with the queue kept (flow 2), and a folder or several files replace the queue, so
/// fifty files selected in Explorer are one fifty-item queue rather than fifty replacements. The path command runs before
/// any URI commands in the same activation.
/// </para>
/// <para>
/// Refusals. Malformed or unknown input - an unknown command or switch, a missing or relative path, a file that is not
/// there or is not audio - is refused with one warning line each and never throws; neither does a command the session
/// cannot carry out. A relative path is accepted only from this process's own command line: a redirected activation has
/// no working directory to resolve it against, and a URI never does. Query values are percent-decoded; <c>+</c> is left
/// alone, because it is a legal character in a Windows file name.
/// </para>
/// </remarks>
public sealed class CommandRouter
{
    /// <summary>The URI prefix, <c>tunqio:</c>.</summary>
    public static readonly string SchemePrefix = Identity.UriScheme + ":";

    private static readonly FrozenDictionary<string, RoutedCommandKind> UriCommands = new Dictionary<string, RoutedCommandKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["play"] = RoutedCommandKind.PlayPaths,
        ["queue"] = RoutedCommandKind.QueuePaths,
        ["toggle"] = RoutedCommandKind.TogglePlayPause,
        ["next"] = RoutedCommandKind.Next,
        ["previous"] = RoutedCommandKind.Previous,
        ["show"] = RoutedCommandKind.Show,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The app's own switches that take a value (the value is skipped with them).</summary>
    private static readonly FrozenSet<string> ValueSwitches = new[]
    {
        "--data-root", "--export-diagnostics", "--library-spike", "--seconds", "--step", "--out", "--resizes",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The app's own switches that take no value.</summary>
    private static readonly FrozenSet<string> FlagSwitches = new[]
    {
        "--redact-paths", "--render-spike", "--nowplaying-spike", "--shell-spike", "--warp",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ICommandTarget _target;
    private readonly ILogger _log;

    public CommandRouter(ICommandTarget target, ILogger<CommandRouter>? log)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
        _log = log ?? NullLogger<CommandRouter>.Instance;
    }

    /// <summary>
    /// Reads the input without acting on it.
    /// </summary>
    /// <param name="tokens">The arguments, file paths or URIs of one activation.</param>
    /// <param name="workingDirectory">Resolves relative paths; null refuses them (a redirected activation).</param>
    /// <param name="emptyMeansShow">True for a redirected activation: a second launch with nothing to say brings the window forward.</param>
    public static RouteResult Parse(IReadOnlyList<string> tokens, string? workingDirectory, bool emptyMeansShow)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var commands = new List<RoutedCommand>();
        var refusals = new List<string>();
        var paths = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (token.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
            {
                ParseUri(token, commands, refusals);
            }
            else if (token.StartsWith("--", StringComparison.Ordinal))
            {
                if (ValueSwitches.Contains(token))
                {
                    if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        i++;
                    }
                }
                else if (!FlagSwitches.Contains(token))
                {
                    refusals.Add($"unknown switch '{token}'");
                }
            }
            else if (TryResolvePath(token, workingDirectory, out string full, out string refusal))
            {
                paths.Add(full);
            }
            else
            {
                refusals.Add(refusal);
            }
        }

        if (paths.Count > 0)
        {
            commands.Insert(0, new RoutedCommand(
                paths.Count == 1 && File.Exists(paths[0]) ? RoutedCommandKind.PlayFile : RoutedCommandKind.PlayPaths,
                paths));
        }

        if (commands.Count == 0 && refusals.Count == 0 && emptyMeansShow)
        {
            commands.Add(RoutedCommand.Of(RoutedCommandKind.Show));
        }

        return new RouteResult(commands, refusals);
    }

    /// <summary>
    /// Reads the input and runs what it asks for, in order. Never throws: every refusal and every failed command is one
    /// warning line, and the next command still runs.
    /// </summary>
    public async Task<RouteResult> RouteAsync(
        IReadOnlyList<string>? tokens, string? workingDirectory, bool emptyMeansShow, CancellationToken ct = default)
    {
        RouteResult result;
        try
        {
            result = Parse(tokens ?? [], workingDirectory, emptyMeansShow);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log.LogWarning("Activation input refused: it could not be read ({Message})", e.Message);
            return new RouteResult([], [e.Message]);
        }

        foreach (string refusal in result.Refusals)
        {
            _log.LogWarning("Activation input refused: {Reason}", refusal);
        }

        foreach (RoutedCommand command in result.Commands)
        {
            _log.LogInformation("Activation: {Command}", command);
            try
            {
                if (command.BringsWindowForward)
                {
                    _target.BringToForeground();
                }

                await RunAsync(command, ct);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _log.LogWarning("Activation command {Command} refused: {Message}", command, e.Message);
            }
        }

        return result;
    }

    private Task RunAsync(RoutedCommand command, CancellationToken ct) => command.Kind switch
    {
        RoutedCommandKind.PlayFile => _target.PlayFileNowAsync(command.Paths[0], ct),
        RoutedCommandKind.PlayPaths => _target.PlayPathsAsync(command.Paths, ct),
        RoutedCommandKind.QueuePaths => _target.QueuePathsAsync(command.Paths, ct),
        RoutedCommandKind.TogglePlayPause => _target.TogglePlayPauseAsync(ct),
        RoutedCommandKind.Next => _target.NextAsync(ct),
        RoutedCommandKind.Previous => _target.PreviousAsync(ct),
        _ => Task.CompletedTask, // Show: bringing the window forward was the whole command.
    };

    private static void ParseUri(string token, List<RoutedCommand> commands, List<string> refusals)
    {
        string rest = token[SchemePrefix.Length..];
        int fragment = rest.IndexOf('#', StringComparison.Ordinal);
        if (fragment >= 0)
        {
            rest = rest[..fragment];
        }

        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            rest = rest[2..];
        }

        int query = rest.IndexOf('?', StringComparison.Ordinal);
        // A browser may hand over tunqio://play/?path=..., so a trailing slash on the command is not part of it.
        string name = (query >= 0 ? rest[..query] : rest).TrimEnd('/');
        if (!UriCommands.TryGetValue(name, out RoutedCommandKind kind))
        {
            refusals.Add($"unknown {Identity.UriScheme}:// command in '{token}'");
            return;
        }

        if (kind is not (RoutedCommandKind.PlayPaths or RoutedCommandKind.QueuePaths))
        {
            commands.Add(RoutedCommand.Of(kind));
            return;
        }

        var paths = new List<string>();
        bool named = false;
        string pairs = query >= 0 ? rest[(query + 1)..] : string.Empty;
        foreach (string pair in pairs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string key = equals >= 0 ? pair[..equals] : pair;
            if (!string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            named = true;
            string value = equals >= 0 ? Uri.UnescapeDataString(pair[(equals + 1)..]) : string.Empty;
            if (value.Length == 0)
            {
                refusals.Add($"'{token}' has an empty path");
            }
            else if (TryResolvePath(value, workingDirectory: null, out string full, out string refusal))
            {
                paths.Add(full);
            }
            else
            {
                refusals.Add(refusal);
            }
        }

        if (!named)
        {
            refusals.Add($"'{token}' names no path ({Identity.UriScheme}://{name}?path=<url-encoded path>)");
        }
        else if (paths.Count > 0)
        {
            commands.Add(new RoutedCommand(kind, paths));
        }
    }

    private static bool TryResolvePath(string token, string? workingDirectory, out string full, out string refusal)
    {
        full = string.Empty;
        refusal = string.Empty;
        try
        {
            if (Path.IsPathFullyQualified(token))
            {
                full = Path.GetFullPath(token);
            }
            else if (workingDirectory is not null)
            {
                full = Path.GetFullPath(token, workingDirectory);
            }
            else
            {
                refusal = $"'{token}' is not a full path, and this activation has no working directory to resolve it against";
                return false;
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            refusal = $"'{token}' is not a valid path ({e.Message})";
            return false;
        }

        if (Directory.Exists(full))
        {
            return true;
        }

        if (!File.Exists(full))
        {
            refusal = $"'{full}' does not exist";
            return false;
        }

        if (!AudioFormats.IsSupported(full))
        {
            refusal = $"'{full}' is not an audio format Tunqio plays";
            return false;
        }

        return true;
    }
}
