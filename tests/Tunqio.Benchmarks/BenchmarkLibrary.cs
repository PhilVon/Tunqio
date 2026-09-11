using Tunqio.FixtureGen;

namespace Tunqio.Benchmarks;

/// <summary>
/// The 100k database every benchmark here measures against: <c>tests/fixtures/library-100k.db</c> when it
/// exists (tests/fixtures/README.md), else the same database generated into a temporary folder. Generating it
/// takes a while, which is why the committed fixture is preferred and why the temporary copy is shared by
/// whichever benchmarks want a read-only view of it.
/// </summary>
internal static class BenchmarkLibrary
{
    /// <summary>The committed fixture's path, whether or not it exists.</summary>
    public static string FixturePath()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Tunqio.sln")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return Path.Combine(dir ?? ".", "tests", "fixtures", "library-100k.db");
    }

    /// <summary>
    /// A 100k database to open. Returns its path and, when one had to be generated, the temporary directory the
    /// caller must delete in its cleanup (null when the committed fixture was used).
    /// </summary>
    public static (string Path, string? Temporary) Acquire(TextWriter log)
    {
        string path = FixturePath();
        if (File.Exists(path))
        {
            return (path, null);
        }

        string temporary = Path.Combine(Path.GetTempPath(), "tunqio-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        path = Path.Combine(temporary, "library-100k.db");
        Library100kBuilder.Build(path, 100_000, FixtureLibraryBuilder.Seed, log);
        return (path, temporary);
    }

    /// <summary>
    /// A private, writable copy of the 100k database, for a benchmark whose work changes it. Always a temporary
    /// directory: the committed fixture is shared by the whole suite and must not be written to.
    /// </summary>
    public static (string Path, string Temporary) AcquireWritable(TextWriter log)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "tunqio-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        string path = Path.Combine(temporary, "library-100k.db");

        string source = FixturePath();
        if (File.Exists(source))
        {
            File.Copy(source, path);
        }
        else
        {
            Library100kBuilder.Build(path, 100_000, FixtureLibraryBuilder.Seed, log);
        }

        return (path, temporary);
    }

    public static void Delete(string? temporary)
    {
        if (temporary is not null && Directory.Exists(temporary))
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
}
