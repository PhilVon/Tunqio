// A fixture, not a visualization: it paints b0's `theme` (schema 2, E4-S6) so a pixel readback can name every
// one of its sixteen floats. The screen is four columns, one per colour in mp_theme_colors' order, and each
// column is split: the top half carries that colour's RGB and the bottom half its alpha in all three channels,
// because an alpha nothing draws is an alpha nothing can prove arrived.
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

struct VSOut { float4 pos : SV_Position; };

VSOut VSMain(uint vid : SV_VertexID) {
    float2 corners[3] = { float2(-1.0, -3.0), float2(-1.0, 1.0), float2(3.0, 1.0) };
    VSOut o;
    o.pos = float4(corners[vid], 0.0, 1.0);
    return o;
}

float4 PSMain(VSOut i) : SV_Target {
    uint column = min((uint)(i.pos.x * 4.0 / max(viewport.x, 1.0)), 3u);
    float4 c = theme[column];
    float3 rgb = i.pos.y * 2.0 < viewport.y ? c.rgb : float3(c.a, c.a, c.a);
    return float4(rgb, 1.0);
}
