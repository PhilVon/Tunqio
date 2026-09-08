namespace Tunqio.Core;

/// <summary>
/// Where Tunqio keeps its data. The layout is docs/library-and-data.md "Storage layout"; the root is the
/// literal <c>%LocalAppData%\Tunqio</c> (docs/identity.md; MSIX write virtualisation is disabled so the
/// path is the same packaged and unpackaged).
/// </summary>
public interface IAppPaths
{
    /// <summary><c>%LocalAppData%\Tunqio</c>.</summary>
    string DataRoot { get; }

    /// <summary><c>library.db</c> under the data root.</summary>
    string DatabasePath { get; }

    /// <summary><c>settings.json</c> under the data root (until E3 moves settings into the database).</summary>
    string SettingsPath { get; }

    /// <summary>Rolling Serilog files, <c>tunqio-yyyyMMdd.log</c>.</summary>
    string LogsDirectory { get; }

    /// <summary>Album art keyed by content hash.</summary>
    string ArtDirectory { get; }

    /// <summary>User visualization presets.</summary>
    string PresetsDirectory { get; }

    /// <summary>Playlist auto-exports.</summary>
    string ExportsDirectory { get; }

    /// <summary>Creates every directory above if missing. Idempotent.</summary>
    void EnsureCreated();
}
