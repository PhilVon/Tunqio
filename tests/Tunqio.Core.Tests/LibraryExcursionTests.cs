using System.Globalization;
using Tunqio.Core.Visualization;
using Xunit.Abstractions;

namespace Tunqio.Core.Tests;

/// <summary>
/// T-175, the managed half: turns the per-hop feature traces
/// <c>native/mpcore.tests/src/test_library_excursion.cpp</c> writes into the units the judgement is actually
/// about - degrees of hue and units of lightness - by pushing them through the shipped
/// <see cref="ReactiveThemeEngine"/> rather than through a copy of its arithmetic.
/// </summary>
/// <remarks>
/// <para>
/// Skipped unless <c>TUNQIO_EXCURSION_OUT</c> points at a directory the native harness has already written,
/// because it needs somebody's music and no CI machine has any. It writes its results beside the traces.
/// </para>
/// <para>
/// <b>Two excursions, and the difference between them is the point.</b> The mapping's excursion is what the
/// centroid and the RMS ask for, frame by frame, with no smoothing: it is the range the mapping would paint if
/// the theme could follow instantly. The engine's excursion is what <see cref="ReactiveThemeEngine.Advance"/>
/// actually produces at the 30 Hz <see cref="IAnalysisFrameSource.Frames"/> publishes at and the default
/// 1.025 s time constant. A listener sees the second. If the two are far apart the smoothing is what is eating
/// the reaction, and widening the mapping would be the wrong repair.
/// </para>
/// </remarks>
public class LibraryExcursionTests(ITestOutputHelper output)
{
    /// <summary>What <see cref="IAnalysisFrameSource.Frames"/> documents, and so what the theming sees.</summary>
    private const double ThemeHz = 30.0;

    /// <summary>Hops per second the native analysis publishes: 48000 / 512.</summary>
    private const double HopHz = 48000.0 / 512.0;

    private static readonly float[] NoSpectrum = [];

    private static string? TraceDirectory => Environment.GetEnvironmentVariable("TUNQIO_EXCURSION_OUT");

    /// <summary>The mapping's hue target for one centroid, with no album art in the way (ReactiveTheme.cs).</summary>
    private static double MappedHue(double centroidHz)
    {
        double c = Math.Clamp(centroidHz, 50.0, 12000.0);
        return 265.0 + (Math.Log(c / 50.0) / Math.Log(12000.0 / 50.0) * 120.0);
    }

    /// <summary>The mapping's lightness target for one RMS in the dark theme (rest 0.09, span 0.16).</summary>
    private static double MappedLightness(double rms)
    {
        double db = 20.0 * Math.Log10(Math.Max(rms, 1e-5));
        double energy = Math.Clamp((db - -45.0) / (-6.0 - -45.0), 0.0, 1.0);
        return 0.09 + (energy * 0.16);
    }

