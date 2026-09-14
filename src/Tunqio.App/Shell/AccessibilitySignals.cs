using System.Runtime.InteropServices;
using Windows.UI.ViewManagement;

namespace Tunqio.App.Shell;

/// <summary>
/// The two accessibility switches that turn audio-reactive theming off (docs/ui-screens-and-flows.md,
/// "Accessibility contract"): "Show animations in Windows" and high contrast.
/// </summary>
/// <remarks>
/// An interface because neither can be driven from a test - a test that set either would be changing the
/// machine's own settings under whoever is using it, and would take every other UI test with it. So the
/// decision this signal feeds is tested against a fake, and what is left untestable is the one line that reads
/// Windows, which <see cref="SystemAccessibilitySignals"/> is deliberately nothing but.
/// </remarks>
public interface IAccessibilitySignals
{
    /// <summary>False when Windows has been asked to reduce motion (<c>UISettings.AnimationsEnabled</c>).</summary>
    bool AnimationsEnabled { get; }

    /// <summary>True while a high-contrast theme is in force.</summary>
    bool HighContrast { get; }

    /// <summary>Raised when either has changed. Not guaranteed to be raised on the UI thread.</summary>
    event EventHandler? Changed;
}

/// <summary>The real thing: Windows, read live.</summary>
/// <remarks>
/// <para>
/// Animations come from <see cref="UISettings"/>, which is the property the contract names and which raises a
/// change event in a desktop app. High contrast comes from <c>SystemParametersInfo(SPI_GETHIGHCONTRAST)</c>
/// rather than from <c>AccessibilitySettings</c>, because that class wants a <c>CoreWindow</c> and a WinUI 3
/// desktop app does not have one.
/// </para>
/// <para>
/// Both properties read Windows on every get rather than caching, so a consumer that polls is never stale even
/// if the event is missed - which is what bounds how long reactive theming can survive the switch being thrown
/// at the poll interval rather than at whatever the event delivery costs.
/// </para>
/// </remarks>
public sealed class SystemAccessibilitySignals : IAccessibilitySignals, IDisposable
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HighContrastOn = 0x00000001;

    private readonly UISettings _settings = new();

    /// <summary>
    /// Subscribes to the system's own change notifications: <c>AnimationsEnabledChanged</c> for reduced motion, and
    /// <c>ColorValuesChanged</c>, which is what Windows raises when a high-contrast theme is turned on or off (E7-S6).
    /// It is also raised for a theme or accent change, which costs a consumer one extra re-read and nothing else.
    /// </summary>
    public SystemAccessibilitySignals()
    {
        _settings.AnimationsEnabledChanged += OnAnimationsEnabledChanged;
        _settings.ColorValuesChanged += OnColorValuesChanged;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool AnimationsEnabled => _settings.AnimationsEnabled;

    /// <inheritdoc />
    public bool HighContrast
    {
        get
        {
            var info = new HighContrastInfo { Size = (uint)Marshal.SizeOf<HighContrastInfo>() };
            return SystemParametersInfo(SpiGetHighContrast, info.Size, ref info, 0)
                   && (info.Flags & HighContrastOn) != 0;
        }
    }

    /// <summary>Unsubscribes; the settings object itself has nothing to release.</summary>
    public void Dispose()
    {
        _settings.AnimationsEnabledChanged -= OnAnimationsEnabledChanged;
        _settings.ColorValuesChanged -= OnColorValuesChanged;
    }

    private void OnAnimationsEnabledChanged(UISettings sender, object args) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnColorValuesChanged(UISettings sender, object args) => Changed?.Invoke(this, EventArgs.Empty);

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrastInfo
    {
        public uint Size;
        public uint Flags;
        public nint DefaultScheme;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref HighContrastInfo info, uint update);
}
