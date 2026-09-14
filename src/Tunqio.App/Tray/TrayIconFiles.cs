using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Tunqio.App.Tray;

/// <summary>
/// The one place the tray icon's file is chosen (E7-S3). docs/identity.md names the files: <c>Assets/Tray/tunqio-16.ico</c> and
/// <c>Assets/Tray/tunqio-32.ico</c>, in light and dark variants, beside the executable. The icon task drops them there; nothing
/// else in the code names a path.
/// </summary>
/// <remarks>
/// <para>
/// A variant is named for the taskbar it is drawn on: <c>tunqio-32-light.ico</c> is the icon for a light taskbar (so, dark
/// artwork), <c>tunqio-32-dark.ico</c> for a dark one. The size is the one the notification area draws at this DPI: 16 px at
/// 100 % scaling, and 32 px above it, where Windows scales the 32 px file down more cleanly than it scales 16 px up. The
/// candidates are tried in <see cref="Candidates"/>'s order, and the first file that exists is used.
/// </para>
/// <para>
/// While none of them exists yet, the tray shows the executable's own icon, and the log says so. The variant is chosen once, at
/// launch: a taskbar theme changed while Tunqio runs is picked up at the next launch.
/// </para>
/// </remarks>
public static class TrayIconFiles
{
    /// <summary>The folder under the executable's directory that holds the tray icons.</summary>
    public const string Folder = "Assets/Tray";

    /// <summary>
    /// The files to try, relative to the executable's directory, best first: the size for <paramref name="smallIconPixels"/>
    /// in the taskbar's variant, then that size unqualified, then the other size the same two ways.
    /// </summary>
    /// <param name="smallIconPixels">The system small-icon size (SM_CXSMICON): 16 at 100 % scaling, 20, 24, 32 above.</param>
    /// <param name="lightTaskbar">True when the taskbar uses the light theme.</param>
    public static IReadOnlyList<string> Candidates(int smallIconPixels, bool lightTaskbar)
    {
        string variant = lightTaskbar ? "light" : "dark";
        int[] sizes = smallIconPixels > 16 ? [32, 16] : [16, 32];
        var candidates = new List<string>(4);
        foreach (int size in sizes)
        {
            candidates.Add($"{Folder}/tunqio-{size}-{variant}.ico");
            candidates.Add($"{Folder}/tunqio-{size}.ico");
        }

        return candidates;
    }

    /// <summary>
    /// The icon for this machine's taskbar: the first candidate file under <paramref name="baseDirectory"/> that exists, else the
    /// icon of <paramref name="fallbackExecutable"/>, with a log line either way.
    /// </summary>
    public static System.Drawing.Icon Load(string baseDirectory, string fallbackExecutable, ILogger log)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);
        ArgumentException.ThrowIfNullOrEmpty(fallbackExecutable);
        ArgumentNullException.ThrowIfNull(log);
        int pixels = SmallIconPixels();
        bool light = TaskbarUsesLightTheme();
        foreach (string candidate in Candidates(pixels, light))
        {
            string path = Path.Combine(baseDirectory, candidate.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path))
            {
                log.LogInformation(
                    "Tray icon file {Path} ({Pixels} px small icons, {Theme} taskbar)", candidate, pixels, light ? "light" : "dark");
                return new System.Drawing.Icon(path);
            }
        }

        log.LogWarning(
            "Tray icon: none of {Candidates} exists yet; using the executable's own icon as the fallback",
            string.Join(", ", Candidates(pixels, light)));
        return System.Drawing.Icon.ExtractAssociatedIcon(fallbackExecutable)
            ?? throw new InvalidOperationException($"'{fallbackExecutable}' has no icon to fall back to");
    }

    /// <summary>The system small-icon width, 16 when it cannot be read.</summary>
    private static int SmallIconPixels()
    {
        int pixels = GetSystemMetrics(SmCxSmIcon);
        return pixels > 0 ? pixels : 16;
    }

    /// <summary>
    /// Whether the taskbar is light, read (never written) from the user's personalisation settings; dark when it cannot be
    /// read, which is Windows' default taskbar.
    /// </summary>
    private static bool TaskbarUsesLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private const int SmCxSmIcon = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
