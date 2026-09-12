// Data-driven visualization presets (ADR-009): a directory holding preset.json plus the HLSL it names, compiled
// at load with D3DCompile against one fixed constant-buffer contract.
//
// Two halves on purpose. `preset_source` is everything that can be known without a GPU - the JSON, validated,
// and the shader text read off disk - so enumeration never compiles anything and a malformed preset is rejected
// with a message rather than a HRESULT. `compiled_preset` is what the device made of it. The split is what lets
// mp_renderer_set_preset compile on the *calling* thread and hand the render thread either a finished preset or
// nothing at all: a preset that does not compile never reaches the render thread, so the one already there keeps
// drawing and the compiler's own text is what the caller gets back (AC-117).
//
// ---- the constant buffer contract -----------------------------------------------------------------
//
// Every preset sees the same b0 and the same two buffer SRVs, filled from the newest mp_analysis_frame. A preset
// declares no resources of its own; this is the whole surface, and E4-S4/S5 write against it.
//
//   cbuffer Frame : register(b0) {
//       float4 viewport;   // x = width px, y = height px, z = 1/width, w = 1/height
//       float4 timing;     // x = seconds since the renderer started, y = seconds since the last frame,
//                          // z = frame index, w = mp_analysis_frame.sequence (0 when nothing has played)
//       float4 level;      // x = rms, y = peak, z = spectral centroid Hz, w = harmonic ratio
//       float4 counts;     // x = onset (0 or 1), y = band count, z = spectrum bins, w = waveform samples
//       float4 bands[3];   // the 10 octave bands in .x .y .z .w order; the last two floats are unused
//       float4 params[4];  // the preset's own parameters, in the order preset.json declares them
//       float4 theme[4];   // schema 2: the shell's theme, primary / secondary / accent / background, each RGBA
//   };
//   Buffer<float> Spectrum : register(t0);   // MP_ANALYSIS_SPECTRUM_BINS magnitudes, full-scale sine = 1.0
//   Buffer<float> Waveform : register(t1);   // MP_ANALYSIS_WAVEFORM_SAMPLES mono samples, newest hop
//
// `theme` is mp_renderer_set_theme's (E4-S6): one palette for the whole renderer, the same four colours the
// shell paints its own background gradient from, so a preset and the window around it agree. It is not a
// parameter - a parameter belongs to one preset and returns to its default on a switch, while the theme
// outlives both - and it is not sixteen parameters either, because param_values_ is an array of independent
// relaxed atomics and four colours stored through sixteen of them could be read half-applied. Appending it is
// what makes this schema 2; a schema 1 preset declares the block without it and reads exactly what it always
// read, since nothing before it moved.
//
// The spectrum and waveform are SRVs rather than cbuffer arrays because HLSL packs a float array one value per
// float4 register: 1024 bins would cost 16 KB of constant buffer to carry 4 KB of data, and every read would be
// an index divide. Both are bound to the vertex and the pixel stage, so a preset may drive geometry or colour
// from either.
#pragma once

#include "mpcore.h"

#include <cstdint>
#include <d3d11.h>
#include <filesystem>
#include <string>
#include <vector>
#include <wrl/client.h>

namespace mp::render {

// Parameters a preset may declare. Four float4 registers; 16 is well past what a visualizer exposes in Settings.
inline constexpr uint32_t k_max_preset_params = 16;
// Octave bands carried in b0, padded to whole registers.
inline constexpr uint32_t k_band_slots = 12;
static_assert(MP_ANALYSIS_OCTAVE_BANDS <= k_band_slots, "bands[3] has to hold every octave band");
// The theme carried in b0 (E4-S6): four RGBA colours, in mp_theme_colors' order.
inline constexpr uint32_t k_theme_slots = 16;

// The b0 layout above, in C++. Every member is float4-aligned, which is what makes the memcpy legal.
struct frame_constants {
    float viewport[4];
    float timing[4];
    float level[4];
    float counts[4];
    float bands[k_band_slots];
    float params[k_max_preset_params];
    float theme[k_theme_slots];
};
static_assert(sizeof(frame_constants) % 16 == 0, "a constant buffer is a whole number of float4 registers");

struct preset_param {
    std::string name;
    float default_value = 0.0f;
    float min_value = 0.0f;
    float max_value = 1.0f;
};

// A preset as preset.json describes it, with the shader text already read. No GPU involved.
struct preset_source {
    std::string id;   // stable, <= 63 bytes: matches mp_preset_info.id
    std::string name; // display, <= 127 bytes: matches mp_preset_info.name
    std::string hlsl;
    std::string shader_name; // what D3DCompile puts in its error messages
    std::string vs_entry = "VSMain";
    std::string ps_entry = "PSMain";
    uint32_t vertex_count = 3;
    uint32_t instance_count = 1;
    bool triangle_strip = false;
    float clear[4] = {0.0f, 0.0f, 0.0f, 1.0f};
    std::vector<preset_param> params;

    // Index of a declared parameter, or -1. Names are compared exactly, as the ABI passes them.
    int find_param(const std::string& param_name) const noexcept;
};

// A preset the device has accepted. Held by the renderer; swapped whole.
struct compiled_preset {
    preset_source source;
    Microsoft::WRL::ComPtr<ID3D11VertexShader> vs;
    Microsoft::WRL::ComPtr<ID3D11PixelShader> ps;
    float values[k_max_preset_params] = {}; // current parameter values, defaults until mp_renderer_set_param
};

// The preset compiled into the core. Always enumerable and always loadable, so a renderer whose preset directory
// is missing or empty still draws, and mp_renderer_set_preset always has a previous preset to fall back to.
// This is the E0-S5 bar scene ported to the contract above, not one of the four built-ins E4-S4/S5 own.
const preset_source& builtin_preset();

// Reads one preset.json and the shader it names. False with a message in `error` when the JSON is malformed,
// a required field is missing or out of range, or the shader file cannot be read. Never compiles.
bool load_preset_source(const std::filesystem::path& json_path, preset_source& out, std::string& error);

// Every <root>/*/preset.json that parses, sorted by id. A directory that fails to parse is skipped and appended
// to `warnings` rather than failing the scan: one bad preset must not cost the user the others.
std::vector<preset_source> scan_preset_root(const std::filesystem::path& root, std::vector<std::string>& warnings);

// MPCORE_PRESET_ROOT when set, else <directory holding this module>/presets. Empty when neither can be resolved.
std::filesystem::path default_preset_root();

// Compiles `src` for `device` on the calling thread. On failure returns MP_E_D3D (or MP_E_INVALID_ARG) and puts
// the compiler's own diagnostic - file, line, error code and message - in `error`; `out` is left untouched.
mp_result compile_preset(ID3D11Device* device, const preset_source& src, compiled_preset& out, std::string& error);

} // namespace mp::render
