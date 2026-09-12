// Radial Spectrum (E4-S5). The polar sibling of spectrum-bars: one instanced wedge per ray, radiating from a
// hub, its length taken from the same logarithmic slice of the 1024-bin spectrum.
//
// Three properties of this shader are deliberate and worth keeping:
//
// 1. DETERMINISM. When something is playing (timing.w, the analysis sequence, is non-zero) nothing here reads
//    `timing`. The picture is a pure function of the analysis frame and the parameters, which is what makes the
//    golden-image test a test rather than a screenshot. The idle animation - the only thing that uses the clock -
//    is behind the timing.w == 0 branch, where there is no spectrum to draw.
//
// 2. FLASH SAFETY, and it is the mirror image of the one spectrum-bars has rather than a copy of it. Here
//    brightness falls with *screen radius*, so the bright part of the picture is a small disc around the hub
//    whose size does not depend on the audio at all; what the audio changes is how far the dim outer part of
//    each ray reaches. That bounds the flashing area by geometry: measured between digital silence and full
//    scale in every bin, 1.72% of the field, against the 25% the accessibility contract allows
//    (docs/ui-screens-and-flows.md). `measure_flash` in test_preset_golden.cpp measures it rather than this
//    comment asserting it.
//
// 3. IT STAYS CIRCULAR. The wedge is built in polar coordinates and then fitted to the *minor* screen dimension,
//    so a 21:9 window draws the same circle a 4:3 one does with more background around it, rather than an
//    ellipse. That is what `fit` below is; viewport.xy are the pixel dimensions.
cbuffer Frame : register(b0) {
    float4 viewport;
    float4 timing;
    float4 level;
    float4 counts;
    float4 bands[3];
    float4 params[4];
};
Buffer<float> Spectrum : register(t0);
Buffer<float> Waveform : register(t1);

// The parameters preset.json declares, in its order: parameter i is params[i / 4][i % 4].
#define P_RAYS params[0].x
#define P_SMOOTHING params[0].y
#define P_COLOUR params[0].z
#define P_GAIN params[0].w
#define P_HUB params[1].x

static const uint MaxRays = 128u;   // must equal "instance_count" in preset.json
static const uint MaxScan = 128u;   // bins one ray may average over, so the loop has a bound the compiler can see
static const float Duty = 0.62;     // of a ray's angular slot that is ray; the rest is gap
static const float OuterEdge = 0.94; // of the minor half-dimension a full-scale ray reaches
static const float TwoPi = 6.28318530718;

struct VSOut {
    float4 pos : SV_Position;
    float3 tint : COLOR0;
    float2 shape : TEXCOORD0; // x = radial position in [0, 1] from the hub to OuterEdge, y = this ray's own length
};

// Three stops, low frequency to high. Distinct from spectrum-bars' azure-aqua-rose on purpose - two presets that
// differ only in geometry are one preset - and, like it, kept off pure white: relative luminance is what the
// flash contract is about.
float3 ramp(float u) {
    const float3 a = float3(0.35, 0.22, 0.90); // violet
    const float3 b = float3(0.85, 0.30, 0.70); // magenta
    const float3 c = float3(0.98, 0.72, 0.30); // gold
    return u < 0.5 ? lerp(a, b, saturate(u * 2.0)) : lerp(b, c, saturate((u - 0.5) * 2.0));
}

// Ray i covers a logarithmic slice of the spectrum, so the bass is not one bin wide and the treble four hundred.
// This is spectrum-bars' bar_magnitude unchanged, and deliberately so: the two presets differ in where they put
// a magnitude, not in how they read one, and a reader comparing them should find only the geometry different.
//
// `smoothing` is spectral, not temporal: it widens the slice into its neighbours and slides the reading from the
// slice's peak toward its mean. A shader has no memory between frames - the constant-buffer contract carries no
// state and the preset declares no resources of its own - so an attack/decay envelope is not something this
// preset can do; that would need the renderer to smooth before it uploads. See docs/visualization-engine.md.
float ray_magnitude(uint i, float rays, float smoothing) {
    const float bins = max(counts.z, 4.0);
    const float centre = ((float)i + 0.5) / rays;
    const float half_slice = 0.5 * (1.0 + smoothing * 1.5) / rays;
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
    // Magnitudes are linear and a mix sits far below full scale; a decade of range reads as a ray.
    return saturate(1.0 + log10(max(mag, 1e-5)) / 5.0);
}

