namespace Tunqio.Library;

/// <summary>Anchor type for the assembly (architecture tests and DI registration find the assembly through it).</summary>
public static class LibraryAssembly
{
    /// <summary>Schema version the repositories in this build expect.</summary>
    public static int SchemaVersion => Database.LibraryMigrations.Latest;
}