    /// <summary>
    /// Puts a hue read back off a palette (0 to 360) into the frame the mapping works in (265 to 385), so that
    /// percentiles over it mean something. The sweep starts at 265 degrees and is 120 wide, which crosses the
    /// 360/0 seam at about 5000 Hz - so a track sitting either side of that seam has hues at 357 and at 2, and
    /// a linear percentile over those reports a 355 degree excursion for what is in fact five.
    /// </summary>
    private static double Unwrap(double hue) => hue < 200.0 ? hue + 360.0 : hue;

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0.0;
        }

        double rank = p / 100.0 * (sorted.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        return lo == hi ? sorted[lo] : sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
    }

    /// <summary>
    /// The two formulas above are restatements of private code, so they are held against the engine itself
    /// before any of them is believed: the first frame an engine sees is unsmoothed by construction
    /// (<c>alpha = 1</c> when not yet seeded), so its palette is the mapping's target exactly. The comparison
    /// is on the sRGB bytes and not on a hue read back out of them, because that round trip is lossy - see
    /// <see cref="HueQuantisationAtTheDarkThemesOperatingPoint"/>, which is the reason.
    /// </summary>
    [Fact]
    public void MappingRestatementMatchesTheEngine()
    {
        const float harmonic = 0.8f;
        double saturation = Math.Clamp(0.20 + (0.55 * harmonic), 0.0, 1.0);

        foreach (float centroid in new[] { 60f, 200f, 800f, 2000f, 5000f, 11000f })
        {
            foreach (float rms in new[] { 0.002f, 0.01f, 0.05f, 0.2f, 0.5f })
            {
                ReactiveThemeEngine engine = new(true, new ReactiveThemeOptions(true, 0.15f));
                ReactiveThemePalette palette =
                    engine.Advance(new AnalysisFrame(1, 0, 0, NoSpectrum, NoSpectrum, rms, rms, centroid,
                                                     harmonic, NoSpectrum, false, 0),
                                   TimeSpan.FromSeconds(1.0 / ThemeHz));

                Srgb expected = ReactiveContrast.Constrain(
                    ReactiveTheming.FromHsl(MappedHue(centroid), saturation, MappedLightness(rms)),
                    ReactiveTheming.DarkForegrounds);

                Assert.Equal(expected, palette.Primary);
            }
        }
    }

    /// <summary>
    /// How finely the dark theme can even express a hue change, which bounds how much reaction is visible
    /// whatever the mapping's range is. The theme paints at lightness 0.09 to 0.25, and at that end of sRGB the
    /// three channels are small integers, so a continuous hue lands on a coarse grid of eight-bit triples. This
    /// records the grid rather than asserting a number, because it is a property of sRGB and not of our code.
    /// </summary>
    [Fact]
    public void HueQuantisationAtTheDarkThemesOperatingPoint()
    {
        foreach (double lightness in new[] { 0.09, 0.13, 0.17, 0.21, 0.25 })
        {
            HashSet<Srgb> distinct = [];
            double worstGap = 0.0;
            double runStart = 0.0;
            Srgb previous = ReactiveTheming.FromHsl(0.0, 0.64, lightness);
            for (int deg = 0; deg <= 3600; deg++)
            {
                double hue = deg / 10.0 % 360.0;
                Srgb colour = ReactiveTheming.FromHsl(hue, 0.64, lightness);
                distinct.Add(colour);
                if (!colour.Equals(previous))
                {
                    worstGap = Math.Max(worstGap, (deg / 10.0) - runStart);
                    runStart = deg / 10.0;
                    previous = colour;
                }
            }

            output.WriteLine($"L={lightness:F2}: {distinct.Count} distinct colours over 360 deg, " +
                             $"widest step {worstGap:F1} deg, mean {360.0 / distinct.Count:F2} deg");
            Assert.True(distinct.Count > 1, "the hue sweep must produce more than one colour");
        }
    }

    /// <summary>
    /// Does nothing at all without <c>TUNQIO_EXCURSION_OUT</c>, which is how it stays out of the way of the
    /// ordinary suite: there is no skip attribute in this project's xunit and a measurement that needs a music
    /// library is not a claim CI can check.
    /// </summary>
    [Fact]
    public void LibraryExcursion()
    {
        string? dir = TraceDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            output.WriteLine("TUNQIO_EXCURSION_OUT unset: nothing to measure.");
            return;
        }

        string[] manifest = File.ReadAllLines(Path.Combine(dir!, "manifest.csv"));
        using StreamWriter report = new(Path.Combine(dir!, "per-track.csv"));
        report.WriteLine("index,name,hops,centroidP5,centroidP50,centroidP95,rmsDbP5,rmsDbP50,rmsDbP95," +
                         "harmonicP50,mapHueP5,mapHueP50,mapHueP95,mapHueSpan,mapLightSpan," +
                         "engHueSpan,engHueP5,engHueP95,engLightSpan,engLightP5,engLightP95,engSatSpan," +
                         "engHueIqr,engLightIqr,pinnedFloorPct,pinnedCeilPct,smHueSpan,smLightSpan,maxHueDrift," +
                         "hueRateP50,hueRateP95,hueRateMax,lightRateP50,lightRateP95");

        for (int row = 1; row < manifest.Length; row++)
        {
            string[] cells = SplitCsv(manifest[row]);
            int index = int.Parse(cells[0], CultureInfo.InvariantCulture);
            int hops = int.Parse(cells[1], CultureInfo.InvariantCulture);
            string name = Path.GetFileNameWithoutExtension(cells[6]);
            if (hops == 0)
            {
                continue;
            }

            string bin = Path.Combine(dir!, $"trace-{index:D3}.bin");
            if (!File.Exists(bin))
            {
                continue;
            }

            byte[] raw = File.ReadAllBytes(bin);
            int count = raw.Length / 12;

            List<double> centroids = new(count);
            List<double> rmsDb = new(count);
            List<double> harmonics = new(count);
            List<double> mapHues = new(count);
            List<double> mapLights = new(count);
            List<double> engHues = new();
            List<double> engLights = new();
            List<double> engSats = new();

            List<double> smHues = new();
            List<double> smLights = new();

            // Rate, not only range. A track that drifts thirty degrees over three minutes and one that swings
            // thirty degrees a chorus have the same span and look nothing alike, and "it does not react" is a
            // complaint about the second quantity. Degrees per second of the painted hue, and lightness units
            // per second of the painted lightness, both at the 30 Hz the theming runs at.
            List<double> hueRate = new();
            List<double> lightRate = new();
            double previousHue = double.NaN;
            double previousLight = double.NaN;
            double maxHueDrift = 0.0;

            ReactiveThemeEngine engine = new(true, new ReactiveThemeOptions(true, 0.15f));
            TimeSpan step = TimeSpan.FromSeconds(1.0 / ThemeHz);

            // The same exponential the engine applies, but on the unquantised numbers, so that the excursion
            // reported is not the eight-bit grid's. Seeded on the first frame exactly as Advance seeds itself.
            double alpha = engine.Alpha(step);
            double hx = 0.0;
            double hy = 0.0;
            double smEnergy = 0.0;
            bool seeded = false;

            // The theming samples the analysis stream at 30 Hz while it publishes at 93.75, so one hop in
            // every 3.125 is the one the engine is handed. Stepping the hop index by that ratio reproduces
            // which frames it sees rather than pretending it sees them all.
            double perTheme = HopHz / ThemeHz;
            double next = 0.0;
            for (int h = 0; h < count; h++)
            {
                float rms = BitConverter.ToSingle(raw, (h * 12) + 0);
                float centroid = BitConverter.ToSingle(raw, (h * 12) + 4);
                float harmonic = BitConverter.ToSingle(raw, (h * 12) + 8);

                centroids.Add(centroid);
                rmsDb.Add(20.0 * Math.Log10(Math.Max(rms, 1e-5f)));
                harmonics.Add(harmonic);
                mapHues.Add(MappedHue(centroid));
                mapLights.Add(MappedLightness(rms));

                if (h < next)
                {
                    continue;
                }

                next += perTheme;
                ReactiveThemePalette palette =
                    engine.Advance(new AnalysisFrame((uint)h, 0, 0, NoSpectrum, NoSpectrum, rms, rms, centroid,
                                                     harmonic, NoSpectrum, false, 0),
                                   step);
                (double hue, double sat, double light) = ReactiveTheming.ToHsl(palette.Primary);
                engHues.Add(Unwrap(hue));
                engLights.Add(light);
                engSats.Add(sat);

                double a = seeded ? alpha : 1.0;
                seeded = true;
                double target = MappedHue(centroid) * Math.PI / 180.0;
                hx += a * (Math.Cos(target) - hx);
                hy += a * (Math.Sin(target) - hy);
                double smHue = (Math.Atan2(hy, hx) * 180.0 / Math.PI % 360.0 + 360.0) % 360.0;
                smHues.Add(Unwrap(smHue));

                double targetLight = MappedLightness(rms);
                smEnergy += a * ((targetLight - 0.09) / 0.16 - smEnergy);
                smLights.Add(0.09 + (smEnergy * 0.16));

                // The model and the painted colour must agree to within the eight-bit grid, or the model is
                // wrong and every number derived from it is too.
                double drift = Math.Abs(smHue - hue) % 360.0;
                maxHueDrift = Math.Max(maxHueDrift, Math.Min(drift, 360.0 - drift));

                if (!double.IsNaN(previousHue))
                {
                    double step2 = Math.Abs(smHue - previousHue) % 360.0;
                    hueRate.Add(Math.Min(step2, 360.0 - step2) * ThemeHz);
                    lightRate.Add(Math.Abs(smLights[^1] - previousLight) * ThemeHz);
                }

                previousHue = smHue;
                previousLight = smLights[^1];
            }

            centroids.Sort();
            rmsDb.Sort();
            harmonics.Sort();
            mapHues.Sort();
            mapLights.Sort();
            engHues.Sort();
            engLights.Sort();
            engSats.Sort();
            smHues.Sort();
            smLights.Sort();
            hueRate.Sort();
            lightRate.Sort();

            // How much of the time the energy axis is against a stop, which is a different complaint from a
            // narrow range: a track pinned at the floor is not using the span at all.
            double floorPct = mapLights.Count(v => v <= 0.09 + 1e-9) * 100.0 / mapLights.Count;
            double ceilPct = mapLights.Count(v => v >= 0.25 - 1e-9) * 100.0 / mapLights.Count;

            report.WriteLine(string.Join(',', new[]
            {
                index.ToString(CultureInfo.InvariantCulture),
                "\"" + name.Replace("\"", "'", StringComparison.Ordinal) + "\"",
                count.ToString(CultureInfo.InvariantCulture),
                F(Percentile(centroids, 5)), F(Percentile(centroids, 50)), F(Percentile(centroids, 95)),
                F(Percentile(rmsDb, 5)), F(Percentile(rmsDb, 50)), F(Percentile(rmsDb, 95)),
                F(Percentile(harmonics, 50)),
                F(Percentile(mapHues, 5)), F(Percentile(mapHues, 50)), F(Percentile(mapHues, 95)),
                F(Percentile(mapHues, 95) - Percentile(mapHues, 5)),
                F(Percentile(mapLights, 95) - Percentile(mapLights, 5)),
                F(Percentile(engHues, 95) - Percentile(engHues, 5)),
                F(Percentile(engHues, 5)), F(Percentile(engHues, 95)),
                F(Percentile(engLights, 95) - Percentile(engLights, 5)),
                F(Percentile(engLights, 5)), F(Percentile(engLights, 95)),
                F(Percentile(engSats, 95) - Percentile(engSats, 5)),
                F(Percentile(engHues, 75) - Percentile(engHues, 25)),
                F(Percentile(engLights, 75) - Percentile(engLights, 25)),
                F(floorPct), F(ceilPct),
                F(Percentile(smHues, 95) - Percentile(smHues, 5)),
                F(Percentile(smLights, 95) - Percentile(smLights, 5)),
                F(maxHueDrift),
                F(Percentile(hueRate, 50)), F(Percentile(hueRate, 95)), F(Percentile(hueRate, 100)),
                F(Percentile(lightRate, 50)), F(Percentile(lightRate, 95)),
            }));
        }

        report.Flush();
        output.WriteLine($"wrote {Path.Combine(dir!, "per-track.csv")}");
    }

    /// <summary>
    /// What a wider sweep, a narrower input range or less smoothing would actually be worth, measured on the
    /// same traces rather than extrapolated from the one number. The three levers are independent and they do
    /// not cost the same: the sweep spends hue the palette has to share with the album art blend, the input
    /// range spends nothing but headroom the library never visits, and the smoothing spends the accessibility
    /// margin the 0.5 s floor exists to protect.
    /// </summary>
    [Fact]
    public void WideningSensitivity()
    {
        string? dir = TraceDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            output.WriteLine("TUNQIO_EXCURSION_OUT unset: nothing to measure.");
            return;
        }

        (string Label, double Low, double High, double Sweep, float Smoothing)[] variants =
        [
            ("shipped   50Hz-12kHz  120deg  s=0.15", 50.0, 12000.0, 120.0, 0.15f),
            ("sweep 180 50Hz-12kHz  180deg  s=0.15", 50.0, 12000.0, 180.0, 0.15f),
            ("sweep 240 50Hz-12kHz  240deg  s=0.15", 50.0, 12000.0, 240.0, 0.15f),
            ("range     200Hz-8kHz  120deg  s=0.15", 200.0, 8000.0, 120.0, 0.15f),
            ("range     500Hz-6kHz  120deg  s=0.15", 500.0, 6000.0, 120.0, 0.15f),
            ("both      500Hz-6kHz  200deg  s=0.15", 500.0, 6000.0, 200.0, 0.15f),
            ("shipped   50Hz-12kHz  120deg  s=0.00", 50.0, 12000.0, 120.0, 0.00f),
            ("both      500Hz-6kHz  200deg  s=0.00", 500.0, 6000.0, 200.0, 0.00f),
        ];

        string[] manifest = File.ReadAllLines(Path.Combine(dir!, "manifest.csv"));
        output.WriteLine("median painted hue span (p5..p95) over the library, degrees:");
        foreach ((string label, double low, double high, double sweep, float smoothing) in variants)
        {
            List<double> spans = [];
            for (int row = 1; row < manifest.Length; row++)
            {
                string[] cells = SplitCsv(manifest[row]);
                int index = int.Parse(cells[0], CultureInfo.InvariantCulture);
                string bin = Path.Combine(dir!, $"trace-{index:D3}.bin");
                if (!File.Exists(bin))
                {
                    continue;
                }

                byte[] raw = File.ReadAllBytes(bin);
                int count = raw.Length / 12;
                double alpha =
                    new ReactiveThemeEngine(true, new ReactiveThemeOptions(true, smoothing))
                        .Alpha(TimeSpan.FromSeconds(1.0 / ThemeHz));

                List<double> hues = new();
                double hx = 0.0;
                double hy = 0.0;
                bool seeded = false;
                double next = 0.0;
                double perTheme = HopHz / ThemeHz;
                for (int h = 0; h < count; h++)
                {
                    if (h < next)
                    {
                        continue;
                    }

                    next += perTheme;
                    double centroid = Math.Clamp(BitConverter.ToSingle(raw, (h * 12) + 4), low, high);
                    double target = (265.0 + (Math.Log(centroid / low) / Math.Log(high / low) * sweep)) * Math.PI
                                    / 180.0;
                    double a = seeded ? alpha : 1.0;
                    seeded = true;
                    hx += a * (Math.Cos(target) - hx);
                    hy += a * (Math.Sin(target) - hy);
                    hues.Add(Unwrap((Math.Atan2(hy, hx) * 180.0 / Math.PI % 360.0 + 360.0) % 360.0));
                }

                if (hues.Count == 0)
                {
                    continue;
                }

                hues.Sort();
                spans.Add(Percentile(hues, 95) - Percentile(hues, 5));
            }

            spans.Sort();
            output.WriteLine($"  {label}  median {Percentile(spans, 50),6:F1}  p10 {Percentile(spans, 10),6:F1}" +
                             $"  p90 {Percentile(spans, 90),6:F1}");
        }
    }

    private static string F(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

    /// <summary>Enough CSV for a manifest this harness wrote itself: quoted fields, no embedded quotes.</summary>
    private static string[] SplitCsv(string line)
    {
        List<string> cells = [];
        bool quoted = false;
        System.Text.StringBuilder cell = new();
        foreach (char c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return [.. cells];
    }
}
