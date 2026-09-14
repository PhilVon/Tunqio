using System.Text;

namespace Tunqio.App.Activation;

/// <summary>
/// A redirected launch arrives as one string (<c>ILaunchActivatedEventArgs.Arguments</c>), not as an argument array, so
/// the main instance splits it the way the C runtime split the second process's own <c>argv</c>: whitespace separates,
/// double quotes group, <c>2n</c> backslashes before a quote are <c>n</c> backslashes and a delimiter, <c>2n+1</c> are
/// <c>n</c> backslashes and a literal quote, and <c>""</c> inside quotes is a literal quote.
/// </summary>
public static class CommandLineText
{
    /// <summary>The arguments in <paramref name="commandLine"/>; empty for null or blank.</summary>
    public static IReadOnlyList<string> Split(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(commandLine))
        {
            return result;
        }

        var current = new StringBuilder();
        bool inQuotes = false;
        bool hasToken = false;
        int i = 0;
        while (i < commandLine.Length)
        {
            char c = commandLine[i];
            if (c == '\\')
            {
                int count = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    count++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                }
                else
                {
                    current.Append('\\', count);
                }

                hasToken = true;
                continue;
            }

            if (c == '"')
            {
                if (inQuotes && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
                {
                    current.Append('"');
                    i += 2;
                }
                else
                {
                    inQuotes = !inQuotes;
                    i++;
                }

                hasToken = true;
                continue;
            }

            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }

                i++;
                continue;
            }

            current.Append(c);
            hasToken = true;
            i++;
        }

        if (hasToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>
    /// Drops a leading executable. An unpackaged launch's activation arguments carry the whole command line, the program
    /// included; a packaged one's carry only the arguments. Nothing Tunqio accepts as an argument ends in <c>.exe</c>.
    /// </summary>
    public static IReadOnlyList<string> WithoutExecutable(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Count > 0 && arguments[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? [.. arguments.Skip(1)]
            : arguments;
    }
}
