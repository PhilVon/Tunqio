using Tunqio.Core.Library;

namespace Tunqio.Core.Visualization;

/// <summary>
/// What the user asked for: whether the theme follows the music at all, and how hard it is smoothed
/// (<c>ui.reactiveTheming</c> and <c>ui.reactiveSmoothing</c>).
/// </summary>
/// <param name="Smoothing">
/// 0 to 1 as the settings file carries it, turned into a time constant by <see cref="TimeConstant"/>. It is not
/// a blend factor: a blend factor is a rate per <i>tick</i>, and this stream ticks at whatever rate the machine
/// manages, so the same number would mean different smoothing on two machines.
/// </param>
public sealed record ReactiveThemeOptions(bool Enabled, float Smoothing)
{
    /// <summary>The floor on the time constant, and the reason it is not zero.</summary>
    /// <remarks>
    /// The accessibility contract forbids full-field luminance change above 3 Hz. A smoothing of 0 with no
    /// floor would let the background jump the whole of its range between two frames, and the contrast
    /// guarantee bounds where the colour may be, not how fast it may get there. Half a second is what makes a
    /// third of a second's worth of exponential approach stay under the 0.10 the golden-image tests use for the
    /// same threshold - and it is a measurement rather than a guess: at 0.25 s the light theme crossed 0.1201,
    /// which is what <c>ReactiveThemeEngineTests</c> caught and what this number answers.
    /// </remarks>
    public static readonly TimeSpan MinimumTimeConstant = TimeSpan.FromSeconds(0.5);

    /// <summary>The same floor for the light theme, which needs a higher one, and why it is a separate number.</summary>
    /// <remarks>
    /// <para>
    /// The 0.5 s above was set against a single hand-picked worst case: silence at 20 Hz to full scale at
    /// 20 kHz, holding the harmonic ratio at 0.8 for both ends. <c>ReactiveThemeEngineTests</c> now searches
    /// every ordered pair of reachable states instead of picking one, and the pair it finds in the LIGHT theme
    /// is worse than the pair that was picked - 0.1005 of relative luminance against the 0.10 threshold, on the
    /// constants this shipped with, where the hand-picked pair reported 0.0559. So the light theme was over the
    /// bound before T-162 widened anything; the search is what found it, not the widening.
    /// </para>
    /// <para>
    /// Why the light theme and not the dark one, at 0.0562 for the same search: HSL lightness is not luminance.
    /// Near the top of the range the same lightness at two different hues is two quite different luminances, so
    /// a hue step at high chroma moves the field even when the lightness never changes - which is the same
    /// asymmetry the engine's constructor already reins in with a smaller lightness span and a 0.35 saturation
    /// scale. Widening the hue sweep from 120 to 180 degrees makes that step reachable over a longer arc and
    /// takes the worst pair from 0.1005 to 0.1030, so it makes a pre-existing violation slightly worse rather
    /// than causing it.
    /// </para>
    /// <para>
    /// 0.8 s puts the light theme's worst surface at 0.0826, a 17% margin and about the dark theme's own
    /// (0.0756). Measured rather than solved for, because the worst pair moves between surfaces as the floor
    /// changes: 0.65 s leaves the secondary at 0.0976, 0.70 s at 0.0906, 0.75 s at 0.0838, 0.90 s at 0.0707.
    /// The secondary is the binding surface and not the primary, because in a light theme it is the one
    /// multiplied UP (lightness x 1.04), which puts it where chroma costs the most luminance.
    /// </para>
    /// <para>
    /// It is a floor and not a default: the shipped smoothing of 0.15 is a 1.025 s time constant, well above
    /// it, so this changes nothing at all unless a user drags <c>ui.reactiveSmoothing</c> below about 0.086.
    /// That is deliberately the cheapest place to spend the fix - the alternative levers are the light theme's
    /// chroma and its lightness span, and both would cost reaction at every setting rather than at one end,
    /// and the chroma one would undo T-176.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan LightMinimumTimeConstant = TimeSpan.FromSeconds(0.8);

    /// <summary>Where <see cref="Smoothing"/> 1.0 lands: slow enough to be a wash rather than a follow.</summary>
    public static readonly TimeSpan MaximumTimeConstant = TimeSpan.FromSeconds(4.0);

    /// <summary>The documented defaults (<see cref="SettingsKeys.Defaults"/>).</summary>
    public static ReactiveThemeOptions Default { get; } =
        new(SettingsKeys.Defaults.UiReactiveTheming, SettingsKeys.Defaults.UiReactiveSmoothing);

