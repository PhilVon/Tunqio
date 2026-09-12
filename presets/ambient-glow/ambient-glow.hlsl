// Ambient Glow (E4-S5, AC-124). Three soft lobes of light over the whole field, one per octave-band group,
// coloured from the album art palette when there is one. A background wash rather than a graphic: the thing it
// is for is a Now Playing panel you are not looking directly at.
//
// ---- why this is one fullscreen quad and not three ------------------------------------------------
//
// The renderer sets no blend state, so overlapping geometry OVERWRITES rather than adds - which is why the
// waveform's soft edge is a lerp toward its clear colour and not a fade to transparent. Three quads would
// occlude each other and a wash has to be additive to be a wash, so instance_count is 1, the vertex shader emits
// one screen-covering quad and the whole picture is the pixel shader's arithmetic. That also means this preset's
// cost is per-pixel and not per-primitive; the 1080p figure in test_preset_golden.cpp is the one that matters.
//
// ---- the palette, and why it is a parameter -------------------------------------------------------
//
// AC-124 is "uses palette colours from album art when available". The palette is E3-S7's: five colours with
// population and luminance, extracted by median cut and stored as palette.json beside the cached image, reached
// through IArtCache.LoadPaletteAsync. It arrives here through the *existing* mp_renderer_set_param as
// art_primary, art_secondary and art_accent - one sRGB colour packed into one float as r*65536 + g*256 + b,
// which is exact in float32 (every integer below 2^24 is) so the unpack below is bit-reproducible and the golden
// image stays a golden image. A negative value means "no art", which is the default: a preset nobody has told
// about a picture draws its own ramp, and that is what "when available" means in code rather than in a comment.
//
// One parameter per colour rather than three channels each, for a reason: renderer::param_values_ is an array of
// independent relaxed atomics that the render thread reads once a frame, so three channel parameters set in
// sequence can be read half-applied and show a wrong colour for one frame. A packed colour is one atomic store.
//
// This is deliberately NOT mp_renderer_set_theme, which is a stub reserved for E4-S6. That is a renderer-wide
// theme - four colours reaching every preset and the shell's own Composition gradient, polled at 30 Hz with EMA
// smoothing and a contrast guarantee - and it needs a field in b0, which is a schema bump. What AC-124 asks for
// is one preset's colour SOURCE, which this contract already expresses.
//
// ---- flash safety ---------------------------------------------------------------------------------
//
// This preset covers the whole field, so the usual argument - "only a small part of the picture can change" -
// is not available to it and it uses the other one: NO PIXEL CAN EVER GET BRIGHT ENOUGH TO FLASH. The wash is
// saturated to 1.0 per channel and then scaled by PeakGlow, so the brightest colour this shader can emit is
// (0.30, 0.302, 0.315) including the background - measured relative luminance 0.0742, against the 0.10 WCAG 2.3.1
// counts as a flash. The flashing area is therefore zero by construction whatever the audio does and whatever the
// album art is, and `measure_flash` in test_preset_golden.cpp measures the peak change as well as the area so the
// zero is a reading and not an absence. It is also why `glow` tops out at 1.0 and only attenuates: a parameter
// must not be able to push a preset past the accessibility contract.
//
// DETERMINISM, as for the other three presets: with something playing (timing.w non-zero) nothing here reads
// `timing`. The idle animation is behind the timing.w == 0 branch.
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
#define P_SPREAD params[0].x
#define P_SMOOTHING params[0].y
#define P_COLOUR params[0].z
#define P_GAIN params[0].w
#define P_GLOW params[1].x
#define P_ART_PRIMARY params[1].y
#define P_ART_SECONDARY params[1].z
#define P_ART_ACCENT params[1].w

