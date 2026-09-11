// Deliberately does not compile: the pixel shader calls something that was never declared, which D3DCompile
// reports as error X3004. AC-117 is about what the user sees when a preset is in this state, so this file is a
// fixture and must stay broken - if a future change makes it compile, the test that guards the fallback is dead.
cbuffer Frame : register(b0) {
    float4 viewport;
    float4 timing;
    float4 level;
    float4 counts;
    float4 bands[3];
    float4 params[4];
};

struct VSOut { float4 pos : SV_Position; };

VSOut VSMain(uint vid : SV_VertexID) {
    float2 corners[3] = { float2(-1.0, -3.0), float2(-1.0, 1.0), float2(3.0, 1.0) };
    VSOut o;
    o.pos = float4(corners[vid], 0.0, 1.0);
    return o;
}

float4 PSMain(VSOut i) : SV_Target {
    return colour_that_was_never_declared(i.pos);
}
