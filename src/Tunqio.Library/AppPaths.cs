using Tunqio.Core;

namespace Tunqio.Library;

/// <summary>
/// The literal <c>%LocalAppData%\Tunqio</c> layout from docs/library-and-data.md. Tests pass an explicit root.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    public AppPaths()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), Identity.DataFolderName))
    {
    }

    public AppPaths(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        DataRoot = Path.GetFullPath(dataRoot);
    }

    public string DataRoot { get; }

    public string DatabasePath => Path.Combine(DataRoot, "library.db");

    public string SettingsPath => Path.Combine(DataRoot, "settings.json");

    public string LogsDirectory => Path.Combine(DataRoot, "logs");

    public string ArtDirectory => Path.Combine(DataRoot, "art");

    public string PresetsDirectory => Path.Combine(DataRoot, "presets");

    public string ExportsDirectory => Path.Combine(DataRoot, "exports", "playlists");

    /// <summary>Log file name for a given day: <c>tunqio-yyyyMMdd.log</c> (docs/identity.md).</summary>
    public static string LogFileName(DateOnly day) => $"{Identity.LogFilePrefix}{day:yyyyMMdd}.log";

    /// <summary>The Serilog rolling-file path template producing <see cref="LogFileName"/> per day.</summary>
    public string LogFileTemplate => Path.Combine(LogsDirectory, $"{Identity.LogFilePrefix}.log");

    public void EnsureCreated()
    {
        foreach (string dir in new[] { DataRoot, LogsDirectory, ArtDirectory, PresetsDirectory, ExportsDirectory })
        {
            Directory.CreateDirectory(dir);
        }
    }
}