// Must match "clear" in preset.json: there is no blending, so the wash is added to this rather than over it.
static const float3 Background = float3(0.02, 0.022, 0.035);
// The ceiling on the wash, and the flash-safety constant. See the header: changing it is changing an
// accessibility property, and the test that measures it will say so.
static const float PeakGlow = 0.28;
// An art colour is renormalised to this maximum channel, which is what makes the flash bound independent of the
// album art: a white sleeve cannot make this preset brighter than a navy one, only a different hue.
static const float ArtLevel = 0.85;
// ...and a colour darker than this is not a glow at any scaling, so the built-in ramp stop is used instead. A
// black-and-white sleeve quantises to near-black entries, and "art with no usable palette" has to draw something.
static const float ArtFloor = 0.08;

struct VSOut {
    float4 pos : SV_Position;
    float2 field : TEXCOORD0; // the field in units of half its height, x widened by the aspect so lobes are round
};

// One octave band. bands[3] carries the ten in .x .y .z .w order, so band k is bands[k / 4][k % 4]; a float4
// has no dynamic component index, hence the select chain.
float band_at(uint k) {
    const float4 v = bands[k / 4u];
    const uint c = k % 4u;
    return c == 0u ? v.x : (c == 1u ? v.y : (c == 2u ? v.z : v.w));
}

// The energy of one run of octave bands, 0..1. `smoothing` slides the reading from the run's peak toward its
// mean, which is the same meaning it has in spectrum-bars and radial-spectrum - spectral, not temporal, because
// a preset has no state between frames. See docs/visualization-engine.md.
float group_energy(uint first, uint last, float smoothing) {
    float peak = 0.0;
    float sum = 0.0;
    float n = 0.0;
    for (uint k = first; k <= last; ++k) {
        const float v = band_at(k);
        peak = max(peak, v);
        sum += v;
        n += 1.0;
    }
    const float mean = n > 0.0 ? sum / n : 0.0;
    const float mag = lerp(peak, mean, saturate(smoothing));
    // The same decade mapping the spectrum presets use, so a mix reads as light rather than as a dark field.
    return saturate(1.0 + log10(max(mag, 1e-5)) / 5.0);
}

// The built-in ramp stops, used when there is no art and as the fallback for an art colour too dark to glow.
float3 default_stop(uint i) {
    if (i == 0u) {
        return float3(0.22, 0.40, 0.92); // deep blue
    }
    if (i == 1u) {
        return float3(0.28, 0.76, 0.74); // teal
    }
    return float3(0.92, 0.52, 0.36); // ember
}

// r*65536 + g*256 + b, in 0..255 per channel, back to 0..1 floats. Every step is exact: the value is an integer
// below 2^24, and the divisions are by powers of two.
float3 unpack_srgb(float packed) {
    const float r = floor(packed / 65536.0);
    const float rest = packed - r * 65536.0;
    const float g = floor(rest / 256.0);
    const float b = rest - g * 256.0;
    return float3(r, g, b) / 255.0;
}

// One palette colour, or the built-in stop when there is no art or the art colour cannot be a glow.
float3 art_or(float packed, uint fallback_index) {
    if (packed < 0.0) {
        return default_stop(fallback_index); // no art: the default, and the default of every parameter
    }
    const float3 c = unpack_srgb(packed);
    const float m = max(c.r, max(c.g, c.b));
    if (m < ArtFloor) {
        return default_stop(fallback_index); // art with no usable palette
    }
    return c * (ArtLevel / m);
}

// The three stops as a ramp, so the colour *source* modes below have something continuous to sit on.
float3 stops(float u) {
    const float3 a = art_or(P_ART_PRIMARY, 0u);
    const float3 b = art_or(P_ART_SECONDARY, 1u);
    const float3 c = art_or(P_ART_ACCENT, 2u);
    return u < 0.5 ? lerp(a, b, saturate(u * 2.0)) : lerp(b, c, saturate((u - 0.5) * 2.0));
}

// Where a lobe sits on that ramp. The `colour` parameter is the colour source, rounded to a mode, and the three
// modes are the ones the other presets have. `own` is the lobe's own place in the ramp, used by mode 0.
float colour_coord(float own) {
    const uint mode = (uint)(clamp(P_COLOUR, 0.0, 2.0) + 0.5);
    if (mode == 1u) {
        return saturate(level.y); // loudness: the whole field moves along the ramp together
    }
    if (mode == 2u) {
        // Spectral centroid, log-mapped over 50 Hz - 12 kHz, so the colour follows the brightness of the sound.
        return saturate(log2(max(level.z, 50.0) / 50.0) / log2(12000.0 / 50.0));
    }
    return own; // band position: bass, middle and treble each take their own stop
}

