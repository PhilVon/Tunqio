namespace Tunqio.SoakRunner;

/// <summary>The repository root (the directory holding Tunqio.sln), so the fixture library has a default.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = Locate();

    private static string Locate()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Tunqio.sln")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return dir ?? Environment.CurrentDirectory;
    }
}
