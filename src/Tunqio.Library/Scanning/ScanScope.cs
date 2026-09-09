using Microsoft.Extensions.Logging;

namespace Tunqio.Library.Scanning;

/// <summary>
/// What part of a folder a scan walks: a directory, with or without its subdirectories. A full scan is one
/// recursive scope at the root; a targeted scan (<see cref="Tunqio.Core.Library.ScanRequest.Paths"/>) is the
/// scopes <see cref="Resolve"/> derives from the paths it was given. Missing marking is confined to the scopes.
/// </summary>
/// <param name="Directory">Full path, no trailing separator (except a drive root).</param>
/// <param name="Recursive">Walk the subdirectories too.</param>
internal sealed record ScanScope(string Directory, bool Recursive)
{
    /// <summary>
    /// Turns the paths of a targeted request into scopes. A directory on disk is a recursive scope; a file on
    /// disk is a flat scope on its directory; a path that is gone is a recursive scope on itself when the
    /// snapshot has rows under it (it was a directory), otherwise a flat scope on its directory. Paths outside
    /// the folder are dropped, nested scopes are folded into the recursive scope that covers them, and the
    /// result is ordered by path so the walk visits directories in one pass.
    /// </summary>
    public static IReadOnlyList<ScanScope> Resolve(string folderPath, IReadOnlyList<string> paths, IEnumerable<string> snapshotPaths, ILogger logger)
    {
        string root = TrimSeparator(Path.GetFullPath(folderPath));
        string rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var scopes = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        List<string>? known = null;
        foreach (string raw in paths)
        {
            string path = TrimSeparator(Path.GetFullPath(raw));
            if (!string.Equals(path, root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug("Targeted path {Path} is outside folder {Folder}; ignored", raw, folderPath);
                continue;
            }

            if (System.IO.Directory.Exists(path))
            {
                Add(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                Add(Path.GetDirectoryName(path) ?? root, recursive: false);
            }
            else
            {
                known ??= snapshotPaths.ToList();
                string prefix = path + Path.DirectorySeparatorChar;
                bool wasDirectory = string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
                    || known.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                if (wasDirectory)
                {
                    Add(path, recursive: true);
                }
                else
                {
                    Add(Path.GetDirectoryName(path) ?? root, recursive: false);
                }
            }
        }

        // Fold: a scope inside a recursive scope adds nothing.
        List<ScanScope> ordered = scopes
            .Select(kv => new ScanScope(kv.Key, kv.Value))
            .OrderBy(s => s.Directory, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var result = new List<ScanScope>(ordered.Count);
        foreach (ScanScope scope in ordered)
        {
            if (result.Count > 0 && result[^1].Recursive && IsUnder(scope.Directory, result[^1].Directory))
            {
                continue;
            }

            result.Add(scope);
        }

        return result;

        void Add(string directory, bool recursive)
        {
            scopes[directory] = recursive || (scopes.TryGetValue(directory, out bool existing) && existing);
        }
    }

    /// <summary>True when <paramref name="path"/> lies in one of the scopes (directly, for a flat scope).</summary>
    public static bool Covers(IReadOnlyList<ScanScope> scopes, string path)
    {
        foreach (ScanScope scope in scopes)
        {
            if (scope.Recursive)
            {
                if (IsUnder(path, scope.Directory))
                {
                    return true;
                }
            }
            else if (string.Equals(TrimSeparator(Path.GetDirectoryName(path) ?? string.Empty), scope.Directory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary><paramref name="path"/> is <paramref name="directory"/> or inside it.</summary>
    private static bool IsUnder(string path, string directory)
    {
        if (string.Equals(path, directory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Drops trailing separators except on a drive root ("C:\").</summary>
    private static string TrimSeparator(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? path : trimmed;
    }
}