    /// <summary>Reads both keys, each falling back to its documented default.</summary>
    public static ReactiveThemeOptions Read(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ReactiveThemeOptions(
            settings.GetValue(SettingsKeys.UiReactiveTheming, SettingsKeys.Defaults.UiReactiveTheming),
            settings.GetValue(SettingsKeys.UiReactiveSmoothing, SettingsKeys.Defaults.UiReactiveSmoothing));
    }

    /// <summary>
    /// The exponential time constant: after this long the colour has covered 1 - 1/e (63.2%) of the distance to
    /// whatever the music is asking for, whatever rate the frames arrived at.
    /// </summary>
    public TimeSpan TimeConstant
    {
        get
        {
            double t = Math.Clamp(Smoothing, 0.0f, 1.0f);
            return MinimumTimeConstant + ((MaximumTimeConstant - MinimumTimeConstant) * t);
        }
    }
}

/// <summary>The four colours the theming produces, each already past the contrast guarantee.</summary>
public sealed record ReactiveThemePalette(Srgb Primary, Srgb Secondary, Srgb Accent, Srgb Background)
{
    /// <summary>Every colour, in <c>mp_theme_colors</c>' order - which is also b0's <c>theme</c> order.</summary>
    public IReadOnlyList<Srgb> All => [Primary, Secondary, Accent, Background];

    /// <summary>The same palette as the renderer takes it, opaque (<c>mp_renderer_set_theme</c>).</summary>
    public ThemeColors ToThemeColors() =>
        new(Channels(Primary), Channels(Secondary), Channels(Accent), Channels(Background));

    private static float[] Channels(Srgb c) => [c.R / 255f, c.G / 255f, c.B / 255f, 1.0f];
}

/// <summary>
/// Audio-reactive theming (E4-S6), as a pure function of the frames it is given and the time between them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here knows about a window.</b> The mapping, the smoothing and the contrast guarantee are
/// arithmetic, so they are tested by feeding synthetic frames to <see cref="Advance"/> and reading the colours
/// back, rather than by looking at a running shell. What the shell adds is a Composition gradient painted from
/// <see cref="ReactiveThemePalette"/> and the accessibility switches that stop it.
/// </para>
/// <para>
/// <b>The smoothing is exponential in wall-clock time, not per frame.</b> <c>alpha = 1 - exp(-dt / tau)</c>, so
/// ten steps of 10 ms land where one step of 100 ms does. That matters twice: the frame source runs at a
/// nominal 30 Hz and a busy machine will not hold it, and AC-127's "within one second" is a claim about seconds
/// rather than about ticks.
/// </para>
/// <para>
/// <b>Hue is smoothed on the unit circle</b>, not as a number of degrees. 350 degrees and 10 degrees are twenty
/// degrees apart, and a scalar EMA between them sweeps the long way round through every other colour; averaging
/// (cos, sin) takes the short way and has no seam to sit on.
/// </para>
/// <para>
/// <b><see cref="AnalysisFrame.Discontinuities"/>.</b> The frame's own documentation says a consumer carrying
/// anything across frames - "a smoothed level, a beat history" - starts again when this moves. This engine is
/// built so that starting again costs nothing: its target is a pure function of one frame, and the only state
/// it carries between frames is the colour currently on screen. There is deliberately no running maximum, no
/// flux and no beat history, because each of those would be a quantity a gap makes wrong. So a restart discards
/// the only history there is, which is the colour being displayed - and discarding that would snap the
/// background in one frame, which is the full-field step the accessibility contract exists to prevent. The
/// count is carried on <see cref="Discontinuities"/> and the restarts are counted on
/// <see cref="AnalysisRestarts"/> so the decision is visible rather than silent, and
/// <c>ReactiveThemeEngineTests</c> pins it: a moved count changes no colour.
/// </para>
/// </remarks>
public sealed class ReactiveThemeEngine
{
    // Where the album art gets a say. The art palette is the track's own colour and the analysis is the moment's,
    // so the hue is blended rather than replaced: a red sleeve stays red through a bright chorus, but the
    // chorus still moves it.
    private const double ArtHueWeight = 0.45;

