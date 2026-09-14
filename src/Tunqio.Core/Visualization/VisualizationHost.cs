namespace Tunqio.Core.Visualization;

/// <summary>One entry of the preset catalogue (<c>mp_preset_info</c>).</summary>
/// <param name="Id">Stable identifier; what <see cref="IVisualizationHost.SetPresetAsync"/> takes.</param>
/// <param name="Name">Display name, as the preset's own manifest gives it.</param>
public readonly record struct PresetInfo(string Id, string Name);

/// <summary>
/// One parameter a preset declares, as <c>mp_preset_param_info</c> gives it (T-142). Everything a settings page
/// needs to build a control for a preset it has never seen - including one a user wrote - without a table of
/// ranges compiled into it.
/// </summary>
/// <param name="Name">What <see cref="IVisualizationHost.SetParameter"/> takes.</param>
/// <param name="Label">Display name; the preset's <c>label</c>, or <paramref name="Name"/> when it declares none.</param>
/// <param name="Unit">"px", "Hz", …; empty when the number is a bare one.</param>
/// <param name="Minimum">Low end of the declared range; a value below it is clamped, not refused.</param>
/// <param name="Maximum">High end of the declared range.</param>
/// <param name="Default">Where the parameter starts, and what Reset puts back.</param>
/// <param name="Step">The granularity the preset means, or 0 for continuous.</param>
/// <param name="Hidden">
/// Set by code and never by a person, so a settings page must not offer it. Ambient Glow's
/// <c>art_primary</c>/<c>art_secondary</c>/<c>art_accent</c> are the reason this exists: they carry one sRGB
/// colour packed into a float from the album art palette, and a slider from −1 to 16 777 215 is not a control.
/// </param>
/// <param name="Choices">
/// Named modes, in value order from <paramref name="Minimum"/>, for a parameter that is a mode rather than a
/// quantity (<c>colour</c> in all four built-ins). Empty for everything else.
/// </param>
public sealed record PresetParameter(
    string Name,
    string Label,
    string Unit,
    float Minimum,
    float Maximum,
    float Default,
    float Step,
    bool Hidden,
    IReadOnlyList<string> Choices)
{
    /// <summary>True when the parameter is a mode: <see cref="Choices"/> names each value.</summary>
    public bool IsChoice => Choices.Count > 0;
}

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
/// <see cref="SetQualityPolicy"/> was declared here and refused by the core until E4-S7 implemented it, which
/// makes it the last of E0-S5's stubs; <see cref="SetThemeColors"/> was the same until E4-S6.
/// </para>
/// </remarks>
public interface IVisualizationHost : IDisposable
{
    /// <summary>True between a successful <see cref="AttachAsync"/> and <see cref="Detach"/>.</summary>
    bool IsAttached { get; }

    /// <summary>
    /// Whether the attached renderer was given an audio engine to draw from. False means every preset is on its
    /// idle animation for the life of this attachment, whatever is playing.
    /// </summary>
    /// <remarks>
    /// On the interface, and on the diagnostics overlay, because of how T-179 hid: the failure mode of "no
    /// audio reaches the picture" is a preset drawing a smooth travelling sine rather than a preset drawing
    /// nothing, so it reads as the product working. Nine stories of review, several of them looking straight at
    /// the visualizer, took a moving idle animation as evidence that the visualizer was reacting. A state that
    /// cannot be told apart from success by looking has to be reported in words somewhere.
    /// </remarks>
    bool HasAudioSource { get; }

    /// <summary>The preset catalogue: the built-in one first, then whatever the preset directory holds. Empty
    /// while detached, because the catalogue is scanned when the renderer is created.</summary>
    IReadOnlyList<PresetInfo> Presets { get; }

    /// <summary>Id of the preset currently drawing, or null while detached.</summary>
    string? ActivePresetId { get; }

    /// <summary>
    /// <see cref="ActivePresetId"/> changed: <see cref="SetPresetAsync"/> succeeded, or <see cref="AttachAsync"/>
    /// started the catalogue's first entry. Carries the new id.
    /// </summary>
    /// <remarks>
    /// The preset decides which parameters mean anything, so anything holding values for one preset has to know
    /// when that preset starts drawing and not only when its own value changes. The album art palette (T-147) is
    /// the case: <c>ambient-glow</c> is the only preset that declares <c>art_primary</c>, so a person who picks
    /// Ambient Glow in the middle of a track would otherwise watch it draw its default colours until the next
    /// one. Raised on the thread that made the change, which for the settings page is the UI thread.
    /// </remarks>
    event EventHandler<string>? PresetChanged;

