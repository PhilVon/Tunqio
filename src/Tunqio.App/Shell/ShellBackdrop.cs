using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml.Media;

namespace Tunqio.App.Shell;

/// <summary>
/// Whether this machine offers each system backdrop material. The seam between the shell's backdrop decision and the
/// two Windows App SDK probes that answer it (E7-S6), so the decision is tested on every answer Windows can give,
/// including a probe that throws, without the test needing a Windows 10 or a Windows 11 machine.
/// </summary>
public interface IBackdropSupport
{
    /// <summary>True where Mica is available: Windows 11 (build 22000) and later.</summary>
    bool IsMicaSupported();

    /// <summary>True where desktop acrylic is available: Windows 10 and later with composition.</summary>
    bool IsDesktopAcrylicSupported();
}

/// <summary>The real probes: the Windows App SDK's own controllers, asked on the machine the app runs on.</summary>
public sealed class SystemBackdropSupport : IBackdropSupport
{
    /// <inheritdoc />
    public bool IsMicaSupported() => MicaController.IsSupported();

    /// <inheritdoc />
    public bool IsDesktopAcrylicSupported() => DesktopAcrylicController.IsSupported();
}

/// <summary>
/// The window's system backdrop: Mica where Windows has it, desktop acrylic where it does not, and neither on a
/// machine that supports no backdrop at all (E2-S1). Mica is a Windows 11 material, and the app supports Windows 10
/// 2004 upwards (Q-4), so the fallback is not a corner case — it is what half the supported range gets.
/// </summary>
/// <remarks>
/// This file is the only place in the app that names a backdrop material or its controller (E7-S6, AC-521), and
/// <c>WindowsVersionGuardTests</c> fails the build's tests if one appears anywhere else. Everything else the Windows 11
/// look consists of — rounded corners and the snap layouts flyout — the app never calls for: Windows draws both on
/// the system frame both windows keep, so there is nothing on Windows 10 to guard.
/// </remarks>
public static class ShellBackdrop
{
    /// <summary>What <see cref="Probe"/> found or <see cref="ForSurface"/> decided, for the log and the diagnostics page.</summary>
    public enum Kind
    {
        /// <summary>No material: the shell paints its own solid surface.</summary>
        None,
        Mica,
        Acrylic,
    }

    /// <summary>
    /// The best backdrop this machine supports. Each probe is checked before it is relied on and a probe that throws
    /// counts as "not supported": it is reported through <paramref name="warn"/> once and never escapes, so a
    /// Windows App SDK or Windows build that cannot answer leaves a flat window rather than no window.
    /// </summary>
    public static Kind Probe(IBackdropSupport support, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(support);
        ArgumentNullException.ThrowIfNull(warn);
        if (Ask(support.IsMicaSupported, "Mica", warn))
        {
            return Kind.Mica;
        }

        return Ask(support.IsDesktopAcrylicSupported, "desktop acrylic", warn) ? Kind.Acrylic : Kind.None;
    }

    /// <summary>
    /// What the shell actually shows given what the machine supports and whether a high-contrast theme is in force.
    /// High contrast turns the material off (docs/ui-screens-and-flows.md, "Accessibility contract"): the translucent
    /// base is exactly what a high-contrast theme is asked to remove, so the shell uses its solid surface instead.
    /// </summary>
    public static Kind ForSurface(Kind supported, bool highContrast) => highContrast ? Kind.None : supported;

    /// <summary>The Windows App SDK backdrop object for <paramref name="which"/>; null for <see cref="Kind.None"/>.</summary>
    public static SystemBackdrop? Create(Kind which) => which switch
    {
        Kind.Mica => new MicaBackdrop { Kind = MicaKind.Base },
        Kind.Acrylic => new DesktopAcrylicBackdrop(),
        _ => null,
    };

    private static bool Ask(Func<bool> probe, string material, Action<string> warn)
    {
        try
        {
            return probe();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            warn($"Shell backdrop: the {material} support check failed ({e.GetType().Name}: {e.Message}); treated as not supported");
            return false;
        }
    }
}