    // The spectral centroid range the hue sweeps over, and the sweep itself. This is the range the presets'
    // "colour = spectral centroid" mode uses too (the same two numbers appear in all four .hlsl), so a preset
    // and the window agree about what "bright" means; changing one without the other breaks that agreement.
    // The sweep starts deep indigo and ends amber, going the short way through magenta and red.
    //
    // 300 Hz to 8.5 kHz rather than the 50 Hz to 12 kHz this shipped with, and 180 degrees rather than 120,
    // because the old numbers spent most of the sweep on centroids music does not produce. Measured over a
    // 140-file library, 2.8M analysis hops (T-175, D-28): the union of every track's own p5..p95 centroid band
    // is 312.7 Hz to 8509.7 Hz, so everything outside 300..8500 was sweep nobody could see. Under the old
    // constants a typical track painted 16.0 of the 120 degrees; under these it paints 39.4 of 180. The
    // eight-bit sRGB grid at the dark theme's lightness resolves about one colour per 2 degrees, so that is
    // roughly twenty distinguishable colours over a track instead of eight - and for the middle half of a
    // track, which is what "it does not react" is really about, the interquartile range goes 5.0 degrees to
    // 12.4, or two colours to six. Clipping stays under 1% of hops at each end, which is what keeps the
    // presets' ramp honest as well as this one's.
    private const double CentroidLowHz = 300.0;
    private const double CentroidHighHz = 8500.0;
    private const double HueStart = 265.0;
    private const double HueSweep = 180.0;

    // The harmonic-ratio band the saturation is mapped over, and the saturation it maps to. Harmonic ratio is
    // 1 minus spectral flatness, so its 0..1 input is theoretical: real music's spectrum is peaky and never
    // visits the bottom of it. Over the same 2.8M hops the pooled p5..p95 is 0.879..0.994 (T-176, D-28), so
    // mapping straight off the raw 0..1 - which is what this did - produced saturation 0.714..0.743 and nothing
    // else, a thirtieth of the range it appears to have. In the dark theme, where saturationScale is 1.0, that
    // barely showed. In the light theme, where the constructor below explains that saturation rather than
    // lightness is what moves the field and scales it by 0.35, the entire within-track movement was 0.0058 of
    // chroma: static to any eye. Normalising through the band the music actually occupies takes that to 0.0466,
    // eight times as much, measured over the same library. Read off the sRGB the engine actually emits - which
    // is the more honest number, because the contrast guarantee and the lightness axis both feed back into a
    // saturation read out of a finished colour - the light theme's painted chroma span goes 0.0237 to 0.0517.
    //
    // The output range either side of the band is deliberately unchanged, so the fix is a range and not a new
    // palette. It is not quite free: because the median track sits high in the band rather than at its centre,
    // the TYPICAL chroma drops from 0.2556 to 0.2176, so the light theme is about 15% less saturated at rest
    // than it was and considerably less static. That trade is the point rather than a side effect.
    private const double HarmonicLow = 0.88;
    private const double HarmonicHigh = 0.99;
    private const double SaturationFloor = 0.20;
    private const double SaturationSpan = 0.55;

    // RMS as a meter reads it. Below -45 dBFS is silence for this purpose and -6 dBFS is as loud as a master
    // gets, so that span is the whole of the energy axis.
    private const double QuietDb = -45.0;
    private const double LoudDb = -6.0;

    private readonly bool _dark;
    private readonly TimeSpan _minimumTimeConstant;
    private readonly IReadOnlyList<Srgb> _foregrounds;
    private readonly double _restLightness;
    private readonly double _lightnessSpan;
    private readonly double _saturationScale;

    private double _hueX;
    private double _hueY;
    private double _saturation;
    private double _energy;
    private bool _seeded;
    private byte _discontinuities;
    private double? _artHue;

    /// <summary>
    /// A new engine for one theme. <paramref name="dark"/> picks both the foreground text the guarantee is made
    /// against and which way loudness moves lightness: a dark theme's background deepens with the music and a
    /// light theme's warms, because in a light theme there is nowhere darker to go that text still reads on.
    /// </summary>
    public ReactiveThemeEngine(bool dark, ReactiveThemeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dark = dark;
        Options = options;
        _minimumTimeConstant = dark
            ? ReactiveThemeOptions.MinimumTimeConstant
            : ReactiveThemeOptions.LightMinimumTimeConstant;
        _foregrounds = dark ? ReactiveTheming.DarkForegrounds : ReactiveTheming.LightForegrounds;
        // How far the music may move the background, per theme, and it is not symmetric. Near the top of the
        // range sRGB luminance climbs steeply and chroma costs a great deal of it: a fully saturated colour at
        // lightness 0.93 is far darker than the grey beside it, so in a light theme the *saturation* is what
        // moves the field, not the lightness. Both are therefore reined in on that side - measured, not
        // guessed: at the dark theme's numbers the light theme crossed the 0.10 flash threshold at 0.1003.
        (_restLightness, _lightnessSpan, _saturationScale) = dark ? (0.09, 0.16, 1.0) : (0.93, -0.07, 0.35);
        Reset();
    }