// Where this ray sits on the ramp. The `colour` parameter is the colour *source*, rounded to a mode, and the
// three modes are the ones spectrum-bars and waveform already have.
float colour_coord(uint i, float rays) {
    const uint mode = (uint)(clamp(P_COLOUR, 0.0, 2.0) + 0.5);
    if (mode == 1u) {
        // Loudness: the whole wheel moves along the ramp together with the peak level.
        return saturate(level.y);
    }
    if (mode == 2u) {
        // Spectral centroid, log-mapped over 50 Hz - 12 kHz, so the colour follows the brightness of the sound.
        return saturate(log2(max(level.z, 50.0) / 50.0) / log2(12000.0 / 50.0));
    }
    // Spectrum position: ray index around the ramp.
    return (float)i / max(rays - 1.0, 1.0);
}

VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID) {
    VSOut o;
    o.pos = float4(0.0, 0.0, 0.0, 1.0);
    o.tint = float3(0.0, 0.0, 0.0);
    o.shape = float2(0.0, 0.0);

    const float rays = clamp(floor(P_RAYS + 0.5), 12.0, (float)MaxRays);
    if ((float)iid >= rays) {
        return o; // past the ray count: all six vertices coincide, so nothing rasterises
    }

    float h;
    if (timing.w > 0.0) {
        h = saturate(ray_magnitude(iid, rays, P_SMOOTHING) * P_GAIN);
    } else {
        // Nothing has ever played: a slow rotating swell, so an idle panel is not a black rectangle. 0.35 Hz, an
        // order below the 3 Hz the accessibility contract cares about, and the only use of the clock here.
        h = saturate((0.38 + 0.26 * sin(timing.x * 2.2 + (float)iid * TwoPi / rays * 3.0)) * P_GAIN);
    }

    const float hub = clamp(P_HUB, 0.05, 0.5);
    const float slot = TwoPi / rays;
    // Ray 0 (the bass) points straight up and the wheel runs anticlockwise from there. Anticlockwise is not a
    // taste: the winding below is fixed, so it is the right one only while the angle increases with the instance.
    const float a0 = 1.57079632679 + (float)iid * slot + slot * (1.0 - Duty) * 0.5;
    const float a1 = a0 + slot * Duty;
    const float r0 = hub;
    const float r1 = hub + (OuterEdge - hub) * h;

    // NDC spans [-1, 1] on both axes whatever the window's shape is, so a circle drawn in it is an ellipse on
    // screen. Fitting to the minor dimension is what keeps it a circle.
    const float aspect = max(viewport.x, 1.0) / max(viewport.y, 1.0);
    const float2 fit = aspect >= 1.0 ? float2(1.0 / aspect, 1.0) : float2(1.0, aspect);

    const float2 i0 = float2(cos(a0), sin(a0));
    const float2 i1 = float2(cos(a1), sin(a1));
    // Wound the way the default rasteriser state wants it - the renderer sets none, so back faces are culled and
    // the other order draws nothing at all, which is exactly what this preset did the first time it ran. Note
    // that it is NOT spectrum-bars' order with angle for x and radius for y: the Jacobian of (angle, radius) ->
    // (r cos a, r sin a) is -r, so that map REVERSES orientation and the two triangles have to be wound the
    // other way round to come out the same way on screen.
    const float2 corners[6] = {i0 * r0 * fit, i1 * r0 * fit, i0 * r1 * fit,
                               i0 * r1 * fit, i1 * r0 * fit, i1 * r1 * fit};
    // The radial coordinate at each of those six, normalised so 1.0 is as far as any ray can reach. The pixel
    // shader needs it as a fraction of the whole field rather than of this ray, which is what makes brightness a
    // function of screen radius and not of position along the ray.
    const float reach = max(OuterEdge - hub, 1e-4);
    const float u1 = (r1 - hub) / reach;
    const float radial[6] = {0.0, 0.0, u1, u1, 0.0, u1};

    o.pos = float4(corners[vid], 0.0, 1.0);
    o.tint = ramp(colour_coord(iid, rays));
    o.shape = float2(radial[vid], max(u1, 1e-4));
    return o;
}

float4 PSMain(VSOut i) : SV_Target {
    const float t = saturate(i.shape.x);
    // Cubic in screen radius: at a third of the way out a ray is a fifth of its hub brightness, so everything
    // beyond a small disc around the centre stays near the clear colour whatever the audio does. This is the
    // flash-safety property, not a style choice - it is what bounds the flashing area by geometry rather than by
    // how loud the music happens to be.
    const float falloff = 1.0 - t;
    const float body = 0.10 + 0.90 * falloff * falloff * falloff;
    // A narrow highlight riding the ray's own end. It does track the audio, which is why it is narrow.
    const float tip = smoothstep(0.94, 1.0, t / i.shape.y);
    const float3 rgb = i.tint * body + float3(0.95, 0.88, 1.0) * tip * 0.35;
    return float4(saturate(rgb), 1.0);
}
