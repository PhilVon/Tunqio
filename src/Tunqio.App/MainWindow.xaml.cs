using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Tunqio.Core;
using Tunqio.Interop;

namespace Tunqio.App;

/// <summary>
/// Shell window. E0-S1 shows the product identity and proves the managed-to-native call path; the real
/// shell layout arrives with E2-S1.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = Identity.WindowTitle(null, null);
        ProductText.Text = Identity.ProductName;
        VersionText.Text = string.Create(CultureInfo.InvariantCulture, $"Version {ProductVersion()}");
        // About-page placeholder (E0-S3): the BASS attribution is shown until E6-S5 builds the real page.
        EngineText.Text = DescribeEngine() + Environment.NewLine + ThirdPartyAttribution.Bass;
    }

    private static string ProductVersion()
    {
        string? informational = typeof(MainWindow).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        return informational ?? typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "?";
    }

    private static string DescribeEngine()
    {
        try
        {
            // A native breakpoint on mpcore_abi_version() in mpcore.dll is hit from here (mixed-mode debugging).
            NativeEngineInfo.EnsureAbiCompatible();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"mpcore {NativeEngineInfo.Version} · ABI {NativeEngineInfo.AbiMajor}.{NativeEngineInfo.AbiMinor}");
        }
        catch (DllNotFoundException)
        {
            return "mpcore.dll not found next to the executable. Build native/mpcore first (msbuild Tunqio.sln).";
        }
        catch (NativeAbiMismatchException ex)
        {
            Debug.WriteLine(ex);
            return ex.Message;
        }
    }
}
