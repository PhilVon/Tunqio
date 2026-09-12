// Waveform (E4-S4). A ribbon through the 512-sample mono waveform: one instanced quad per segment, thickened
// along the segment's normal in pixels so the line keeps its weight at any window size or DPI.
//
// The same two properties as spectrum-bars, for the same reasons:
//
// 1. DETERMINISM. With something playing (timing.w non-zero) nothing here reads `timing`, so the picture is a
//    pure function of the analysis frame and the parameters and the golden image is reproducible. The idle
//    animation is behind the timing.w == 0 branch.
//
// 2. FLASH SAFETY. This one is structural: what is drawn is a line a few pixels thick. Even at full-scale
//    excursion it covers a small fraction of the field, so a full-field luminance change is not a shape this
//    preset can make. `measure_flash` in test_preset_golden.cpp measures it rather than assuming it: 1.1% of
//    the field changes between silence and full scale at the 640x360 that test uses, against the 25% the
//    contract allows. A thinner line on a taller field is a smaller fraction still, so 1080p is safer, not less.
cbuffer Frame : register(b0) {
    float4 viewport;
    float4 timing;
    float4 level;
    float4 counts;
    float4 bands[3];
    float4 params[4];
    float4 theme[4];
};
Buffer<float> Spectrum : register(t0);
Buffer<float> Waveform : register(t1);

// The parameters preset.json declares, in its order: parameter i is params[i / 4][i % 4].
#define P_POINTS params[0].x
#define P_SMOOTHING params[0].y
#define P_COLOUR params[0].z
#define P_GAIN params[0].w
#define P_THICKNESS params[1].x
#define P_THEME_MIX params[1].y

static const uint MaxPoints = 256u; // must equal "instance_count" in preset.json
static const uint MaxTaps = 48u;    // samples one point may average over, so the loop has a bound
static const float Height = 0.82;   // of half the field a full-scale excursion reaches
// Must match "clear" in preset.json: the ribbon has no alpha blending behind it, so its soft edge is a lerp
// toward the background rather than a fade to transparent.
static const float3 Background = float3(0.02, 0.022, 0.035);

struct VSOut {
    float4 pos : SV_Position;
    float3 tint : COLOR0;
    float2 shape : TEXCOORD0; // x = signed position across the ribbon in [-1, 1], y = |excursion| at this end
};

// ---- the renderer-wide theme (E4-S6's `theme`, wired to a picture by T-162) -----------------------
//
// theme[0], [1] and [2] are mp_theme_colors' primary, secondary and accent; theme[3] is the shell's own
// background, which this preset does not draw with because it has its own `clear`. mpcore.h: "until a theme is
// set every channel is zero, so alpha 0 is how a preset reads 'the shell has not told me one'" - that branch
// is what keeps this preset's golden image byte-identical on a renderer nobody has themed.
//
// WHAT THE THEME REPLACES is the ramp's three STOPS, and nothing else, so it stays orthogonal to `colour`,
// which chooses where ON the ramp to sample. Here that pairing is worth naming: this preset's mode 0 is
// EXCURSION, so with a theme the ribbon is theme-primary where it is near zero and theme-accent at its peaks -
// the shape of the meaning is unchanged and only the colours are the app's.
//
// BRIGHTNESS STAYS THIS PRESET'S: a theme colour is scaled so it is no more luminous than the stop it
// replaces. See the fuller note in spectrum-bars.hlsl for why that is what keeps the flash measurement an
// upper bound over every theme rather than a number that was true of one palette.
//
// Duplicated rather than shared, as ramp() and colour_coord() already are: D3DCompile is called with a null
// include handler (native/mpcore/src/render/preset.cpp), so a preset cannot #include anything.

// A theme colour dimmer than this is not a ramp stop at any scaling, so the preset's own stop is used. In
// luma2's units.
static const float ThemeFloor = 0.004;

// WCAG relative luminance approximated at gamma 2 rather than 2.4 - three multiplies rather than three pow()s.
// See spectrum-bars.hlsl for the measurement that chose it over weighting the gamma-encoded values directly.
float luma2(float3 c) {
    const float3 s = c * c;
    return 0.2126 * s.r + 0.7152 * s.g + 0.0722 * s.b;
}

// One ramp stop under the theme; the preset's own stop when there is no theme, when the theme colour is too
// dark to be a stop, or when theme_mix is 0.
float3 themed(float3 own, float4 t) {
    if (t.a <= 0.0) {
        return own; // the shell has not told this preset a theme
    }
    const float3 c = saturate(t.rgb);
    const float lt = luma2(c);
    if (lt < ThemeFloor) {
        return own;
    }
    // Scale DOWN only: luma2 scales as the square so sqrt lands on the stop's luminance, and min(1.0, ...)
    // makes it one-directional because a theme colour dimmer than the stop is always safe.
    const float3 fitted = c * min(1.0, sqrt(luma2(own) / lt));
    return lerp(own, fitted, saturate(P_THEME_MIX));
}

