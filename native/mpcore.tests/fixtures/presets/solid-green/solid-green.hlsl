// A fixture, not a visualization: one full-screen triangle in a colour a pixel readback can name exactly.
// `level` drives the green channel, so mp_renderer_set_param is testable by looking at the picture.
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

struct VSOut { float4 pos : SV_Position; };

VSOut VSMain(uint vid : SV_VertexID) {
    float2 corners[3] = { float2(-1.0, -3.0), float2(-1.0, 1.0), float2(3.0, 1.0) };
    VSOut o;
    o.pos = float4(corners[vid], 0.0, 1.0);
    return o;
}

float4 PSMain(VSOut i) : SV_Target {
    return float4(0.0, params[0].x, 0.0, 1.0);
}