// 1 / (1 + d^4): smooth, bounded, and cheap enough to run once per pixel three times at 1080p on WARP.
float lobe(float2 p, float2 centre, float radius) {
    const float2 d = (p - centre) / max(radius, 1e-3);
    const float q = dot(d, d);
    return 1.0 / (1.0 + q * q);
}

VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID) {
    // One screen-covering quad, wound the way the default rasteriser state wants it - the renderer sets none, so
    // back faces are culled and the other order draws nothing at all.
    const float2 corners[6] = {float2(-1.0, -1.0), float2(-1.0, 1.0), float2(1.0, -1.0),
                               float2(1.0, -1.0),  float2(-1.0, 1.0), float2(1.0, 1.0)};
    const float2 p = corners[vid];
    const float aspect = max(viewport.x, 1.0) / max(viewport.y, 1.0);

    VSOut o;
    o.pos = float4(p, 0.0, 1.0);
    // Widening x by the aspect measures distance in units of half the field's HEIGHT on both axes, so a lobe is
    // a circle on a 21:9 window as much as on a 4:3 one and the composition simply has more room.
    o.field = float2(p.x * aspect, p.y);
    return o;
}

float4 PSMain(VSOut i) : SV_Target {
    const float aspect = max(viewport.x, 1.0) / max(viewport.y, 1.0);
    const float spread = clamp(P_SPREAD, 0.25, 1.2);
    const float gain = P_GAIN;

    float3 energy;
    if (timing.w > 0.0) {
        // Ten octave bands in three groups: 31.5-125 Hz, 250 Hz-2 kHz, 4-16 kHz.
        energy = float3(group_energy(0u, 2u, P_SMOOTHING), group_energy(3u, 6u, P_SMOOTHING),
                        group_energy(7u, 9u, P_SMOOTHING));
    } else {
        // Nothing has ever played: three slow breaths at unrelated rates, so an idle panel is not a black
        // rectangle. The fastest is 0.36 Hz, an order below the 3 Hz the accessibility contract cares about, and
        // this is the only use of the clock in this shader.
        energy = float3(0.34 + 0.22 * sin(timing.x * 1.5), 0.30 + 0.20 * sin(timing.x * 2.3 + 1.7),
                        0.26 + 0.18 * sin(timing.x * 1.1 + 3.4));
    }
    energy = saturate(energy * gain);

    // Fixed lobe centres, spread across the field's width. Fixed because motion would have to come from the
    // clock and this shader does not read it while something is playing; what moves is the light, not the lamps.
    const float2 centres[3] = {float2(-0.52 * aspect, -0.28), float2(0.06 * aspect, 0.34),
                               float2(0.58 * aspect, -0.18)};
    const float own[3] = {0.0, 0.5, 1.0};

    float3 wash = float3(0.0, 0.0, 0.0);
    for (uint k = 0u; k < 3u; ++k) {
        const float e = energy[k];
        // A louder band is a bigger lamp as well as a brighter one, which is what makes the field breathe
        // instead of just changing level.
        const float radius = spread * (0.55 + 0.75 * e);
        wash += stops(colour_coord(own[k])) * (e * lobe(i.field, centres[k], radius));
    }

    // saturate BEFORE the scaling, so PeakGlow really is the ceiling on every channel: see the flash-safety note
    // at the top. clamp on P_GLOW because a parameter is clamped to its declared range and its range tops out at
    // 1.0 - this is belt and braces for a future manifest edit, not for a value the ABI can deliver.
    const float3 rgb = Background + saturate(wash) * (PeakGlow * clamp(P_GLOW, 0.0, 1.0));
    return float4(saturate(rgb), 1.0);
}
