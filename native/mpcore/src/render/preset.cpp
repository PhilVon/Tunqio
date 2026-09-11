#include "render/preset.h"

#include "abi/last_error.h"
#include "common/log.h"

#include <algorithm>
#include <cstring>
#include <d3dcompiler.h>
#include <fstream>
#include <nlohmann/json.hpp>
#include <sstream>

#include <windows.h>

namespace mp::render {
namespace {

namespace fs = std::filesystem;
using json = nlohmann::json;

constexpr uint32_t k_schema = 1u;
constexpr size_t k_max_id = 63;         // mp_preset_info.id is char[64]
constexpr size_t k_max_name = 127;      // mp_preset_info.name is char[128]
constexpr size_t k_max_hlsl = 1u << 20; // a preset shader is source, not an asset
constexpr uint32_t k_max_vertices = 1u << 16;
constexpr uint32_t k_max_instances = 1u << 12;

// The preset compiled into the core: the E0-S5 bar scene, re-expressed against the contract in preset.h. It
// reads the spectrum when something is playing and animates itself when nothing is, so an idle Now Playing panel
// is not a black rectangle and a test can tell "drawing" from "stopped" without an engine.
constexpr const char* k_builtin_hlsl = R"hlsl(
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

static const uint BarCount = 64;

struct VSOut { float4 pos : SV_Position; float3 color : COLOR; };

// Bar i covers a logarithmic slice of the spectrum, so the bass is not one bin wide and the treble four hundred.
float bar_level(uint i) {
    float bins = max(counts.z, 2.0);
    float lo = pow(bins, (float)i / BarCount);
    float hi = pow(bins, (float)(i + 1) / BarCount);
    uint first = (uint)lo;
    uint last = max((uint)hi, first + 1);
    float peak = 0.0;
    for (uint b = first; b < last && b < first + 64u; ++b) {
        peak = max(peak, Spectrum.Load((int)b));
    }
    // Magnitudes are linear and a mix sits far below full scale; a decade of range reads as a bar.
    return saturate(1.0 + log10(max(peak, 1e-5)) / 5.0);
}

VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID) {
    float gain = params[0].x;
    // timing.w is mp_analysis_frame.sequence: zero means nothing has ever played, so there is no spectrum to read.
    float h = timing.w > 0.0 ? saturate(bar_level(iid) * gain)
                             : saturate((0.5 + 0.45 * sin(timing.x * 2.0 + iid * 0.3)) * gain);
    float w = 2.0 / BarCount;
    float x0 = -1.0 + iid * w + w * 0.1;
    float x1 = x0 + w * 0.8;
    float y0 = -1.0;
    float y1 = -1.0 + h * 2.0;
    float2 corners[6] = { float2(x0, y0), float2(x0, y1), float2(x1, y0),
                          float2(x1, y0), float2(x0, y1), float2(x1, y1) };
    VSOut o;
    o.pos = float4(corners[vid], 0.0, 1.0);
    float t = iid / (float)BarCount;
    o.color = float3(0.15 + 0.85 * t, 0.55 + 0.2 * h, 1.0 - 0.8 * t);
    return o;
}

float4 PSMain(VSOut i) : SV_Target { return float4(i.color, 1.0); }
)hlsl";

bool valid_id(const std::string& s) {
    if (s.empty() || s.size() > k_max_id) {
        return false;
    }
    return std::all_of(s.begin(), s.end(), [](unsigned char c) {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' ||
               c == '.';
    });
}

// A shader path out of preset.json may only name a file beside the preset. Anything absolute, rooted or
// containing ".." is refused rather than resolved: a preset is data, and one day it will be data a user
// downloaded.
bool safe_relative(const fs::path& p) {
    if (p.empty() || p.is_absolute() || p.has_root_name() || p.has_root_directory()) {
        return false;
    }
    for (const auto& part : p) {
        if (part == ".." || part == "") {
            return false;
        }
    }
    return true;
}

