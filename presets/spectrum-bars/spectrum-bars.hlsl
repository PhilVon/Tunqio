// Spectrum Bars (E4-S4). One instanced quad per bar over a logarithmic slice of the 1024-bin spectrum.
//
// Two properties of this shader are deliberate and worth keeping:
//
// 1. DETERMINISM. When something is playing (timing.w, the analysis sequence, is non-zero) nothing here reads
//    `timing`. The picture is a pure function of the analysis frame and the parameters, which is what makes the
//    golden-image test a test rather than a screenshot. The idle animation - the only thing that uses the clock -
//    is behind the timing.w == 0 branch, where there is no spectrum to draw.
//
// 2. FLASH SAFETY. Brightness is a function of *screen* height, not of position within the bar, so the lower
//    part of a bar is close to the clear colour whatever the audio does and only the top of the field can change
//    much between one frame and the next. That is what keeps the flashing area far below the 25% of the field
//    the accessibility contract allows (docs/ui-screens-and-flows.md), and the tip highlight - which does track
//    the audio - is a few per cent of the field. `measure_flash` in test_preset_golden.cpp measures it, rather
//    than this comment asserting it: 7.0% of the field between silence and full scale, against 25% allowed.
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
#define P_BARS params[0].x
#define P_SMOOTHING params[0].y
#define P_COLOUR params[0].z
#define P_GAIN params[0].w

static const uint MaxBars = 128u;  // must equal "instance_count" in preset.json
static const uint MaxScan = 128u;  // bins one bar may average over, so the loop has a bound the compiler can see
static const float Duty = 0.58;    // of a bar's slot that is bar; the rest is gap
static const float TopMargin = 0.94; // a full-scale bar stops short of the top edge rather than clipping into it

struct VSOut {
    float4 pos : SV_Position;
    float3 tint : COLOR0;
    float2 shape : TEXCOORD0; // x = height up the screen (0 bottom, 1 top), y = this bar's own height
};

// Three stops, low frequency to high. Kept off pure white on purpose: relative luminance is what the flash
// contract is about, and an aqua at full brightness is already the brightest thing this preset draws.
float3 ramp(float u) {
    const float3 a = float3(0.10, 0.38, 0.95); // azure
    const float3 b = float3(0.20, 0.78, 0.72); // aqua
    const float3 c = float3(0.95, 0.42, 0.52); // rose
    return u < 0.5 ? lerp(a, b, saturate(u * 2.0)) : lerp(b, c, saturate((u - 0.5) * 2.0));
}

// Bar i covers a logarithmic slice of the spectrum, so the bass is not one bin wide and the treble four hundred.
//
// `smoothing` is spectral, not temporal: it widens the slice into its neighbours and slides the reading from the
// slice's peak toward its mean. A shader has no memory between frames - the constant-buffer contract carries no
// state and the preset declares no resources of its own - so an attack/decay envelope is not something this
// preset can do; that would need the renderer to smooth before it uploads. See docs/visualization-engine.md.
float bar_magnitude(uint i, float bars, float smoothing) {
    const float bins = max(counts.z, 4.0);
    const float centre = ((float)i + 0.5) / bars;
    const float half_slice = 0.5 * (1.0 + smoothing * 1.5) / bars;
    const float lo = pow(bins, saturate(centre - half_slice));
    const float hi = pow(bins, saturate(centre + half_slice));
    const uint first = (uint)lo;
    const uint last = max((uint)hi, first + 1u);

    float peak = 0.0;
    float sum = 0.0;
    float n = 0.0;
    for (uint b = first; b < last && b < first + MaxScan; ++b) {
        const float m = Spectrum.Load((int)b);
        peak = max(peak, m);
        sum += m;
        n += 1.0;
    }
    const float mean = n > 0.0 ? sum / n : 0.0;
    const float mag = lerp(peak, mean, saturate(smoothing));
    // Magnitudes are linear and a mix sits far below full scale; a decade of range reads as a bar.
    return saturate(1.0 + log10(max(mag, 1e-5)) / 5.0);
}

// Where this bar sits on the ramp. The `colour` parameter is the colour *source*, rounded to a mode.
float colour_coord(uint i, float bars) {
    const uint mode = (uint)(clamp(P_COLOUR, 0.0, 2.0) + 0.5);
    if (mode == 1u) {
        // Loudness: the whole field moves along the ramp together with the peak level.
        return saturate(level.y);
    }
    if (mode == 2u) {
        // Spectral centroid, log-mapped over 50 Hz - 12 kHz, so the colour follows the brightness of the sound.
        return saturate(log2(max(level.z, 50.0) / 50.0) / log2(12000.0 / 50.0));
    }
    // Spectrum position: bar index across the ramp.
    return (float)i / max(bars - 1.0, 1.0);
}

VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID) {
    VSOut o;
    o.pos = float4(0.0, 0.0, 0.0, 1.0);
    o.tint = float3(0.0, 0.0, 0.0);
    o.shape = float2(0.0, 0.0);

    const float bars = clamp(floor(P_BARS + 0.5), 8.0, (float)MaxBars);
    if ((float)iid >= bars) {
        return o; // past the bar count: all six vertices coincide, so nothing rasterises
    }

    float h;
    if (timing.w > 0.0) {
        h = saturate(bar_magnitude(iid, bars, P_SMOOTHING) * P_GAIN);
    } else {
        // Nothing has ever played: a slow travelling swell, so an idle panel is not a black rectangle. 0.35 Hz,
        // an order below the 3 Hz the accessibility contract cares about, and the only use of the clock here.
        h = saturate((0.42 + 0.30 * sin(timing.x * 2.2 + (float)iid * 0.22)) * P_GAIN);
    }

    const float slot = 2.0 / bars;
    const float x0 = -1.0 + (float)iid * slot + slot * (1.0 - Duty) * 0.5;
    const float x1 = x0 + slot * Duty;
    const float y0 = -1.0;
    const float y1 = -1.0 + h * 2.0 * TopMargin;
    const float2 corners[6] = {float2(x0, y0), float2(x0, y1), float2(x1, y0),
                               float2(x1, y0), float2(x0, y1), float2(x1, y1)};

    const float2 p = corners[vid];
    o.pos = float4(p, 0.0, 1.0);
    o.tint = ramp(colour_coord(iid, bars));
    o.shape = float2((p.y + 1.0) * 0.5, max(h * TopMargin, 1e-4));
    return o;
}

float4 PSMain(VSOut i) : SV_Target {
    const float t = saturate(i.shape.x);
    // Cubic in screen height: at mid-height a bar is a sixth of its full brightness, so the bottom two thirds of
    // the field barely move whatever the audio does. This is the flash-safety property, not a style choice.
    const float body = 0.12 + 0.88 * t * t * t;
    // A narrow highlight riding the bar's own tip. It does track the audio, which is why it is narrow.
    const float tip = smoothstep(0.955, 1.0, t / i.shape.y);
    const float3 rgb = i.tint * body + float3(0.85, 0.92, 1.0) * tip * 0.5;
    return float4(saturate(rgb), 1.0);
}
