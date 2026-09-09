namespace Tunqio.Interop.Tests;

/// <summary>Repository root (the directory holding Tunqio.sln) and native binaries next to the test assembly.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = Locate();

    public static string File(params string[] segments) => Path.Combine([Root, .. segments]);

    /// <summary>A native DLL copied next to the test assembly by the project's build target.</summary>
    public static string NativeLibrary(string fileName) => Path.Combine(AppContext.BaseDirectory, fileName);

    public static string RepoVersion()
    {
        string props = System.IO.File.ReadAllText(File("Directory.Build.props"));
        return System.Xml.Linq.XDocument.Parse(props).Descendants("TunqioVersion").Single().Value;
    }

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
