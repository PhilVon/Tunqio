using Microsoft.UI.Windowing;

namespace Tunqio.App.Shell;

/// <summary>
/// The window icon (T-191): the title bar, the taskbar button and Alt+Tab show <c>Assets/Tunqio.ico</c>, the multi-size icon
/// tools/IconGen renders from assets/brand/tunqio-icon.svg. The file sits beside the executable in both build shapes (a
/// Content item), so the same call serves the unpackaged app and the package. Tunqio.exe carries the same icon as its
/// ApplicationIcon, which is what Explorer shows for the file itself.
/// </summary>
public static class AppIcon
{
    /// <summary>The icon file, relative to the executable's directory.</summary>
    public const string RelativePath = "Assets/Tunqio.ico";

    /// <summary>The icon file under <paramref name="baseDirectory"/>.</summary>
    public static string PathUnder(string baseDirectory) =>
        Path.Combine(baseDirectory, RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Sets <paramref name="window"/>'s icon from the file under <paramref name="baseDirectory"/>; false, with a warning in the
    /// log, when the file is missing or Windows refuses it, in which case the window keeps the executable's icon.
    /// </summary>
    public static bool Apply(AppWindow window, string baseDirectory, string windowName)
    {
        ArgumentNullException.ThrowIfNull(window);
        string path = PathUnder(baseDirectory);
        if (!File.Exists(path))
        {
            Serilog.Log.Warning("Window icon: {Path} is missing; the {Window} keeps the executable's icon", path, windowName);
            return false;
        }

        try
        {
            window.SetIcon(path);
            Serilog.Log.Information("Window icon: {Window} uses {Icon}", windowName, RelativePath);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Serilog.Log.Warning(e, "Window icon: {Path} was refused; the {Window} keeps the executable's icon", path, windowName);
            return false;
        }
    }
}
