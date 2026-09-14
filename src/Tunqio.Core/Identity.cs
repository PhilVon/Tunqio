namespace Tunqio.Core;

/// <summary>
/// Every string that names the product, mirrored from <c>docs/identity.md</c> (the source of truth).
/// Values marked "frozen at 1.0" cannot change after the first public release without breaking
/// in-place upgrade, pinned shortcuts or user bookmarks. <c>Tunqio.Core.Tests</c> checks that the
/// MSIX manifest and the App project agree with these constants.
/// </summary>
public static class Identity
{
    /// <summary>User-facing display name. Written "Tunqio" in prose and UI, never all-caps.</summary>
    public const string ProductName = "Tunqio";

    /// <summary>Short description used by the manifest and the About page.</summary>
    public const string ShortDescription = "Tunqio music player";

    /// <summary>MSIX package identity name. Frozen at 1.0.</summary>
    public const string PackageName = "Tunqio";

    /// <summary>
    /// Manifest publisher and the subject of the self-signed release certificate. Development and release builds share it:
    /// releases stay self-signed (D-34), so it is never replaced. Frozen at 1.0.
    /// </summary>
    public const string Publisher = "CN=Tunqio";

    /// <summary>Manifest Application Id; half of the Application User Model ID. Frozen at 1.0.</summary>
    public const string ApplicationId = "Tunqio";

    /// <summary>Assembly name of the shell executable.</summary>
    public const string ExecutableName = "Tunqio";

    /// <summary>App execution alias (<c>uap5:AppExecutionAlias</c>). Frozen at 1.0.</summary>
    public const string ExecutionAlias = "tunqio.exe";

    /// <summary>URI scheme for <c>tunqio://</c> commands. Frozen at 1.0.</summary>
    public const string UriScheme = "tunqio";

    /// <summary>Manifest file type association group name. Frozen at 1.0.</summary>
    public const string FileTypeAssociationGroup = "tunqio-audio";

    /// <summary>Folder under <c>%LocalAppData%</c> holding the library, art, logs and exports. Frozen at 1.0.</summary>
    public const string DataFolderName = "Tunqio";

    /// <summary>Log file name prefix; files are <c>tunqio-yyyyMMdd.log</c> under the logs folder.</summary>
    public const string LogFilePrefix = "tunqio-";

    /// <summary>Windows truncates notify-icon tooltips beyond this many characters.</summary>
    public const int TrayTooltipMaxLength = 127;

    private const string TrackSeparator = " – "; // en dash between title and artist
    private const string ProductSuffix = " — " + ProductName; // em dash before the product name
    private const string Ellipsis = "…";

    /// <summary>
    /// Main window title: <c>Tunqio</c> when idle, <c>Title – Artist — Tunqio</c> with a track loaded.
    /// </summary>
    public static string WindowTitle(string? title, string? artist)
    {
        string track = TrackText(title, artist);
        return track.Length == 0 ? ProductName : track + ProductSuffix;
    }

    /// <summary>
    /// Tray tooltip: <c>Tunqio</c> when idle, <c>Title – Artist</c> with a track loaded, trimmed to
    /// <see cref="TrayTooltipMaxLength"/> by shortening the title first and keeping the artist.
    /// </summary>
    public static string TrayTooltip(string? title, string? artist)
    {
        string track = TrackText(title, artist);
        if (track.Length == 0)
        {
            return ProductName;
        }

        if (track.Length <= TrayTooltipMaxLength)
        {
            return track;
        }

        string safeTitle = title?.Trim() ?? string.Empty;
        string safeArtist = artist?.Trim() ?? string.Empty;
        if (safeTitle.Length > 0 && safeArtist.Length > 0)
        {
            int budget = TrayTooltipMaxLength - TrackSeparator.Length - safeArtist.Length - Ellipsis.Length;
            if (budget >= 8)
            {
                return safeTitle[..budget] + Ellipsis + TrackSeparator + safeArtist;
            }
        }

        return track[..(TrayTooltipMaxLength - Ellipsis.Length)] + Ellipsis;
    }

    private static string TrackText(string? title, string? artist)
    {
        string safeTitle = title?.Trim() ?? string.Empty;
        string safeArtist = artist?.Trim() ?? string.Empty;
        if (safeTitle.Length == 0)
        {
            return safeArtist;
        }

        return safeArtist.Length == 0 ? safeTitle : safeTitle + TrackSeparator + safeArtist;
    }
}
