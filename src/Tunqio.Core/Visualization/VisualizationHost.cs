namespace Tunqio.Core.Visualization;

/// <summary>One entry of the preset catalogue (<c>mp_preset_info</c>).</summary>
/// <param name="Id">Stable identifier; what <see cref="IVisualizationHost.SetPresetAsync"/> takes.</param>
/// <param name="Name">Display name, as the preset's own manifest gives it.</param>
public readonly record struct PresetInfo(string Id, string Name);

/// <summary>Palette handed to the renderer (<c>mp_theme_colors</c>); each colour is RGBA in 0..1.</summary>
public sealed record ThemeColors(
    IReadOnlyList<float> Primary,
    IReadOnlyList<float> Secondary,
    IReadOnlyList<float> Accent,
    IReadOnlyList<float> Background);

/// <summary>How the renderer is allowed to trade detail for frame rate (<c>mp_quality_policy</c>).</summary>
public enum QualityPolicy
{
    /// <summary>The renderer chooses, and changes its mind when the frame rate does.</summary>
    Auto = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

/// <summary>
/// A preset's HLSL did not compile. The preset that was already running is still running: the native loader
/// compiles on the calling thread and only swaps in a preset the device accepted, so a failure here costs the
/// caller nothing but the switch it asked for.
/// </summary>
/// <remarks>
/// <see cref="CompilerMessage"/> is the shader compiler's own first diagnostic - file, line, error code and text.
/// It is meant to be shown, not logged and swallowed: the person who will fix it is the person who wrote the
/// preset, and the message is the only thing that tells them where.
/// </remarks>
public sealed class PresetCompilationException(string presetId, string compilerMessage, Exception? inner = null)
    : InvalidOperationException($"The preset '{presetId}' did not compile: {compilerMessage}", inner)
{
    public string PresetId { get; } = presetId;

    public string CompilerMessage { get; } = compilerMessage;
}

/// <summary>
/// The visualizer surface, as the shell sees it (E4-S3). One instance per process; it owns the native renderer
/// and everything about it that the Now Playing panel drives - the panel it draws into, the size and DPI of that
/// panel, whether it is visible at all, and which preset is running.
/// </summary>
/// <remarks>
/// <para>
/// The panel is passed to <see cref="AttachAsync"/> as the COM <c>IUnknown</c> of a <c>SwapChainPanel</c>
/// (<see cref="nint"/>), not as the XAML object. docs/solution-structure.md sketched this as taking the panel
/// itself, but the same document forbids <c>Tunqio.Interop</c> from referencing WinUI, and something has to give:
/// the shell already holds the WinUI reference and already knows how to produce the pointer, so it produces it.
/// </para>
/// <para>
/// <see cref="SetThemeColors"/> and <see cref="SetQualityPolicy"/> are declared here because they belong to this
/// surface, but the native core does not implement them until E4-S6 and E4-S7 and will refuse them until then.
/// </para>
/// </remarks>
public interface IVisualizationHost : IDisposable
{
    /// <summary>True between a successful <see cref="AttachAsync"/> and <see cref="Detach"/>.</summary>
    bool IsAttached { get; }

    /// <summary>The preset catalogue: the built-in one first, then whatever the preset directory holds. Empty
    /// while detached, because the catalogue is scanned when the renderer is created.</summary>
    IReadOnlyList<PresetInfo> Presets { get; }

    /// <summary>Id of the preset currently drawing, or null while detached.</summary>
    string? ActivePresetId { get; }

    /// <summary>Render statistics, polled while attached. Feeds the diagnostics overlay.</summary>
    IObservable<RenderStats> Stats { get; }

    /// <summary>
    /// Creates the device, the swap chain and the render thread against <paramref name="swapChainPanelNative"/>,
    /// the <c>IUnknown</c> of the shell's SwapChainPanel (<see cref="nint.Zero"/> with
    /// <see cref="RendererConfig.Headless"/> for tests). Must be called on the UI thread, because
    /// <c>ISwapChainPanelNative::SetSwapChain</c> is. Attaching twice detaches the first.
    /// </summary>
    Task AttachAsync(nint swapChainPanelNative, RendererConfig config);

    /// <summary>Stops and joins the render thread and releases the device. Safe when not attached.</summary>
    void Detach();

    /// <summary>Physical pixel size and composition scale of the panel.</summary>
    void Resize(int width, int height, float scaleX, float scaleY);

    /// <summary>False parks the render thread (the panel is hidden or the window is minimised); true resumes it.</summary>
    void SetVisible(bool visible);

    /// <summary>
    /// Switches preset. Throws <see cref="PresetCompilationException"/> when the preset's shader does not
    /// compile, leaving the preset that was running in place.
    /// </summary>
    Task SetPresetAsync(string id);

    /// <summary>Sets a parameter the active preset declares; values outside its range are clamped to it.</summary>
    void SetParameter(string name, float value);

    /// <summary>Not implemented by the core until E4-S6.</summary>
    void SetThemeColors(ThemeColors colors);

    /// <summary>Not implemented by the core until E4-S7.</summary>
    void SetQualityPolicy(QualityPolicy policy);
}