    /// <summary>Render statistics, polled while attached. Feeds the diagnostics overlay.</summary>
    IObservable<RenderStats> Stats { get; }

    /// <summary>
    /// Creates the device, the swap chain and the render thread against <paramref name="swapChainPanelNative"/>,
    /// the <c>IUnknown</c> of the shell's SwapChainPanel (<see cref="nint.Zero"/> with
    /// <see cref="RendererConfig.Headless"/> for tests). Must be called on the UI thread, because
    /// <c>ISwapChainPanelNative::SetSwapChain</c> is. Attaching twice detaches the first.
    /// </summary>
    /// <param name="audioEngineNative">
    /// The <c>mp_engine</c> the presets are drawn from, or <see cref="nint.Zero"/> for a visualizer with no
    /// audio behind it. It is an <see cref="nint"/> here for the same reason
    /// <paramref name="swapChainPanelNative"/> is: Core describes the contract and does not reference the
    /// binding that owns the handle.
    /// <para>
    /// <b>It is required, and that is the whole of T-179.</b> The renderer polls the engine for analysis and
    /// gives up immediately when it has none, which leaves the preset constant buffer's <c>timing.w</c> at zero
    /// - and every shipped preset reads that as "nothing is playing" and draws its idle animation. So a
    /// visualizer attached with no engine is not visibly broken, it is visibly *fine*: a smooth travelling sine
    /// that has never heard the music. It shipped that way because this parameter did not exist and the
    /// binding's own default filled it in.
    /// </para>
    /// </param>
    Task AttachAsync(nint swapChainPanelNative, nint audioEngineNative, RendererConfig config);

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

    /// <summary>
    /// What one preset in the catalogue declares (T-142) - any of them, not only the one drawing, so a settings
    /// page can describe a preset before switching to it. Empty when the preset declares no parameters; throws
    /// when no preset has that id.
    /// </summary>
    IReadOnlyList<PresetParameter> GetPresetParameters(string presetId);

    /// <summary>
    /// A second directory scanned for presets in addition to the ones shipped beside the core - the app's own
    /// <c>%LocalAppData%\Tunqio\presets</c>, which the core cannot name for itself. Rescans as it is set.
    /// </summary>
    void SetUserPresetRoot(string path);

    /// <summary>
    /// Rereads both preset roots and returns the new catalogue. This is the "refresh" behind a preset dropped in
    /// while the app runs: the catalogue is otherwise read once, when the renderer is created (T-126). What is
    /// drawing keeps drawing, whatever the rescan finds.
    /// </summary>
    IReadOnlyList<PresetInfo> RefreshPresets();

    /// <summary>The most recent render statistics, or null while detached. A pull, for a diagnostics readout
    /// that refreshes on its own schedule rather than on <see cref="Stats"/>'.</summary>
    RenderStats? TryGetStats();

    /// <summary>
    /// The renderer-wide theme (E4-S6): four colours into every preset's constant buffer, surviving a preset
    /// switch, and the same ones the shell's own background gradient is painted from. Channels outside 0..1 are
    /// clamped; one that is not a finite number throws.
    /// </summary>
    void SetThemeColors(ThemeColors colors);

    /// <summary>
    /// How the renderer may trade detail for frame rate (E4-S7). <see cref="QualityPolicy.Auto"/> - the
    /// default - hands the tier to a controller on the render thread, which drops the render scale when the
    /// frame cost goes over budget and raises it when the headroom comes back; the other three pin the tier
    /// and stop it deciding. What it decided, and what it decided it on, is on <see cref="RenderStats"/>.
    /// </summary>
    void SetQualityPolicy(QualityPolicy policy);

    /// <summary>
    /// The renderer-wide attack and decay envelope on the analysis frame (T-184): spectrum, bands and levels rise over
    /// <see cref="TemporalSmoothing.AttackMs"/> and fall over <see cref="TemporalSmoothing.DecayMs"/> instead of
    /// jumping, for every preset, surviving a preset switch. <see cref="TemporalSmoothing.Off"/> draws exactly what the
    /// analysis published. Applied on the render thread's next frame, so it is live; it is not remembered by the
    /// renderer across a detach, so whoever attaches sets it again.
    /// </summary>
    void SetTemporalSmoothing(TemporalSmoothing smoothing);
}
