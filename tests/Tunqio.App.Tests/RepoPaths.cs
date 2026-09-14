namespace Tunqio.App.Tests;

/// <summary>Finds the repository root (the directory holding Tunqio.sln) from the test binary's location.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = Locate();

    public static string File(params string[] segments) => Path.Combine([Root, .. segments]);

    private static string Locate()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (System.IO.File.Exists(Path.Combine(dir, "Tunqio.sln")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new InvalidOperationException("Tunqio.sln not found above " + AppContext.BaseDirectory);
    }
}