    /// <summary>The smoothing and the off switch, re-readable while running (the settings page may change them).</summary>
    public ReactiveThemeOptions Options { get; set; }

    /// <summary>Which theme's text the contrast guarantee is being made against.</summary>
    public bool DarkTheme => _dark;

    /// <summary>True while <see cref="Options"/> says the theme may follow the music.</summary>
    public bool Enabled => Options.Enabled;

    /// <summary>The colours as they stand, always past <see cref="ReactiveContrast.Constrain"/>.</summary>
    public ReactiveThemePalette Palette { get; private set; } = null!;

    /// <summary>The colours with no music at all: what <see cref="Advance"/> settles on, and what "off" looks like.</summary>
    public ReactiveThemePalette Resting => Build(_dark ? HueStart : HueStart, 0.0, 0.0);

    /// <summary>The count off the last frame seen (<see cref="AnalysisFrame.Discontinuities"/>).</summary>
    public byte Discontinuities => _discontinuities;

    /// <summary>How many times that count has moved since this engine was created.</summary>
    public int AnalysisRestarts { get; private set; }

    /// <summary>Frames folded in since the engine was created or last <see cref="Reset"/>.</summary>
    public long FramesSeen { get; private set; }

    /// <summary>
    /// The current track's art palette, or null for a track without one. Its dominant colour's hue is blended
    /// into the hue the analysis chooses; its lightness is not, because lightness is what the contrast
    /// guarantee owns.
    /// </summary>
    public void SetArtPalette(ArtPalette? palette)
    {
        if (palette is null || palette.Colors.Count == 0)
        {
            _artHue = null;
            return;
        }

        PaletteColor dominant = palette.Dominant;
        (double hue, double saturation, _) = ReactiveTheming.ToHsl(new Srgb(dominant.R, dominant.G, dominant.B));

        // A near-grey sleeve has a hue, but it is noise: its chromaticity is whatever survived quantisation.
        _artHue = saturation >= 0.12 ? hue : null;
    }

    /// <summary>Back to the resting palette, as if nothing had ever played.</summary>
    public void Reset()
    {
        _hueX = Math.Cos(HueStart * Math.PI / 180.0);
        _hueY = Math.Sin(HueStart * Math.PI / 180.0);
        _saturation = 0.0;
        _energy = 0.0;
        _seeded = false;
        FramesSeen = 0;
        Palette = Build(HueStart, 0.0, 0.0);
    }

    /// <summary>
    /// Folds in <paramref name="elapsed"/> of time and, when there is one, a new frame. A null frame is a tick
    /// on which nothing new arrived - paused, stopped, or simply faster than the analysis publishes - and the
    /// theme eases back toward rest rather than freezing on whatever the last note was.
    /// </summary>
    public ReactiveThemePalette Advance(AnalysisFrame? frame, TimeSpan elapsed)
    {
        if (!Options.Enabled)
        {
            Reset();
            return Palette;
        }

        double targetHue;
        double targetSaturation;
        double targetEnergy;
        if (frame is { } f)
        {
            if (FramesSeen > 0 && f.Discontinuities != _discontinuities)
            {
                AnalysisRestarts++;
            }

            _discontinuities = f.Discontinuities;
            FramesSeen++;
            targetHue = HueFor(f);
            targetSaturation = SaturationFor(f.HarmonicRatio);
            targetEnergy = EnergyFor(f.Rms);
        }
        else
        {
            // Nothing playing: the hue stays where it is and the colour drains out of it.
            targetHue = CurrentHue();
            targetSaturation = 0.0;
            targetEnergy = 0.0;
        }

        double alpha = Alpha(elapsed);
        if (!_seeded)
        {
            // The first frame is not smoothed toward from an invented starting colour - it *is* the starting
            // colour. Smoothing from a placeholder would show a sweep nobody played.
            alpha = 1.0;
            _seeded = true;
        }

        double tx = Math.Cos(targetHue * Math.PI / 180.0);
        double ty = Math.Sin(targetHue * Math.PI / 180.0);
        _hueX += alpha * (tx - _hueX);
        _hueY += alpha * (ty - _hueY);
        _saturation += alpha * (targetSaturation - _saturation);
        _energy += alpha * (targetEnergy - _energy);

        Palette = Build(CurrentHue(), _saturation, _energy);
        return Palette;
    }