bool read_file(const fs::path& path, size_t max_bytes, std::string& out, std::string& error) {
    std::error_code ec;
    const auto size = fs::file_size(path, ec);
    if (ec) {
        error = path.string() + ": " + ec.message();
        return false;
    }
    if (size > max_bytes) {
        error = path.string() + ": " + std::to_string(size) + " bytes exceeds the " + std::to_string(max_bytes) +
                " byte limit";
        return false;
    }
    std::ifstream in{path, std::ios::binary};
    if (!in) {
        error = path.string() + ": cannot open";
        return false;
    }
    std::ostringstream buffer;
    buffer << in.rdbuf();
    out = buffer.str();
    return true;
}

// D3DCompile's diagnostic on one line and short enough to survive mp_last_error's 512-byte buffer. The full text
// goes to the log; what the preset switcher shows is the first error, which is the one the author has to fix.
std::string first_diagnostic(const char* text) {
    if (text == nullptr) {
        return "the compiler reported no diagnostic";
    }
    std::string all{text};
    std::string line;
    std::istringstream lines{all};
    std::string first;
    while (std::getline(lines, line)) {
        while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) {
            line.pop_back();
        }
        if (line.empty()) {
            continue;
        }
        if (first.empty()) {
            first = line;
        }
        if (line.find("error") != std::string::npos) {
            first = line;
            break;
        }
    }
    if (first.empty()) {
        first = "the compiler reported no diagnostic";
    }
    if (first.size() > 320) {
        first.resize(320);
    }
    return first;
}

mp_result compile_stage(const preset_source& src, const char* entry, const char* target, ID3DBlob** blob,
                        std::string& error) {
    Microsoft::WRL::ComPtr<ID3DBlob> errors;
    const UINT flags = D3DCOMPILE_OPTIMIZATION_LEVEL3 | D3DCOMPILE_ENABLE_STRICTNESS;
    const HRESULT hr = D3DCompile(src.hlsl.data(), src.hlsl.size(), src.shader_name.c_str(), nullptr, nullptr, entry,
                                  target, flags, 0, blob, &errors);
    if (SUCCEEDED(hr)) {
        return MP_OK;
    }
    const char* text = errors ? static_cast<const char*>(errors->GetBufferPointer()) : nullptr;
    log(MP_LOG_ERROR, "preset '%s': %s (%s) did not compile:\n%s", src.id.c_str(), entry, target,
        text != nullptr ? text : "no diagnostic");
    error = "preset '" + src.id + "': " + first_diagnostic(text);
    return MP_E_D3D;
}

} // namespace

int preset_source::find_param(const std::string& param_name) const noexcept {
    for (size_t i = 0; i < params.size(); ++i) {
        if (params[i].name == param_name) {
            return static_cast<int>(i);
        }
    }
    return -1;
}

const preset_source& builtin_preset() {
    static const preset_source p = [] {
        preset_source s;
        s.id = "builtin-bars";
        s.name = "Bars (built in)";
        s.hlsl = k_builtin_hlsl;
        s.shader_name = "builtin-bars.hlsl";
        s.vertex_count = 6;
        s.instance_count = 64;
        s.clear[0] = 0.04f;
        s.clear[1] = 0.04f;
        s.clear[2] = 0.06f;
        s.clear[3] = 1.0f;
        s.params.push_back({"gain", 1.0f, 0.0f, 4.0f});
        return s;
    }();
    return p;
}

