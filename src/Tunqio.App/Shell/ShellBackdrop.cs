using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media;

namespace Tunqio.App.Shell;

/// <summary>
/// The window's system backdrop: Mica where Windows has it, desktop acrylic where it does not, and neither on a
/// machine that supports no backdrop at all (E2-S1). Mica is a Windows 11 material, and the app supports Windows 10
/// 2004 upwards (Q-4), so the fallback is not a corner case — it is what half the supported range gets.
/// </summary>
public static class ShellBackdrop
{
    /// <summary>What <see cref="Choose"/> picked, for the log and the diagnostics page.</summary>
    public enum Kind
    {
        None,
        Mica,
        Acrylic,
    }

    /// <summary>
    /// The best backdrop this machine supports, and its name. Returns null when neither material is available, in
    /// which case the window keeps its own opaque background and looks flat rather than looking broken.
    /// </summary>
    public static (SystemBackdrop? Backdrop, Kind Which) Choose()
    {
        if (MicaController.IsSupported())
        {
            return (new MicaBackdrop { Kind = MicaKind.Base }, Kind.Mica);
        }

        return DesktopAcrylicController.IsSupported()
            ? (new DesktopAcrylicBackdrop(), Kind.Acrylic)
            : (null, Kind.None);
    }
}
