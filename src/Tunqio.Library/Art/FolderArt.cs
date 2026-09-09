namespace Tunqio.Library.Art;

/// <summary>
/// The folder-image fallback (docs/library-and-data.md, ExtractArt): the first of <c>cover.*</c>, <c>folder.*</c>,
/// <c>front.*</c> (any image extension) and then any <c>.jpg</c> in the audio file's directory. Names are
/// compared case-insensitively and ties broken by ordinal path so the choice is stable across scans.
/// </summary>
internal static class FolderArt
{
    private static readonly string[] Stems = ["cover", "folder", "front"];
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp"];

    public static string? Find(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        string? bestPath = null;
        int bestRank = int.MaxValue;
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            int rank = Rank(path);
            if (rank < bestRank || (rank == bestRank && bestPath is not null && string.CompareOrdinal(path, bestPath) < 0))
            {
                bestRank = rank;
                bestPath = path;
            }
        }

        return bestPath;
    }

    /// <summary>Lower is better; <see cref="int.MaxValue"/> is not an image we take.</summary>
    private static int Rank(string path)
    {
        string extension = Path.GetExtension(path);
        if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return int.MaxValue;
        }

        string stem = Path.GetFileNameWithoutExtension(path);
        int index = Array.FindIndex(Stems, s => string.Equals(s, stem, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            return index;
        }

        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? Stems.Length
            : int.MaxValue;
    }
}