bool load_preset_source(const fs::path& json_path, preset_source& out, std::string& error) {
    std::string text;
    if (!read_file(json_path, 64u * 1024u, text, error)) {
        return false;
    }

    const std::string where = json_path.string();
    json doc;
    try {
        doc = json::parse(text);
    } catch (const json::exception& e) {
        error = where + ": " + e.what();
        return false;
    }
    if (!doc.is_object()) {
        error = where + ": the document is not a JSON object";
        return false;
    }

    preset_source p;
    try {
        const auto schema = doc.value("schema", k_schema);
        if (schema != k_schema) {
            error = where + ": schema " + std::to_string(schema) + " is not understood (this build reads schema " +
                    std::to_string(k_schema) + ")";
            return false;
        }
        p.id = doc.value("id", std::string{});
        p.name = doc.value("name", std::string{});
        const auto shader = doc.value("shader", std::string{});
        p.vs_entry = doc.value("vertex_entry", std::string{"VSMain"});
        p.ps_entry = doc.value("pixel_entry", std::string{"PSMain"});
        p.vertex_count = doc.value("vertex_count", 3u);
        p.instance_count = doc.value("instance_count", 1u);
        const auto topology = doc.value("topology", std::string{"trianglelist"});

        if (!valid_id(p.id)) {
            error = where + ": \"id\" must be 1-" + std::to_string(k_max_id) + " characters of [A-Za-z0-9._-] (got \"" +
                    p.id + "\")";
            return false;
        }
        if (p.name.empty() || p.name.size() > k_max_name) {
            error = where + ": \"name\" must be 1-" + std::to_string(k_max_name) + " bytes";
            return false;
        }
        if (p.vs_entry.empty() || p.ps_entry.empty()) {
            error = where + ": \"vertex_entry\" and \"pixel_entry\" cannot be empty";
            return false;
        }
        if (topology == "trianglelist") {
            p.triangle_strip = false;
        } else if (topology == "trianglestrip") {
            p.triangle_strip = true;
        } else {
            error = where + ": \"topology\" must be \"trianglelist\" or \"trianglestrip\" (got \"" + topology + "\")";
            return false;
        }
        if (p.vertex_count == 0 || p.vertex_count > k_max_vertices) {
            error = where + ": \"vertex_count\" must be 1-" + std::to_string(k_max_vertices);
            return false;
        }
        if (p.instance_count == 0 || p.instance_count > k_max_instances) {
            error = where + ": \"instance_count\" must be 1-" + std::to_string(k_max_instances);
            return false;
        }

        if (const auto clear = doc.find("clear"); clear != doc.end()) {
            if (!clear->is_array() || clear->size() != 4) {
                error = where + ": \"clear\" must be four numbers [r, g, b, a]";
                return false;
            }
            for (size_t i = 0; i < 4; ++i) {
                p.clear[i] = std::clamp(clear->at(i).get<float>(), 0.0f, 1.0f);
            }
        }

        if (const auto params = doc.find("parameters"); params != doc.end()) {
            if (!params->is_array()) {
                error = where + ": \"parameters\" must be an array";
                return false;
            }
            if (params->size() > k_max_preset_params) {
                error = where + ": " + std::to_string(params->size()) + " parameters exceeds the limit of " +
                        std::to_string(k_max_preset_params);
                return false;
            }
            for (const auto& entry : *params) {
                if (!entry.is_object()) {
                    error = where + ": every entry of \"parameters\" must be an object";
                    return false;
                }
                preset_param param;
                param.name = entry.value("name", std::string{});
                param.min_value = entry.value("min", 0.0f);
                param.max_value = entry.value("max", 1.0f);
                param.default_value = entry.value("default", param.min_value);
                if (!valid_id(param.name)) {
                    error = where + ": parameter names must be 1-" + std::to_string(k_max_id) +
                            " characters of [A-Za-z0-9._-] (got \"" + param.name + "\")";
                    return false;
                }
                if (p.find_param(param.name) >= 0) {
                    error = where + ": parameter \"" + param.name + "\" is declared twice";
                    return false;
                }
                if (!(param.min_value <= param.max_value)) {
                    error = where + ": parameter \"" + param.name + "\" has min above max";
                    return false;
                }
                param.default_value = std::clamp(param.default_value, param.min_value, param.max_value);
                p.params.push_back(std::move(param));
            }
        }

        const fs::path shader_relative{shader};
        if (!safe_relative(shader_relative)) {
            error = where + ": \"shader\" must name a file beside preset.json (got \"" + shader + "\")";
            return false;
        }
        p.shader_name = shader_relative.filename().string();
        if (!read_file(json_path.parent_path() / shader_relative, k_max_hlsl, p.hlsl, error)) {
            return false;
        }
        if (p.hlsl.empty()) {
            error = where + ": \"" + shader + "\" is empty";
            return false;
        }
    } catch (const json::exception& e) {
        error = where + ": " + e.what();
        return false;
    }

    out = std::move(p);
    return true;
}