float3 ramp(float u) {
    const float3 a = themed(float3(0.16, 0.52, 0.95), theme[0]); // azure
    const float3 b = themed(float3(0.35, 0.90, 0.80), theme[1]); // aqua
    const float3 c = themed(float3(0.98, 0.62, 0.38), theme[2]); // amber
    return u < 0.5 ? lerp(a, b, saturate(u * 2.0)) : lerp(b, c, saturate((u - 0.5) * 2.0));
}

// The waveform at u in [0, 1], box-averaged. `smoothing` is the width of that box in samples: spatial, along the
// hop, not an envelope over time - a shader has no memory between frames. See the note in spectrum-bars.hlsl.
float sample_at(float u, float smoothing) {
    const float n = max(counts.w, 2.0);
    const float centre = saturate(u) * (n - 1.0);
    const float half_box = 0.5 + saturate(smoothing) * 10.0;
    const int first = (int)max(centre - half_box, 0.0);
    const int last = (int)min(centre + half_box, n - 1.0);

    float sum = 0.0;
    float taps = 0.0;
    for (int b = first; b <= last && b < first + (int)MaxTaps; ++b) {
        sum += Waveform.Load(b);
        taps += 1.0;
    }
    return taps > 0.0 ? sum / taps : 0.0;
}

float excursion(float u, float smoothing) {
    if (timing.w > 0.0) {
        return clamp(sample_at(u, smoothing) * P_GAIN, -1.0, 1.0);
    }
    // Nothing has ever played: a slow standing wave, so an idle panel is not a black rectangle. 0.29 Hz, an
    // order below the 3 Hz of the accessibility contract, and the only use of the clock here.
    return clamp(0.45 * sin(u * 12.0 + timing.x * 1.8) * cos(timing.x * 0.6) * P_GAIN, -1.0, 1.0);
}

// Where this point sits on the ramp. The `colour` parameter is the colour *source*, rounded to a mode.
float colour_coord(float u, float y) {
    const uint mode = (uint)(clamp(P_COLOUR, 0.0, 2.0) + 0.5);
    if (mode == 1u) {
        // Loudness: the whole ribbon moves along the ramp together with the peak level.
        return saturate(level.y);
    }
    if (mode == 2u) {
        // Spectral centroid, log-mapped over 50 Hz - 12 kHz, so the colour follows the brightness of the sound.
        return saturate(log2(max(level.z, 50.0) / 50.0) / log2(12000.0 / 50.0));
    }
    // Excursion: the ribbon is coolest where it is near zero and warmest at its peaks.
    return saturate(abs(y));
}

VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID) {
    VSOut o;
    o.pos = float4(0.0, 0.0, 0.0, 1.0);
    o.tint = float3(0.0, 0.0, 0.0);
    o.shape = float2(0.0, 0.0);

    const float points = clamp(floor(P_POINTS + 0.5), 16.0, (float)MaxPoints);
    if ((float)iid >= points) {
        return o; // past the point count: all six vertices coincide, so nothing rasterises
    }

    const float smoothing = P_SMOOTHING;
    const float u0 = (float)iid / points;
    const float u1 = ((float)iid + 1.0) / points;
    const float a0 = excursion(u0, smoothing);
    const float a1 = excursion(u1, smoothing);
    const float2 p0 = float2(u0 * 2.0 - 1.0, a0 * Height);
    const float2 p1 = float2(u1 * 2.0 - 1.0, a1 * Height);

    // Thicken along the segment's normal. The normal is taken in pixels and put back into NDC, so the ribbon is
    // the same weight on a wide window as a tall one; viewport.zw are 1/width and 1/height, and NDC spans 2.
    const float2 delta = float2((p1.x - p0.x) / max(viewport.z, 1e-6), (p1.y - p0.y) / max(viewport.w, 1e-6));
    const float2 direction = normalize(delta + float2(1e-5, 0.0));
    const float2 normal = float2(-direction.y, direction.x);
    const float2 offset = normal * max(P_THICKNESS, 1.0) * float2(viewport.z, viewport.w);

    // Wound the way the default rasteriser state wants it - the renderer sets none, so back faces are culled
    // and the other order draws nothing at all. `direction.x` is always positive here (u rises with the
    // instance), so `normal` always points the same way and one winding is right for every segment.
    const float2 corners[6] = {p0 - offset, p0 + offset, p1 - offset, p1 - offset, p0 + offset, p1 + offset};
    const float across[6] = {-1.0, 1.0, -1.0, -1.0, 1.0, 1.0};
    const float ends[6] = {a0, a0, a1, a1, a0, a1};

    o.pos = float4(corners[vid], 0.0, 1.0);
    o.tint = ramp(colour_coord(u0, ends[vid]));
    o.shape = float2(across[vid], abs(ends[vid]));
    return o;
}

float4 PSMain(VSOut i) : SV_Target {
    // No blend state is set on this pipeline, so the soft edge is a lerp toward the clear colour. Feathering the
    // last 55% of the half-width is what stops a two-pixel diagonal line looking like a staircase.
    const float core = 1.0 - smoothstep(0.45, 1.0, abs(i.shape.x));
    const float3 rgb = lerp(Background, i.tint, core);
    return float4(saturate(rgb), 1.0);
}
