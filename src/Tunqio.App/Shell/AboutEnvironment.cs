using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Tunqio.Core;
using Tunqio.Interop;

namespace Tunqio.App.Shell;

/// <summary>
/// What Settings › About &amp; Diagnostics says about the build it is running in (E6-S5): the product name, the
/// app version, the native core's version and ABI, the OS, and where the executable is (which is where
/// <c>licenses/</c> is). A record so a test can hand the view model a made-up one; <see cref="Current"/> reads
/// the real one.
/// </summary>
/// <param name="ProductName"><see cref="Identity.ProductName"/>.</param>
/// <param name="AppVersion">The informational version, which is <c>TunqioVersion</c> in Directory.Build.props.</param>
/// <param name="CoreVersion"><c>mp_version()</c> of the loaded core, or why it could not be read.</param>
/// <param name="AbiVersion">The core's ABI as <c>major.minor</c>, or why it could not be read.</param>
/// <param name="OsVersion">The OS, as the runtime describes it.</param>
/// <param name="BaseDirectory">The directory holding the executable; <c>licenses/</c> is under it.</param>
public sealed record AboutEnvironment(
    string ProductName,
    string AppVersion,
    string CoreVersion,
    string AbiVersion,
    string OsVersion,
    string BaseDirectory)
{
    /// <summary>The build this process is: assembly attributes, the native core through the interop, and the runtime's OS description.</summary>
    public static AboutEnvironment Current()
    {
        (string core, string abi) = ReadCore();
        return new AboutEnvironment(
            Identity.ProductName,
            ReadAppVersion(),
            core,
            abi,
            string.Create(CultureInfo.InvariantCulture, $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"),
            AppContext.BaseDirectory);
    }

    /// <summary>
    /// The informational version (<c>InformationalVersion</c> in Directory.Build.props is <c>TunqioVersion</c>), falling
    /// back to the three-part assembly version, which is the same number with a <c>.0</c> the props append.
    /// </summary>
    public static string ReadAppVersion()
    {
        Assembly assembly = typeof(AboutEnvironment).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // A source-link build appends "+<commit>"; the page shows the version and the export keeps the rest.
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }

    private static (string Core, string Abi) ReadCore()
    {
        try
        {
            NativeEngineInfo.EnsureAbiCompatible();
            return (NativeEngineInfo.Version, string.Create(CultureInfo.InvariantCulture, $"{NativeEngineInfo.AbiMajor}.{NativeEngineInfo.AbiMinor}"));
        }
        catch (DllNotFoundException)
        {
            return ("not loaded (mpcore.dll is not next to the executable)", "unknown");
        }
        catch (NativeAbiMismatchException ex)
        {
            return ("refused: " + ex.Message, "unknown");
        }
    }
}