std::vector<preset_source> scan_preset_root(const fs::path& root, std::vector<std::string>& warnings) {
    std::vector<preset_source> found;
    std::error_code ec;
    if (root.empty() || !fs::is_directory(root, ec)) {
        return found;
    }
    for (fs::directory_iterator it{root, fs::directory_options::skip_permission_denied, ec}, end; it != end;
         it.increment(ec)) {
        if (ec) {
            warnings.push_back(root.string() + ": " + ec.message());
            break;
        }
        if (!it->is_directory(ec) || ec) {
            continue;
        }
        const fs::path manifest = it->path() / "preset.json";
        if (!fs::exists(manifest, ec) || ec) {
            continue;
        }
        preset_source p;
        std::string error;
        if (load_preset_source(manifest, p, error)) {
            found.push_back(std::move(p));
        } else {
            warnings.push_back(std::move(error));
        }
    }
    std::sort(found.begin(), found.end(), [](const preset_source& a, const preset_source& b) { return a.id < b.id; });
    // Two directories claiming one id would make mp_renderer_set_preset ambiguous; the first by path wins and the
    // rest are named in the warnings, because silently picking one is how a user loses a preset without knowing.
    const auto duplicate = std::unique(found.begin(), found.end(),
                                       [](const preset_source& a, const preset_source& b) { return a.id == b.id; });
    for (auto it = duplicate; it != found.end(); ++it) {
        warnings.push_back(root.string() + ": more than one preset claims the id \"" + it->id + "\"; keeping one");
    }
    found.erase(duplicate, found.end());
    return found;
}

fs::path default_preset_root() {
    wchar_t buffer[MAX_PATH];
    if (const DWORD n = GetEnvironmentVariableW(L"MPCORE_PRESET_ROOT", buffer, MAX_PATH); n > 0 && n < MAX_PATH) {
        return fs::path{buffer};
    }
    HMODULE self = nullptr;
    if (GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&default_preset_root), &self) == 0) {
        return {};
    }
    const DWORD n = GetModuleFileNameW(self, buffer, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) {
        return {};
    }
    return fs::path{buffer}.parent_path() / "presets";
}

mp_result compile_preset(ID3D11Device* device, const preset_source& src, compiled_preset& out, std::string& error) {
    if (device == nullptr) {
        error = "compile_preset: no device";
        return MP_E_INVALID_ARG;
    }
    if (src.hlsl.empty()) {
        error = "preset '" + src.id + "': the shader is empty";
        return MP_E_INVALID_ARG;
    }

    Microsoft::WRL::ComPtr<ID3DBlob> vs_blob;
    Microsoft::WRL::ComPtr<ID3DBlob> ps_blob;
    if (const mp_result r = compile_stage(src, src.vs_entry.c_str(), "vs_5_0", &vs_blob, error); r != MP_OK) {
        return r;
    }
    if (const mp_result r = compile_stage(src, src.ps_entry.c_str(), "ps_5_0", &ps_blob, error); r != MP_OK) {
        return r;
    }

    // Built into locals first: `out` is the caller's live preset in the fallback path, and it must not be half
    // replaced by a preset that compiled but whose shader objects the device then refused.
    compiled_preset made;
    made.source = src;
    HRESULT hr = device->CreateVertexShader(vs_blob->GetBufferPointer(), vs_blob->GetBufferSize(), nullptr, &made.vs);
    if (FAILED(hr)) {
        error = "preset '" + src.id + "': CreateVertexShader failed (HRESULT 0x" + std::to_string(hr) + ")";
        return MP_E_D3D;
    }
    hr = device->CreatePixelShader(ps_blob->GetBufferPointer(), ps_blob->GetBufferSize(), nullptr, &made.ps);
    if (FAILED(hr)) {
        error = "preset '" + src.id + "': CreatePixelShader failed (HRESULT 0x" + std::to_string(hr) + ")";
        return MP_E_D3D;
    }
    for (size_t i = 0; i < made.source.params.size(); ++i) {
        made.values[i] = made.source.params[i].default_value;
    }
    out = std::move(made);
    return MP_OK;
}

} // namespace mp::render