    /// <summary>
    /// The smoothing factor for one step of <paramref name="elapsed"/>: <c>1 - exp(-dt / tau)</c>, where tau is
    /// the setting's time constant held up to this theme's own floor
    /// (<see cref="ReactiveThemeOptions.LightMinimumTimeConstant"/> explains why the light theme has a
    /// different one). At the shipped smoothing the floor never binds.
    /// </summary>
    public double Alpha(TimeSpan elapsed)
    {
        double dt = elapsed.TotalSeconds;
        if (dt <= 0.0)
        {
            return 0.0;
        }

        double tau = Math.Max(Options.TimeConstant.TotalSeconds, _minimumTimeConstant.TotalSeconds);
        return tau <= 0.0 ? 1.0 : 1.0 - Math.Exp(-dt / tau);
    }

    private double CurrentHue()
    {
        if (_hueX == 0.0 && _hueY == 0.0)
        {
            return HueStart;
        }

        double degrees = Math.Atan2(_hueY, _hueX) * 180.0 / Math.PI;
        return degrees < 0.0 ? degrees + 360.0 : degrees;
    }

    private double HueFor(AnalysisFrame frame)
    {
        double centroid = Math.Clamp(frame.SpectralCentroidHz, CentroidLowHz, CentroidHighHz);
        double position = Math.Log(centroid / CentroidLowHz) / Math.Log(CentroidHighHz / CentroidLowHz);
        double hue = HueStart + (position * HueSweep);
        if (_artHue is not { } art)
        {
            return hue % 360.0;
        }

        // Blended on the circle, for the same reason the smoothing is: two hues either side of zero have a
        // short way between them and a long one, and the mean of the degrees is the wrong one half the time.
        double x = ((1.0 - ArtHueWeight) * Math.Cos(hue * Math.PI / 180.0)) + (ArtHueWeight * Math.Cos(art * Math.PI / 180.0));
        double y = ((1.0 - ArtHueWeight) * Math.Sin(hue * Math.PI / 180.0)) + (ArtHueWeight * Math.Sin(art * Math.PI / 180.0));
        double blended = Math.Atan2(y, x) * 180.0 / Math.PI;
        return blended < 0.0 ? blended + 360.0 : blended;
    }

    /// <summary>
    /// Saturation for one harmonic ratio, normalised through <see cref="HarmonicLow"/>..<see cref="HarmonicHigh"/>
    /// rather than through the raw 0..1 the feature is defined on. See the constants for why.
    /// </summary>
    private static double SaturationFor(float harmonicRatio)
    {
        double position = Math.Clamp((harmonicRatio - HarmonicLow) / (HarmonicHigh - HarmonicLow), 0.0, 1.0);
        return Math.Clamp(SaturationFloor + (position * SaturationSpan), 0.0, 1.0);
    }

    private static double EnergyFor(float rms)
    {
        double db = 20.0 * Math.Log10(Math.Max(rms, 1e-5));
        return Math.Clamp((db - QuietDb) / (LoudDb - QuietDb), 0.0, 1.0);
    }

    private ReactiveThemePalette Build(double hue, double saturation, double energy)
    {
        double lightness = Math.Clamp(_restLightness + (energy * _lightnessSpan), 0.0, 1.0);
        double chroma = saturation * _saturationScale;
        Srgb primary = Colour(hue, chroma, lightness);
        Srgb secondary = Colour(hue + 24.0, chroma, lightness * (_dark ? 0.55 : 1.04));
        Srgb accent = Colour(hue + 190.0, Math.Min(1.0, chroma * 1.25), lightness * (_dark ? 1.35 : 0.94));
        Srgb background = Colour(hue, chroma * 0.5, lightness * (_dark ? 0.35 : 1.05));
        return new ReactiveThemePalette(primary, secondary, accent, background);
    }

    private Srgb Colour(double hue, double saturation, double lightness) =>
        ReactiveContrast.Constrain(
            ReactiveTheming.FromHsl(((hue % 360.0) + 360.0) % 360.0, Math.Clamp(saturation, 0.0, 1.0),
                                    Math.Clamp(lightness, 0.0, 1.0)),
            _foregrounds);
}
