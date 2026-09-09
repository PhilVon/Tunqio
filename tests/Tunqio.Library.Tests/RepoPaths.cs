namespace Tunqio.Library.Tests;

/// <summary>Repository root (the directory holding Tunqio.sln).</summary>
internal static class RepoPaths
{
    public static string Root { get; } = Locate();

    public static string File(params string[] segments) => Path.Combine([Root, .. segments]);

    private static string Locate()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !System.IO.File.Exists(Path.Combine(dir, "Tunqio.sln")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return dir ?? throw new InvalidOperationException("Tunqio.sln not found above " + AppContext.BaseDirectory);
    }
}
