#include "render/renderer.h"

#include "abi/last_error.h"
#include "common/log.h"
#include "render/swapchainpanel_native.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <exception>
#include <new>

#include <windows.h>

namespace mp::render {
namespace {

mp_result d3d_fail(const char* what, HRESULT hr) {
    char text[256];
    std::snprintf(text, sizeof text, "%s failed: HRESULT 0x%08lX", what, static_cast<unsigned long>(hr));
    mp::abi::set_last_error(text);
    log(MP_LOG_ERROR, "%s", text);
    return MP_E_D3D;
}

mp_result invalid_arg(const std::string& text) {
    mp::abi::set_last_error(text);
    return MP_E_INVALID_ARG;
}

int64_t qpc() {
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

int64_t qpc_freq() {
    static const int64_t f = [] {
        LARGE_INTEGER fr;
        QueryPerformanceFrequency(&fr);
        return fr.QuadPart;
    }();
    return f;
}

void copy_utf8(char* dst, size_t cap, const std::string& src) {
    const size_t n = std::min(src.size(), cap - 1);
    std::memcpy(dst, src.data(), n);
    dst[n] = '\0';
}

// A UTF-8 path off the ABI as the wide path Windows wants. Not std::filesystem::u8path, which C++20 deprecates,
// and not the path(std::string) constructor, which would read the bytes in the active code page and lose the
// directory of anyone whose user name is not ASCII.
std::filesystem::path utf8_path_of(const std::string& utf8) {
    if (utf8.empty()) {
        return {};
    }
    const int n = MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), -1, nullptr, 0);
    if (n <= 1) {
        return {};
    }
    std::wstring wide(static_cast<size_t>(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8.c_str(), -1, wide.data(), n);
    return std::filesystem::path{wide};
}

} // namespace

// ---- lifetime -----------------------------------------------------------------------------------

mp_result renderer::create(mp_engine* engine, void* swap_chain_panel_native, const mp_renderer_config& config,
                           std::unique_ptr<renderer>& out) {
    std::unique_ptr<renderer> r{new renderer{}};
    const mp_result result = r->init(engine, swap_chain_panel_native, config);
    if (result != MP_OK) {
        return result;
    }
    out = std::move(r);
    return MP_OK;
}

mp_result renderer::init(mp_engine* engine, void* swap_chain_panel_native, const mp_renderer_config& config) {
    engine_ = engine;
    headless_ = config.headless != 0;
    vsync_ = config.vsync != 0;
    if (!headless_ && swap_chain_panel_native == nullptr) {
        mp::abi::set_last_error("mp_renderer_create: swap_chain_panel_native is NULL and headless is 0");
        return MP_E_INVALID_ARG;
    }
    const uint32_t width = std::max(1u, config.width);
    const uint32_t height = std::max(1u, config.height);
    pending_width_.store(width);
    pending_height_.store(height);
    pending_scale_x_.store(config.scale_x > 0.0f ? config.scale_x : 1.0f);
    pending_scale_y_.store(config.scale_y > 0.0f ? config.scale_y : 1.0f);

    wake_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (wake_ == nullptr) {
        return d3d_fail("CreateEvent(wake)", HRESULT_FROM_WIN32(GetLastError()));
    }
    analysis_ = std::make_unique<mp_analysis_frame>();
    smoothed_ = std::make_unique<mp_analysis_frame>();
    // 200 KB, allocated here rather than on the render thread, because a 6 KB frame times thirty-three slots
    // is not something to be allocating between two pictures.
    analysis_history_ = std::make_unique<std::array<analysis_slot, k_analysis_history + 1>>();

    mp_result r = create_device(config.force_warp != 0);
    if (r != MP_OK) {
        return r;
    }
    r = headless_ ? create_targets(width, height) : create_swap_chain(swap_chain_panel_native, width, height);
    if (r != MP_OK) {
        return r;
    }
    r = create_frame_resources();
    if (r != MP_OK) {
        return r;
    }
    // Deliberately not fatal, and deliberately here rather than lazily in the loop: a device that will not
    // make timestamp queries still draws, and the controller falls back to the frame interval (the fallback
    // is what mp_render_stats.cost_source reports). Created before the render thread starts, so the nine
    // query objects are part of the settled device reference count AC-118's leak test reads rather than
    // something that appears under it.
    gpu_timing_ok_ = create_gpu_timing() == MP_OK;
    if (!gpu_timing_ok_) {
        log(MP_LOG_WARN, "timestamp queries unavailable; adaptive quality will decide on the frame interval");
    }

    load_catalog();
    // The built-in preset is the one the renderer starts on, whatever is on disk: it is the only preset that
    // cannot fail to be there, and having it drawing first is what gives set_preset a previous preset to keep.
    r = set_preset(builtin_preset().id.c_str());
    if (r != MP_OK) {
        return r;
    }

    resize_pending_.store(true); // applies the scale transform on the first frame
    log(MP_LOG_INFO, "renderer created: %s, %ux%u, %s%s, %zu preset(s)", adapter_.c_str(), width, height,
        headless_ ? "headless" : "composition swap chain", warp_.load() ? " (WARP)" : "", catalog_.size());
    thread_ = std::thread([this] { run(); });
    return MP_OK;
}

renderer::~renderer() {
    stop_.store(true, std::memory_order_release);
    if (wake_ != nullptr) {
        SetEvent(wake_);
    }
    if (thread_.joinable()) {
        thread_.join();
    }
    if (waitable_ != nullptr) {
        CloseHandle(waitable_);
    }
    if (wake_ != nullptr) {
        CloseHandle(wake_);
    }
    if (context_) {
        context_->ClearState();
    }
}

mp_result renderer::create_device(bool force_warp) {
    const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
    const UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL got{};
    HRESULT hr = E_FAIL;
    if (!force_warp) {
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, levels, ARRAYSIZE(levels),
                               D3D11_SDK_VERSION, &device_, &got, &context_);
        if (FAILED(hr)) {
            log(MP_LOG_WARN, "hardware D3D11 device unavailable (0x%08lX); falling back to WARP",
                static_cast<unsigned long>(hr));
        }
    }
    if (FAILED(hr)) {
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags, levels, ARRAYSIZE(levels),
                               D3D11_SDK_VERSION, &device_, &got, &context_);
        if (FAILED(hr)) {
            return d3d_fail("D3D11CreateDevice(WARP)", hr);
        }
        warp_.store(true);
    }

    com_ptr<IDXGIDevice> dxgi_device;
    com_ptr<IDXGIAdapter> adapter;
    if (SUCCEEDED(device_.As(&dxgi_device)) && SUCCEEDED(dxgi_device->GetAdapter(&adapter))) {
        DXGI_ADAPTER_DESC desc{};
        if (SUCCEEDED(adapter->GetDesc(&desc))) {
            char name[128];
            WideCharToMultiByte(CP_UTF8, 0, desc.Description, -1, name, sizeof name, nullptr, nullptr);
            adapter_ = name;
        }
    }
    return MP_OK;
}

mp_result renderer::create_swap_chain(void* swap_chain_panel_native, uint32_t width, uint32_t height) {
    com_ptr<IDXGIDevice> dxgi_device;
    com_ptr<IDXGIAdapter> adapter;
    com_ptr<IDXGIFactory2> factory;
    HRESULT hr = device_.As(&dxgi_device);
    if (SUCCEEDED(hr)) {
        hr = dxgi_device->GetAdapter(&adapter);
    }
    if (SUCCEEDED(hr)) {
        hr = adapter->GetParent(__uuidof(IDXGIFactory2), &factory);
    }
    if (FAILED(hr)) {
        return d3d_fail("IDXGIFactory2 from device", hr);
    }

    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = width;
    desc.Height = height;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.SampleDesc.Count = 1;
    desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    desc.BufferCount = 2;
    desc.Scaling = DXGI_SCALING_STRETCH;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
    desc.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;

    com_ptr<IDXGISwapChain1> swap_chain1;
    hr = factory->CreateSwapChainForComposition(device_.Get(), &desc, nullptr, &swap_chain1);
    if (FAILED(hr)) {
        return d3d_fail("CreateSwapChainForComposition", hr);
    }
    hr = swap_chain1.As(&swap_chain_);
    if (FAILED(hr)) {
        return d3d_fail("IDXGISwapChain2", hr);
    }
    swap_chain_->SetMaximumFrameLatency(1);
    waitable_ = swap_chain_->GetFrameLatencyWaitableObject();

    com_ptr<ISwapChainPanelNativeWinUI3> panel;
    hr = static_cast<IUnknown*>(swap_chain_panel_native)->QueryInterface(__uuidof(ISwapChainPanelNativeWinUI3), &panel);
    if (FAILED(hr)) {
        return d3d_fail("QueryInterface(ISwapChainPanelNative)", hr);
    }
    hr = panel->SetSwapChain(swap_chain_.Get());
    if (FAILED(hr)) {
        return d3d_fail("ISwapChainPanelNative::SetSwapChain", hr);
    }
    return create_targets(width, height);
}

mp_result renderer::create_targets(uint32_t width, uint32_t height) {
    rtv_.Reset();
    com_ptr<ID3D11Texture2D> back_buffer;
    HRESULT hr;
    if (headless_) {
        offscreen_.Reset();
        D3D11_TEXTURE2D_DESC td{};
        td.Width = width;
        td.Height = height;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_RENDER_TARGET;
        hr = device_->CreateTexture2D(&td, nullptr, &offscreen_);
        if (FAILED(hr)) {
            return d3d_fail("CreateTexture2D(offscreen)", hr);
        }
        back_buffer = offscreen_;
    } else {
        hr = swap_chain_->GetBuffer(0, __uuidof(ID3D11Texture2D), &back_buffer);
        if (FAILED(hr)) {
            return d3d_fail("IDXGISwapChain::GetBuffer", hr);
        }
    }
    hr = device_->CreateRenderTargetView(back_buffer.Get(), nullptr, &rtv_);
    if (FAILED(hr)) {
        return d3d_fail("CreateRenderTargetView", hr);
    }
    width_ = width;
    height_ = height;
    return MP_OK;
}

// The whole preset-facing resource set: b0 and the two Buffer<float> SRVs described in preset.h. They belong to
// the renderer, not to a preset, which is what makes switching presets a matter of two shader objects.
mp_result renderer::create_frame_resources() {
    D3D11_BUFFER_DESC cb{};
    cb.ByteWidth = sizeof(frame_constants);
    cb.Usage = D3D11_USAGE_DYNAMIC;
    cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    cb.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    HRESULT hr = device_->CreateBuffer(&cb, nullptr, &constants_);
    if (FAILED(hr)) {
        return d3d_fail("CreateBuffer(frame constants)", hr);
    }

    const struct {
        uint32_t elements;
        com_ptr<ID3D11Buffer>* buffer;
        com_ptr<ID3D11ShaderResourceView>* srv;
        const char* what;
    } feeds[] = {
        {MP_ANALYSIS_SPECTRUM_BINS, &spectrum_, &spectrum_srv_, "spectrum"},
        {MP_ANALYSIS_WAVEFORM_SAMPLES, &waveform_, &waveform_srv_, "waveform"},
    };
    // Zero-initialised, so a preset reads silence rather than whatever the driver handed us before anything has
    // ever played - the map below only happens once an analysis frame exists.
    const std::vector<float> zeros(MP_ANALYSIS_SPECTRUM_BINS, 0.0f);
    for (const auto& feed : feeds) {
        D3D11_BUFFER_DESC bd{};
        bd.ByteWidth = feed.elements * sizeof(float);
        bd.Usage = D3D11_USAGE_DYNAMIC;
        bd.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        D3D11_SUBRESOURCE_DATA initial{};
        initial.pSysMem = zeros.data();
        hr = device_->CreateBuffer(&bd, &initial, feed.buffer->GetAddressOf());
        if (FAILED(hr)) {
            return d3d_fail("CreateBuffer(analysis feed)", hr);
        }
        D3D11_SHADER_RESOURCE_VIEW_DESC sd{};
        sd.Format = DXGI_FORMAT_R32_FLOAT;
        sd.ViewDimension = D3D11_SRV_DIMENSION_BUFFER;
        sd.Buffer.FirstElement = 0;
        sd.Buffer.NumElements = feed.elements;
        hr = device_->CreateShaderResourceView(feed.buffer->Get(), &sd, feed.srv->GetAddressOf());
        if (FAILED(hr)) {
            return d3d_fail("CreateShaderResourceView(analysis feed)", hr);
        }
    }
    return MP_OK;
}

void renderer::load_catalog() {
    std::lock_guard lock{preset_mutex_};
    load_catalog_locked();
}

// Both roots, shipped first. The order is the precedence: an id already in the catalogue is skipped, so a user
// preset cannot shadow spectrum-bars or the compiled-in one, and the skip is logged rather than silent because
// a preset that vanishes without a word is how a user loses an afternoon.
void renderer::load_catalog_locked() {
    catalog_.clear();
    catalog_.push_back(builtin_preset());
    preset_root_ = default_preset_root();
    std::vector<std::string> warnings;
    const std::filesystem::path roots[] = {preset_root_, user_preset_root_};
    for (const auto& root : roots) {
        if (root.empty()) {
            continue;
        }
        for (auto& found : scan_preset_root(root, warnings)) {
            const auto clash = std::find_if(catalog_.begin(), catalog_.end(),
                                            [&found](const preset_source& p) { return p.id == found.id; });
            if (clash != catalog_.end()) {
                warnings.push_back(root.string() + ": a preset there claims the id \"" + found.id +
                                   "\", which is already taken; ignoring it");
                continue;
            }
            catalog_.push_back(std::move(found));
        }
    }
    for (const auto& warning : warnings) {
        log(MP_LOG_WARN, "preset: %s", warning.c_str());
    }
    log(MP_LOG_INFO, "preset roots %s and %s: %zu preset(s) including the built-in",
        preset_root_.empty() ? "(none)" : preset_root_.string().c_str(),
        user_preset_root_.empty() ? "(none)" : user_preset_root_.string().c_str(), catalog_.size());
}

// ---- control plane ------------------------------------------------------------------------------

void renderer::resize(uint32_t width, uint32_t height, float scale_x, float scale_y) noexcept {
    pending_width_.store(std::max(1u, width), std::memory_order_relaxed);
    pending_height_.store(std::max(1u, height), std::memory_order_relaxed);
    pending_scale_x_.store(scale_x > 0.0f ? scale_x : 1.0f, std::memory_order_relaxed);
    pending_scale_y_.store(scale_y > 0.0f ? scale_y : 1.0f, std::memory_order_relaxed);
    resize_pending_.store(true, std::memory_order_release);
}

void renderer::set_visible(bool visible) noexcept {
    visible_.store(visible, std::memory_order_release);
    if (visible && wake_ != nullptr) {
        SetEvent(wake_); // resume on the next frame rather than at the end of the hidden wait
    }
}

mp_result renderer::enum_presets(mp_preset_info* out, uint32_t* count) const {
    std::lock_guard lock{preset_mutex_};
    const auto total = static_cast<uint32_t>(catalog_.size());
    if (out == nullptr) {
        *count = total;
        return MP_OK;
    }
    const uint32_t writable = std::min(*count, total);
    for (uint32_t i = 0; i < writable; ++i) {
        mp_preset_info& info = out[i];
        std::memset(&info, 0, sizeof info);
        info.struct_size = sizeof info;
        copy_utf8(info.id, sizeof info.id, catalog_[i].id);
        copy_utf8(info.name, sizeof info.name, catalog_[i].name);
    }
    *count = writable;
    return MP_OK;
}

mp_result renderer::enum_preset_params(const char* utf8_preset_id, mp_preset_param_info* out, uint32_t* count) const {
    const std::string id{utf8_preset_id};
    std::lock_guard lock{preset_mutex_};
    const auto it =
        std::find_if(catalog_.begin(), catalog_.end(), [&id](const preset_source& p) { return p.id == id; });
    if (it == catalog_.end()) {
        return invalid_arg("mp_renderer_enum_preset_params: no preset with id \"" + id + "\" (" +
                           std::to_string(catalog_.size()) + " known; enumerate with mp_renderer_enum_presets)");
    }

    const auto total = static_cast<uint32_t>(it->params.size());
    if (out == nullptr) {
        *count = total;
        return MP_OK;
    }
    const uint32_t writable = std::min(*count, total);
    for (uint32_t i = 0; i < writable; ++i) {
        const preset_param& declared = it->params[i];
        mp_preset_param_info& info = out[i];
        std::memset(&info, 0, sizeof info);
        info.struct_size = sizeof info;
        copy_utf8(info.name, sizeof info.name, declared.name);
        copy_utf8(info.label, sizeof info.label, declared.label.empty() ? declared.name : declared.label);
        copy_utf8(info.unit, sizeof info.unit, declared.unit);
        std::string packed;
        for (const auto& choice : declared.choices) {
            packed += packed.empty() ? "" : "|";
            packed += choice;
        }
        copy_utf8(info.choices, sizeof info.choices, packed);
        info.min_value = declared.min_value;
        info.max_value = declared.max_value;
        info.default_value = declared.default_value;
        info.step = declared.step;
        info.flags = (declared.hidden ? static_cast<uint32_t>(MP_PARAM_HIDDEN) : 0u) |
                     (declared.choices.empty() ? 0u : static_cast<uint32_t>(MP_PARAM_CHOICE));
    }
    *count = writable;
    return MP_OK;
}

mp_result renderer::set_user_preset_root(const char* utf8_path) {
    const std::string given = utf8_path == nullptr ? std::string{} : std::string{utf8_path};
    std::lock_guard lock{preset_mutex_};
    user_preset_root_ = utf8_path_of(given);
    load_catalog_locked();
    return MP_OK;
}

uint32_t renderer::rescan_presets() {
    std::lock_guard lock{preset_mutex_};
    load_catalog_locked();
    return static_cast<uint32_t>(catalog_.size());
}

mp_result renderer::set_preset(const char* utf8_id) {
    const std::string id{utf8_id};
    preset_source source;
    {
        std::lock_guard lock{preset_mutex_};
        const auto it =
            std::find_if(catalog_.begin(), catalog_.end(), [&id](const preset_source& p) { return p.id == id; });
        if (it == catalog_.end()) {
            return invalid_arg("mp_renderer_set_preset: no preset with id \"" + id + "\" (" +
                               std::to_string(catalog_.size()) + " known; enumerate with mp_renderer_enum_presets)");
        }
        source = *it;
    }

    // Compiled here, on the caller's thread, and only handed over if it worked. That is the whole of AC-117: a
    // preset that does not compile never becomes the renderer's, so whatever was drawing keeps drawing, and the
    // caller gets the compiler's own words rather than "MP_E_D3D".
    auto made = std::make_shared<compiled_preset>();
    std::string error;
    if (const mp_result r = compile_preset(device_.Get(), source, *made, error); r != MP_OK) {
        mp::abi::set_last_error(error);
        return r;
    }

    std::lock_guard lock{preset_mutex_};
    for (size_t i = 0; i < k_max_preset_params; ++i) {
        param_values_[i].store(made->values[i], std::memory_order_relaxed);
    }
    current_ = made;
    pending_ = std::move(made);
    preset_pending_.store(true, std::memory_order_release);
    log(MP_LOG_INFO, "preset '%s' compiled and active", id.c_str());
    return MP_OK;
}

mp_result renderer::set_param(const char* utf8_name, float value) {
    const std::string name{utf8_name};
    std::lock_guard lock{preset_mutex_};
    if (!current_) {
        mp::abi::set_last_error("mp_renderer_set_param: no preset is active");
        return MP_E_STATE;
    }
    const int index = current_->source.find_param(name);
    if (index < 0) {
        std::string known;
        for (const auto& p : current_->source.params) {
            known += known.empty() ? "" : ", ";
            known += p.name;
        }
        return invalid_arg("mp_renderer_set_param: preset \"" + current_->source.id + "\" declares no parameter \"" +
                           name + "\"" + (known.empty() ? " (it declares none)" : " (it declares " + known + ")"));
    }
    const preset_param& declared = current_->source.params[static_cast<size_t>(index)];
    if (!std::isfinite(value)) {
        return invalid_arg("mp_renderer_set_param: \"" + name + "\" was given a value that is not finite");
    }
    param_values_[static_cast<size_t>(index)].store(std::clamp(value, declared.min_value, declared.max_value),
                                                    std::memory_order_relaxed);
    return MP_OK;
}

mp_result renderer::set_theme(const mp_theme_colors& colors) {
    // The four colours in mp_theme_colors' own order, which is the order b0's `theme` declares them in.
    const float* source[4] = {colors.primary, colors.secondary, colors.accent, colors.background};
    std::array<float, k_theme_slots> next{};
    for (size_t c = 0; c < 4; ++c) {
        for (size_t ch = 0; ch < 4; ++ch) {
            const float v = source[c][ch];
            if (!std::isfinite(v)) {
                return invalid_arg("mp_renderer_set_theme: colour " + std::to_string(c) + " channel " +
                                   std::to_string(ch) + " is not a finite number");
            }
            // Clamped rather than refused, as set_param clamps to a declared range: a shell that computed a
            // channel slightly outside 0..1 has asked for the edge of the gamut, not made an error.
            next[c * 4 + ch] = std::clamp(v, 0.0f, 1.0f);
        }
    }

    {
        std::lock_guard lock{theme_mutex_};
        theme_ = next;
    }
    // Released after the write, so the render thread that sees the new generation sees the colours behind it.
    theme_generation_.fetch_add(1, std::memory_order_release);
    return MP_OK;
}

std::array<float, k_theme_slots> renderer::theme_now() const {
    std::lock_guard lock{theme_mutex_};
    return theme_;
}

mp_result renderer::set_quality(mp_quality_policy policy) {
    switch (policy) {
    case MP_QUALITY_AUTO:
    case MP_QUALITY_LOW:
    case MP_QUALITY_MEDIUM:
    case MP_QUALITY_HIGH:
        break;
    default:
        return invalid_arg("mp_renderer_set_quality: " + std::to_string(static_cast<int>(policy)) +
                           " is not one of MP_QUALITY_AUTO, LOW, MEDIUM or HIGH");
    }
    quality_policy_.store(static_cast<uint32_t>(policy), std::memory_order_release);
    return MP_OK;
}

void renderer::set_quality_tuning(const quality_tuning& tuning) {
    {
        std::lock_guard lock{quality_tuning_mutex_};
        quality_tuning_ = tuning;
    }
    quality_tuning_generation_.fetch_add(1, std::memory_order_release);
}

quality_tuning renderer::quality_tuning_now() const {
    std::lock_guard lock{quality_tuning_mutex_};
    return quality_tuning_;
}

std::string renderer::active_preset_id() const {
    std::lock_guard lock{preset_mutex_};
    return current_ ? current_->source.id : std::string{};
}

uint32_t renderer::device_references() const noexcept {
    if (!device_) {
        return 0;
    }
    ID3D11Device* raw = device_.Get();
    raw->AddRef();
    return static_cast<uint32_t>(raw->Release());
}

bool renderer::capture_pixel(uint32_t x, uint32_t y, uint8_t out_bgra[4], int timeout_ms) noexcept {
    if (stop_.load(std::memory_order_acquire) || !thread_.joinable()) {
        return false;
    }
    capture_x_.store(x, std::memory_order_relaxed);
    capture_y_.store(y, std::memory_order_relaxed);
    capture_done_.store(false, std::memory_order_release);
    capture_pending_.store(true, std::memory_order_release);

    const int64_t deadline = qpc() + static_cast<int64_t>(timeout_ms) * qpc_freq() / 1000;
    while (!capture_done_.load(std::memory_order_acquire)) {
        if (qpc() > deadline || stop_.load(std::memory_order_acquire)) {
            capture_pending_.store(false, std::memory_order_release);
            return false;
        }
        Sleep(1);
    }
    const uint32_t packed = capture_value_.load(std::memory_order_acquire);
    out_bgra[0] = static_cast<uint8_t>(packed & 0xFF);
    out_bgra[1] = static_cast<uint8_t>((packed >> 8) & 0xFF);
    out_bgra[2] = static_cast<uint8_t>((packed >> 16) & 0xFF);
    out_bgra[3] = static_cast<uint8_t>((packed >> 24) & 0xFF);
    return true;
}

bool renderer::capture_frame(std::vector<uint8_t>& out_bgra, uint32_t& out_width, uint32_t& out_height,
                             int timeout_ms) noexcept {
    if (stop_.load(std::memory_order_acquire) || !thread_.joinable()) {
        return false;
    }
    capture_full_.store(true, std::memory_order_release);
    capture_done_.store(false, std::memory_order_release);
    capture_pending_.store(true, std::memory_order_release);

    const int64_t deadline = qpc() + static_cast<int64_t>(timeout_ms) * qpc_freq() / 1000;
    while (!capture_done_.load(std::memory_order_acquire)) {
        if (qpc() > deadline || stop_.load(std::memory_order_acquire)) {
            capture_pending_.store(false, std::memory_order_release);
            capture_full_.store(false, std::memory_order_release);
            return false;
        }
        Sleep(1);
    }
    capture_full_.store(false, std::memory_order_release);
    // capture_done_ was released by the render thread after it filled these, and acquired above.
    try {
        out_bgra = capture_pixels_;
    } catch (const std::exception&) {
        return false; // noexcept: a 1080p readback is 8 MB, and a caller gets `false` rather than a terminate
    }
    out_width = capture_pixels_width_;
    out_height = capture_pixels_height_;
    return out_width > 0 && out_height > 0 && out_bgra.size() == static_cast<size_t>(out_width) * out_height * 4;
}

void renderer::set_analysis_override(const mp_analysis_frame* frame) {
    std::lock_guard lock{analysis_override_mutex_};
    if (frame == nullptr) {
        analysis_override_.reset();
        analysis_override_active_.store(false, std::memory_order_release);
        return;
    }
    if (!analysis_override_) {
        analysis_override_ = std::make_unique<mp_analysis_frame>();
    }
    *analysis_override_ = *frame;
    // Bumped under the same lock the render thread copies under, so a generation it has seen is a frame it has.
    analysis_override_generation_.fetch_add(1, std::memory_order_acq_rel);
    analysis_override_active_.store(true, std::memory_order_release);
}

mp_result renderer::set_av_sync(const mp_av_sync_config& config) {
    if (config.mode != MP_AV_SYNC_NEWEST && config.mode != MP_AV_SYNC_AUDIBLE) {
        char text[192];
        std::snprintf(text, sizeof text,
                      "mp_renderer_set_av_sync: mode %u is neither MP_AV_SYNC_NEWEST (0) nor MP_AV_SYNC_AUDIBLE (1)",
                      config.mode);
        return invalid_arg(text);
    }
    if (!std::isfinite(config.offset_ms)) {
        return invalid_arg("mp_renderer_set_av_sync: offset_ms is not a finite number");
    }
    // The ring is sized before the capacity is published, so a render thread that sees a non-zero capacity is
    // never looking at a vector that has not been resized yet.
    const uint32_t capacity = std::min<uint32_t>(config.probe_capacity, k_max_probe_samples);
    {
        std::lock_guard lock{probe_mutex_};
        if (capacity != probe_.size()) {
            probe_.assign(capacity, mp_latency_sample{});
            probe_head_ = 0;
            probe_count_ = 0;
        }
    }
    probe_capacity_.store(capacity, std::memory_order_release);
    av_sync_offset_ms_.store(config.offset_ms, std::memory_order_relaxed);
    av_sync_mode_.store(config.mode, std::memory_order_release);
    return MP_OK;
}

mp_result renderer::set_temporal_smoothing(float attack_ms, float decay_ms) {
    if (!std::isfinite(attack_ms) || !std::isfinite(decay_ms)) {
        return invalid_arg("mp_renderer_set_temporal_smoothing: attack_ms and decay_ms must be finite numbers");
    }
    if (attack_ms < 0.0f || decay_ms < 0.0f) {
        char text[192];
        std::snprintf(text, sizeof text,
                      "mp_renderer_set_temporal_smoothing: attack_ms %g and decay_ms %g must not be negative (0 is off)",
                      static_cast<double>(attack_ms), static_cast<double>(decay_ms));
        return invalid_arg(text);
    }
    smoothing_attack_ms_.store(std::min(attack_ms, k_max_attack_ms), std::memory_order_relaxed);
    smoothing_decay_ms_.store(std::min(decay_ms, k_max_decay_ms), std::memory_order_relaxed);
    return MP_OK;
}

envelope_times renderer::temporal_smoothing() const noexcept {
    return envelope_times{smoothing_attack_ms_.load(std::memory_order_relaxed),
                          smoothing_decay_ms_.load(std::memory_order_relaxed)};
}

void renderer::set_smoothing_clock_manual(bool manual) {
    smoothing_clock_manual_.store(manual, std::memory_order_release);
}

void renderer::advance_smoothing_clock(double seconds) {
    smoothing_clock_ns_.fetch_add(static_cast<int64_t>(std::llround(seconds * 1e9)), std::memory_order_acq_rel);
}

// Full-size elements only: the export wraps this in mp::abi::out_array, which is what serves a caller whose
// mp_latency_sample is shorter than this build's. Destructive, and safe to run twice for that reason - the
// count query out_array makes first takes nothing.
mp_result renderer::drain_latency(mp_latency_sample* out, uint32_t* count) {
    if (probe_capacity_.load(std::memory_order_acquire) == 0) {
        mp::abi::set_last_error("mp_renderer_drain_latency: the latency probe is off; call "
                                "mp_renderer_set_av_sync with a probe_capacity first");
        return MP_E_STATE;
    }
    std::lock_guard lock{probe_mutex_};
    if (out == nullptr || *count == 0) {
        *count = static_cast<uint32_t>(probe_count_); // the count query; nothing is taken
        return MP_OK;
    }
    const size_t taking = std::min<size_t>(*count, probe_count_);
    const size_t capacity = probe_.size();
    for (size_t i = 0; i < taking; ++i) {
        out[i] = probe_[(probe_head_ + i) % capacity];
    }
    probe_head_ = capacity == 0 ? 0 : (probe_head_ + taking) % capacity;
    probe_count_ -= taking;
    *count = static_cast<uint32_t>(taking);
    return MP_OK;
}

void renderer::get_stats(mp_render_stats& out) const noexcept {
    std::memset(&out, 0, sizeof out);
    out.struct_size = sizeof out;
    out.frames = frames_.load(std::memory_order_relaxed);
    out.resizes = resizes_.load(std::memory_order_relaxed);
    out.fps = fps_x100_.load(std::memory_order_relaxed) / 100.0;
    out.frame_ms_last = frame_us_last_.load(std::memory_order_relaxed) / 1000.0f;
    out.frame_ms_max = frame_us_max_.load(std::memory_order_relaxed) / 1000.0f;
    out.frame_ms_avg =
        out.frames > 1 ? static_cast<float>(frame_us_total_.load(std::memory_order_relaxed) / 1000.0 / (out.frames - 1))
                       : 0.0f;
    for (size_t i = 0; i < MP_RENDER_HISTOGRAM_BUCKETS; ++i) {
        out.frame_ms_histogram[i] = histogram_[i].load(std::memory_order_relaxed);
    }
    out.dxgi_present_count = dxgi_present_count_.load(std::memory_order_relaxed);
    out.dxgi_missed_refreshes = dxgi_missed_.load(std::memory_order_relaxed);
    out.width = pending_width_.load(std::memory_order_relaxed);
    out.height = pending_height_.load(std::memory_order_relaxed);
    out.warp = warp_.load() ? 1 : 0;
    out.headless = headless_ ? 1 : 0;
    out.device_lost = device_lost_.load() ? 1 : 0;
    out.visible = visible_.load() ? 1 : 0;
    const size_t n = std::min(adapter_.size(), sizeof out.adapter - 1);
    std::memcpy(out.adapter, adapter_.data(), n);
    out.adapter[n] = '\0';
    out.quality_policy = quality_policy_.load(std::memory_order_relaxed);
    out.quality_tier = stat_quality_tier_.load(std::memory_order_relaxed);
    out.quality_changes = stat_quality_changes_.load(std::memory_order_relaxed);
    // Before the render thread has drawn anything these read the surface at full scale, which is what the
    // renderer is about to draw: a zero here would say "nothing" where the truth is "not yet".
    const uint32_t rw = stat_render_width_.load(std::memory_order_relaxed);
    const uint32_t rh = stat_render_height_.load(std::memory_order_relaxed);
    out.render_width = rw != 0 ? rw : out.width;
    out.render_height = rh != 0 ? rh : out.height;
    out.render_scale = static_cast<float>(stat_render_scale_x1000_.load(std::memory_order_relaxed)) / 1000.0f;
    out.frame_cost_ms = static_cast<float>(stat_frame_cost_ns_.load(std::memory_order_relaxed)) / 1'000'000.0f;
    out.cost_source = stat_cost_source_.load(std::memory_order_relaxed);
}

// ---- render thread ------------------------------------------------------------------------------

void renderer::run() {
    SetThreadDescription(GetCurrentThread(), L"Tunqio render");
    const int64_t start = qpc();
    int64_t previous = start;
    last_frame_qpc_ = 0;
    fps_window_start_qpc_ = start;
    while (!stop_.load(std::memory_order_acquire)) {
        if (!visible_.load(std::memory_order_acquire)) {
            WaitForSingleObject(wake_, 50); // set_visible(1) and shutdown signal it; the timeout is the backstop
            last_frame_qpc_ = 0;            // do not count the pause as a frame gap
            previous = qpc();
            continue;
        }
        if (waitable_ != nullptr) {
            WaitForSingleObjectEx(waitable_, 1000, TRUE);
        }
        if (stop_.load(std::memory_order_acquire)) {
            break;
        }
        // The capture request is taken before anything else this iteration will apply. A request can only have
        // been posted after whatever the caller did first (a set_preset, a set_param) was published, so latching
        // it here and answering it at the bottom means the pixel handed back is from a frame that has all of it.
        const bool serving = capture_pending_.exchange(false, std::memory_order_acq_rel);
        apply_pending_resize();
        apply_pending_preset();
        if (!rtv_) {
            if (serving) {
                capture_pending_.store(true, std::memory_order_release); // still owed; try again next iteration
            }
            Sleep(5);
            continue;
        }
        const int64_t now = qpc();
        const double seconds = static_cast<double>(now - start) / static_cast<double>(qpc_freq());
        // The tier decided at the bottom of the previous iteration becomes this frame's rectangle here, so a
        // frame is drawn entirely at one scale and the cost measured around it is the cost of one tier.
        apply_render_scale();
        begin_gpu_timing();
        render_frame(seconds, static_cast<double>(now - previous) / static_cast<double>(qpc_freq()), now);
        end_gpu_timing();
        previous = now;
        if (serving) {
            serve_capture();
        }
        if (swap_chain_) {
            const HRESULT hr = swap_chain_->Present(vsync_ ? 1 : 0, 0);
            if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET) {
                device_lost_.store(true);
                log(MP_LOG_ERROR, "device lost during Present (0x%08lX); render thread stopping",
                    static_cast<unsigned long>(hr));
                break;
            }
            record_latency_sample();
            collect_dxgi_statistics();
        } else {
            // Headless: pace to roughly 60 fps so the numbers resemble a real display without burning a core.
            context_->Flush();
            // Before the pacing sleep, not after it: a sample taken on the far side would report the sleep as
            // latency, and the headless path is where the latency harness measures.
            record_latency_sample();
            Sleep(vsync_ ? 16 : 0);
        }
        apply_quality(seconds, now); // reads last_frame_qpc_, so before record_frame_time moves it
        record_frame_time(now);
        frames_.fetch_add(1, std::memory_order_relaxed);
    }
    // The render thread's own references go with the thread, not with the object: a preset outliving the loop
    // would keep shader objects alive past the point where the device count is supposed to settle.
    render_preset_.reset();
    capture_staging_.Reset();
}

void renderer::apply_pending_resize() {
    if (!resize_pending_.exchange(false, std::memory_order_acq_rel)) {
        return;
    }
    const uint32_t w = pending_width_.load(std::memory_order_relaxed);
    const uint32_t h = pending_height_.load(std::memory_order_relaxed);
    const float sx = pending_scale_x_.load(std::memory_order_relaxed);
    const float sy = pending_scale_y_.load(std::memory_order_relaxed);
    if (w != width_ || h != height_) {
        context_->OMSetRenderTargets(0, nullptr, nullptr);
        rtv_.Reset();
        if (swap_chain_) {
            const HRESULT hr = swap_chain_->ResizeBuffers(0, w, h, DXGI_FORMAT_UNKNOWN,
                                                          DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT);
            if (FAILED(hr)) {
                if (hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET) {
                    device_lost_.store(true);
                    stop_.store(true);
                }
                log(MP_LOG_ERROR, "ResizeBuffers(%u x %u) failed: 0x%08lX", w, h, static_cast<unsigned long>(hr));
                return;
            }
        }
        if (create_targets(w, h) != MP_OK) {
            return;
        }
        resizes_.fetch_add(1, std::memory_order_relaxed);
        // ResizeBuffers puts the source size back to the whole buffer, and the picture costs a different
        // number of pixels than it did a moment ago: both halves of the quality state are now stale.
        render_width_ = 0;
        render_height_ = 0;
        quality_model_dirty_ = true;
    }
    if (swap_chain_) {
        // The panel is laid out in DIPs; the swap chain is in pixels. The inverse scale maps one onto the other.
        DXGI_MATRIX_3X2_F m{};
        m._11 = 1.0f / sx;
        m._22 = 1.0f / sy;
        swap_chain_->SetMatrixTransform(&m);
    }
}

void renderer::apply_pending_preset() {
    if (!preset_pending_.load(std::memory_order_acquire)) {
        return;
    }
    std::lock_guard lock{preset_mutex_};
    render_preset_ = std::move(pending_);
    pending_.reset();
    preset_pending_.store(false, std::memory_order_release);
    // A different preset is a different cost model, and T-148 measured how different: 10 to 17 times between
    // ambient-glow and the other three. Carrying a gain estimate across that is worse than having none.
    quality_model_dirty_ = true;
}

// The mixer's format, which is how a byte position becomes a millisecond. Cached: it changes only when the
// output device does, and a device change is not something to pay for on every frame.
void renderer::refresh_mix_format() {
    const uint64_t frame = frames_.load(std::memory_order_relaxed);
    if (byte_rate_ > 0.0 && frame - mix_format_frame_ < 64) {
        return; // about once a second at 60 fps
    }
    mix_format_frame_ = frame;
    mp_engine_stats stats{};
    stats.struct_size = sizeof stats;
    if (engine_ == nullptr || mp_engine_get_stats(engine_, &stats) != MP_OK) {
        return;
    }
    if (stats.output_sample_rate == 0 || stats.output_channels == 0) {
        return; // no output has been opened yet; the previous cache, or zero, is the honest answer
    }
    // mp_clock.mixer_byte_pos counts float frames times channels times four, and E1-S6 made the mixer run at
    // the device's own rate, so the output format IS the mixer's.
    bytes_per_frame_ = static_cast<double>(stats.output_channels) * 4.0;
    byte_rate_ = static_cast<double>(stats.output_sample_rate) * bytes_per_frame_;
    mix_sample_rate_ = stats.output_sample_rate;
}

// Takes whatever the analysis has published into the history ring. The frame lands in the slot AFTER the
// newest, which is not one of the counted entries, so a poll that turns out to hold the sequence already at
// the head can be discarded by simply not advancing - it has overwritten nothing a later frame may want.
void renderer::poll_analysis(int64_t now_qpc) {
    const size_t capacity = analysis_history_->size();
    analysis_slot& scratch = (*analysis_history_)[analysis_history_next_];
    scratch.frame.struct_size = sizeof(mp_analysis_frame);
    if (mp_analysis_try_get_latest(engine_, &scratch.frame) != MP_OK) {
        return;
    }
    if (analysis_history_count_ > 0) {
        const analysis_slot& newest = (*analysis_history_)[(analysis_history_next_ + capacity - 1) % capacity];
        if (newest.frame.sequence == scratch.frame.sequence) {
            return; // nothing new since the last poll; the ring already holds this one
        }
    }
    scratch.first_seen_qpc = now_qpc;
    analysis_history_next_ = (analysis_history_next_ + 1) % capacity;
    if (analysis_history_count_ < k_analysis_history) {
        ++analysis_history_count_;
    }
}

// Which of the frames this thread has seen the picture is drawn from (E4-S8). MP_AV_SYNC_NEWEST, and every
// path that cannot answer the question - no clock, no mixer format, no history - is the newest, which is what
// every build before ABI 0.17 always did.
const renderer::analysis_slot* renderer::choose_analysis_frame(int64_t now_qpc) {
    if (analysis_history_count_ == 0) {
        return nullptr;
    }
    const size_t capacity = analysis_history_->size();
    // back == 0 is the newest entry; back == count-1 the oldest.
    const auto at = [&](size_t back) -> const analysis_slot& {
        return (*analysis_history_)[(analysis_history_next_ + capacity - 1 - back) % capacity];
    };
    const analysis_slot* newest = &at(0);
    if (av_sync_mode_.load(std::memory_order_acquire) != MP_AV_SYNC_AUDIBLE || engine_ == nullptr) {
        return newest;
    }
    refresh_mix_format();
    if (byte_rate_ <= 0.0) {
        return newest;
    }
    mp_clock clock{};
    clock.struct_size = sizeof clock;
    if (mp_engine_get_clock(engine_, &clock) != MP_OK) {
        return newest;
    }
    // Where the listener is on the mixer's own axis, carried forward from the instant the clock was read to
    // now. The carry is microseconds in practice; it is here because the clock reading and the frame this
    // decision is for are not the same instant, and pretending they are would be a bias rather than noise.
    const double elapsed_s = static_cast<double>(now_qpc - clock.qpc_ticks) / static_cast<double>(qpc_freq());
    const double audible =
        static_cast<double>(clock.mixer_byte_pos - clock.output_buffered_bytes) + elapsed_s * byte_rate_;
    const double offset_bytes =
        static_cast<double>(av_sync_offset_ms_.load(std::memory_order_relaxed)) / 1000.0 * byte_rate_;
    const double target = audible + offset_bytes;
    // A frame is documented as describing the hop STARTING at its mixer_byte_pos, so the instant it stands
    // for is the middle of that hop and not its leading edge. Half a hop is 5.3 ms at 48 kHz and 5.8 at 44.1,
    // which is a third of a 60 Hz refresh interval: a bias if it is skipped, not a rounding.
    // The hop length off the ABI rather than out of analysis/analyzer.h: the waveform field IS the newest hop
    // (the header says so and the analyzer static_asserts it), so this keeps render/ off the analysis
    // internals for one number that the public contract already carries.
    const double half_hop = 0.5 * static_cast<double>(MP_ANALYSIS_WAVEFORM_SAMPLES) * bytes_per_frame_;
    const analysis_slot* best = newest;
    double best_distance = 0.0;
    for (size_t back = 0; back < analysis_history_count_; ++back) {
        const analysis_slot& slot = at(back);
        const double instant = static_cast<double>(slot.frame.mixer_byte_pos) + half_hop;
        const double distance = std::fabs(instant - target);
        if (back == 0 || distance < best_distance) {
            best_distance = distance;
            best = &slot;
        }
        // The ring is ordered, so once the candidates start getting further away they stay further away and
        // the rest of the history is older still. Offline, where the listener is level with the mixer, this
        // exits on the first entry and the whole of compensation costs one comparison.
        else if (distance > best_distance) {
            break;
        }
    }
    return best;
}

// What the picture that has just been presented was drawn from, and where the listener was when it was. Called
// from the render thread immediately after Present (or after Flush, headless) and BEFORE the headless pacing
// sleep, because a sample taken after that sleep would report the sleep as latency.
//
// The clock is read here rather than reused from choose_analysis_frame's reading precisely because this is the
// instant the number is about: the frame is on the queue now, and where the loudspeaker is NOW is the other
// half of the subtraction. Its own qpc_ticks is taken as present_qpc for the same reason - the two halves of
// av_error_ms are then one reading rather than two instants a caller has to assume are the same.
void renderer::record_latency_sample() {
    if (probe_capacity_.load(std::memory_order_acquire) == 0 || !have_analysis_) {
        return;
    }
    mp_latency_sample sample{};
    sample.struct_size = sizeof sample;
    sample.analysis_sequence = analysis_sequence_;
    sample.frame_index = frames_.load(std::memory_order_relaxed);
    sample.drawn_mixer_byte_pos = analysis_->mixer_byte_pos;
    sample.analysis_qpc = analysis_->qpc_ticks;
    sample.first_seen_qpc = drawn_first_seen_qpc_;
    sample.qpc_frequency = qpc_freq();
    sample.byte_rate = byte_rate_;
    sample.mixer_sample_rate = mix_sample_rate_;
    sample.mode = av_sync_mode_.load(std::memory_order_relaxed);
    sample.redrawn = drawn_repeat_ ? 1 : 0;
    sample.present_qpc = qpc();
    if (engine_ != nullptr) {
        refresh_mix_format();
        sample.byte_rate = byte_rate_;
        sample.mixer_sample_rate = mix_sample_rate_;
        mp_clock clock{};
        clock.struct_size = sizeof clock;
        if (mp_engine_get_clock(engine_, &clock) == MP_OK) {
            sample.present_qpc = clock.qpc_ticks;
            sample.mixer_byte_pos = clock.mixer_byte_pos;
            sample.audible_mixer_byte_pos = clock.mixer_byte_pos - clock.output_buffered_bytes;
        }
    }
    std::lock_guard lock{probe_mutex_};
    const size_t capacity = probe_.size();
    if (capacity == 0) {
        return;
    }
    probe_[(probe_head_ + probe_count_) % capacity] = sample;
    if (probe_count_ < capacity) {
        ++probe_count_;
    } else {
        probe_head_ = (probe_head_ + 1) % capacity; // full: the oldest goes, and frame_index says one did
    }
}

void renderer::update_frame_resources(double seconds, double delta, int64_t now_qpc) {
    // The envelope's step, read FIRST: under a test's manual clock an advance that this frame sees was made after
    // any override set before it, so the frame never eases toward the input the test is about to replace.
    double envelope_step = delta;
    if (smoothing_clock_manual_.load(std::memory_order_acquire)) {
        const int64_t now_ns = smoothing_clock_ns_.load(std::memory_order_acquire);
        envelope_step = static_cast<double>(now_ns - smoothing_clock_seen_ns_) / 1e9;
        smoothing_clock_seen_ns_ = now_ns;
    }
    bool fresh = false;
    drawn_repeat_ = have_analysis_;
    if (analysis_override_active_.load(std::memory_order_acquire)) {
        // A test's fixed frame wins over the engine's. The generation is compared before the lock is taken, so
        // an override held across a thousand frames costs two atomic loads a frame and exactly one copy; the
        // generation is read again inside the lock because it is what the copy taken under it corresponds to.
        if (const uint32_t generation = analysis_override_generation_.load(std::memory_order_acquire);
            !have_analysis_ || generation != analysis_override_seen_) {
            std::lock_guard lock{analysis_override_mutex_};
            if (analysis_override_) {
                *analysis_ = *analysis_override_;
                analysis_override_seen_ = analysis_override_generation_.load(std::memory_order_relaxed);
                analysis_sequence_ = analysis_->sequence;
                have_analysis_ = true;
                fresh = true;
                drawn_first_seen_qpc_ = now_qpc;
            }
        }
    } else if (engine_ != nullptr) {
        // Two steps since E4-S8, where there was one: everything published goes into the history ring, and
        // then one of the ring is chosen. The choice is the newest wherever there is nothing to compensate
        // against, so the frame this picks up is the frame ABI 0.16 picked up unless a listener is behind.
        poll_analysis(now_qpc);
        if (const analysis_slot* chosen = choose_analysis_frame(now_qpc); chosen != nullptr) {
            fresh = !have_analysis_ || chosen->frame.sequence != analysis_sequence_;
            if (fresh) {
                *analysis_ = chosen->frame;
                analysis_sequence_ = chosen->frame.sequence;
            }
            drawn_first_seen_qpc_ = chosen->first_seen_qpc;
            have_analysis_ = true;
        }
    }
    drawn_repeat_ = drawn_repeat_ && !fresh;

    // Temporal smoothing (T-184): the one place the analysis frame becomes something other than what the analysis
    // published before a preset sees it. analysis_ stays the frame that was CHOSEN - the latency probe describes it
    // and the sequence names it - and `drawn` is what the preset is given. With both time constants at zero this
    // block is skipped entirely and `drawn` is analysis_ itself, so off is not "an envelope that happens to pass
    // everything through" but the path every build before ABI 0.19 took, byte for byte.
    const mp_analysis_frame* drawn = analysis_.get();
    bool upload_spectrum = fresh;
    if (const envelope_times times = temporal_smoothing(); have_analysis_ && !times.off()) {
        // Every frame, not only a fresh one: between two analysis frames the input is held and the envelope is
        // still moving toward it, which is the whole of what it is for.
        const bool moved = envelope_.apply(*analysis_, times, envelope_step, *smoothed_);
        drawn = smoothed_.get();
        upload_spectrum = fresh || moved || !gpu_holds_smoothed_;
        gpu_holds_smoothed_ = true;
    } else {
        envelope_.reset();
        // Switched off with an eased spectrum still on the GPU: put the analysis frame's own back once.
        upload_spectrum = fresh || (gpu_holds_smoothed_ && have_analysis_);
        gpu_holds_smoothed_ = false;
    }

    // The size the preset is drawing at, which at anything below MP_QUALITY_HIGH is smaller than the panel.
    // A preset must see the pixels it is actually filling: the waveform's thickness and the radial spectrum's
    // hub are in pixels, and handing them the panel's size would make a half-scale picture draw a half-width
    // line that the compositor then stretches back to the thickness the parameter asked for - a lower tier
    // that changed the composition rather than only the resolution.
    const uint32_t rw = render_width_ > 0 ? render_width_ : width_;
    const uint32_t rh = render_height_ > 0 ? render_height_ : height_;
    frame_constants c{};
    c.viewport[0] = static_cast<float>(rw);
    c.viewport[1] = static_cast<float>(rh);
    c.viewport[2] = rw > 0 ? 1.0f / static_cast<float>(rw) : 0.0f;
    c.viewport[3] = rh > 0 ? 1.0f / static_cast<float>(rh) : 0.0f;
    c.timing[0] = static_cast<float>(seconds);
    c.timing[1] = static_cast<float>(delta);
    c.timing[2] = static_cast<float>(frames_.load(std::memory_order_relaxed));
    c.timing[3] = have_analysis_ ? static_cast<float>(analysis_sequence_) : 0.0f;
    if (have_analysis_) {
        c.level[0] = drawn->rms;
        c.level[1] = drawn->peak;
        c.level[2] = drawn->spectral_centroid_hz;
        c.level[3] = drawn->harmonic_ratio;
        c.counts[0] = drawn->onset != 0 ? 1.0f : 0.0f;
        std::memcpy(c.bands, drawn->bands, sizeof drawn->bands);
    }
    c.counts[1] = static_cast<float>(MP_ANALYSIS_OCTAVE_BANDS);
    c.counts[2] = static_cast<float>(MP_ANALYSIS_SPECTRUM_BINS);
    c.counts[3] = static_cast<float>(MP_ANALYSIS_WAVEFORM_SAMPLES);
    for (size_t i = 0; i < k_max_preset_params; ++i) {
        c.params[i] = param_values_[i].load(std::memory_order_relaxed);
    }
    // One acquire load a frame; the lock is taken only on the frame after a set_theme, so the sixteen floats
    // always reach a shader as the one palette they were set as rather than as four colours in mid-change.
    if (const uint32_t generation = theme_generation_.load(std::memory_order_acquire); generation != theme_seen_) {
        std::lock_guard lock{theme_mutex_};
        theme_render_ = theme_;
        // Re-read under the lock, because that is the generation the copy just taken corresponds to.
        theme_seen_ = theme_generation_.load(std::memory_order_relaxed);
    }
    std::memcpy(c.theme, theme_render_.data(), sizeof c.theme);

    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (SUCCEEDED(context_->Map(constants_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
        std::memcpy(mapped.pData, &c, sizeof c);
        context_->Unmap(constants_.Get(), 0);
    }
    if (upload_spectrum && SUCCEEDED(context_->Map(spectrum_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
        std::memcpy(mapped.pData, drawn->spectrum, sizeof drawn->spectrum);
        context_->Unmap(spectrum_.Get(), 0);
    }
    if (!fresh) {
        return; // the waveform on the GPU is already this frame's; the envelope never touches it
    }
    if (SUCCEEDED(context_->Map(waveform_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
        std::memcpy(mapped.pData, analysis_->waveform, sizeof analysis_->waveform);
        context_->Unmap(waveform_.Get(), 0);
    }
}

void renderer::render_frame(double seconds, double delta, int64_t now_qpc) {
    const compiled_preset* preset = render_preset_.get();
    if (preset == nullptr) {
        return; // nothing has compiled yet; the loop keeps the target clear of stale content below
    }
    update_frame_resources(seconds, delta, now_qpc);

    ID3D11RenderTargetView* rtv = rtv_.Get();
    context_->OMSetRenderTargets(1, &rtv, nullptr);
    context_->ClearRenderTargetView(rtv, preset->source.clear);
    // The quality tier's rectangle, not the panel's. Rasterisation is clipped to the viewport, so this is
    // where the per-pixel saving comes from: a Low tier at 1920x1080 rasterises 960x540 pixels and the
    // compositor stretches them (apply_render_scale).
    D3D11_VIEWPORT vp{};
    vp.Width = static_cast<float>(render_width_ > 0 ? render_width_ : width_);
    vp.Height = static_cast<float>(render_height_ > 0 ? render_height_ : height_);
    vp.MaxDepth = 1.0f;
    context_->RSSetViewports(1, &vp);
    context_->IASetInputLayout(nullptr);
    context_->IASetPrimitiveTopology(preset->source.triangle_strip ? D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP
                                                                   : D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ID3D11Buffer* cb = constants_.Get();
    ID3D11ShaderResourceView* srvs[] = {spectrum_srv_.Get(), waveform_srv_.Get()};
    context_->VSSetShader(preset->vs.Get(), nullptr, 0);
    context_->VSSetConstantBuffers(0, 1, &cb);
    context_->VSSetShaderResources(0, 2, srvs);
    context_->PSSetShader(preset->ps.Get(), nullptr, 0);
    context_->PSSetConstantBuffers(0, 1, &cb);
    context_->PSSetShaderResources(0, 2, srvs);
    context_->DrawInstanced(preset->source.vertex_count, preset->source.instance_count, 0, 0);
}

// Answers a capture request latched at the top of this iteration, from the frame just rendered and before
// Present, because a flip-model back buffer is not readable afterwards.
void renderer::serve_capture() {
    com_ptr<ID3D11Texture2D> source = offscreen_;
    if (!source && swap_chain_) {
        swap_chain_->GetBuffer(0, __uuidof(ID3D11Texture2D), &source);
    }
    if (!source) {
        return;
    }
    if (capture_full_.load(std::memory_order_acquire)) {
        serve_full_capture(source.Get());
        return;
    }
    if (!capture_staging_) {
        D3D11_TEXTURE2D_DESC td{};
        td.Width = 1;
        td.Height = 1;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_STAGING;
        td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (FAILED(device_->CreateTexture2D(&td, nullptr, &capture_staging_))) {
            capture_pending_.store(false, std::memory_order_release);
            return;
        }
    }
    const uint32_t x = std::min(capture_x_.load(std::memory_order_relaxed), width_ - 1);
    const uint32_t y = std::min(capture_y_.load(std::memory_order_relaxed), height_ - 1);
    D3D11_BOX box{};
    box.left = x;
    box.right = x + 1;
    box.top = y;
    box.bottom = y + 1;
    box.back = 1;
    context_->CopySubresourceRegion(capture_staging_.Get(), 0, 0, 0, 0, source.Get(), 0, &box);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (SUCCEEDED(context_->Map(capture_staging_.Get(), 0, D3D11_MAP_READ, 0, &mapped))) {
        uint32_t packed = 0;
        std::memcpy(&packed, mapped.pData, sizeof packed);
        context_->Unmap(capture_staging_.Get(), 0);
        capture_value_.store(packed, std::memory_order_relaxed);
        capture_pending_.store(false, std::memory_order_release);
        capture_done_.store(true, std::memory_order_release);
        return;
    }
    capture_pending_.store(false, std::memory_order_release);
}

// The whole target rather than one pixel, for the golden-image test. Same place in the loop and the same
// staging-then-Map shape as above; the only differences are a texture the size of the target, reused across
// captures, and a row-by-row copy because a staging texture's pitch is not its width.
void renderer::serve_full_capture(ID3D11Texture2D* source) {
    if (width_ == 0 || height_ == 0) {
        capture_pending_.store(false, std::memory_order_release);
        return;
    }
    if (!capture_frame_staging_ || capture_frame_staging_width_ != width_ || capture_frame_staging_height_ != height_) {
        capture_frame_staging_.Reset();
        D3D11_TEXTURE2D_DESC td{};
        td.Width = width_;
        td.Height = height_;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_STAGING;
        td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        if (FAILED(device_->CreateTexture2D(&td, nullptr, &capture_frame_staging_))) {
            capture_pending_.store(false, std::memory_order_release);
            return;
        }
        capture_frame_staging_width_ = width_;
        capture_frame_staging_height_ = height_;
    }

    context_->CopyResource(capture_frame_staging_.Get(), source);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (FAILED(context_->Map(capture_frame_staging_.Get(), 0, D3D11_MAP_READ, 0, &mapped))) {
        capture_pending_.store(false, std::memory_order_release);
        return;
    }
    const size_t row_bytes = static_cast<size_t>(width_) * 4u;
    try {
        capture_pixels_.resize(row_bytes * height_);
    } catch (const std::exception&) {
        context_->Unmap(capture_frame_staging_.Get(), 0);
        capture_pending_.store(false, std::memory_order_release);
        return;
    }
    const auto* src = static_cast<const uint8_t*>(mapped.pData);
    for (uint32_t y = 0; y < height_; ++y) {
        std::memcpy(capture_pixels_.data() + row_bytes * y, src + static_cast<size_t>(mapped.RowPitch) * y, row_bytes);
    }
    context_->Unmap(capture_frame_staging_.Get(), 0);
    capture_pixels_width_ = width_;
    capture_pixels_height_ = height_;
    capture_pending_.store(false, std::memory_order_release);
    capture_done_.store(true, std::memory_order_release);
}

void renderer::record_frame_time(int64_t now) {
    if (last_frame_qpc_ != 0) {
        const auto us = static_cast<uint32_t>((now - last_frame_qpc_) * 1'000'000 / qpc_freq());
        frame_us_last_.store(us, std::memory_order_relaxed);
        frame_us_total_.fetch_add(us, std::memory_order_relaxed);
        uint32_t prev = frame_us_max_.load(std::memory_order_relaxed);
        while (us > prev && !frame_us_max_.compare_exchange_weak(prev, us, std::memory_order_relaxed)) {
        }
        // Buckets: <8.4, <16.7, <20, <33.4, <50, >=50 ms.
        constexpr uint32_t edges[] = {8'400, 16'700, 20'000, 33'400, 50'000};
        size_t bucket = MP_RENDER_HISTOGRAM_BUCKETS - 1;
        for (size_t i = 0; i < 5; ++i) {
            if (us < edges[i]) {
                bucket = i;
                break;
            }
        }
        histogram_[bucket].fetch_add(1, std::memory_order_relaxed);
    }
    last_frame_qpc_ = now;

    ++fps_window_frames_;
    const int64_t window = now - fps_window_start_qpc_;
    if (window >= qpc_freq()) {
        fps_x100_.store(static_cast<uint32_t>(fps_window_frames_ * 100 * qpc_freq() / window),
                        std::memory_order_relaxed);
        fps_window_start_qpc_ = now;
        fps_window_frames_ = 0;
    }
}

// ---- adaptive quality (E4-S7) --------------------------------------------------------------------

mp_result renderer::create_gpu_timing() {
    D3D11_QUERY_DESC desc{};
    for (auto& slot : gpu_timing_) {
        desc.Query = D3D11_QUERY_TIMESTAMP_DISJOINT;
        if (FAILED(device_->CreateQuery(&desc, slot.disjoint.GetAddressOf()))) {
            return MP_E_D3D;
        }
        desc.Query = D3D11_QUERY_TIMESTAMP;
        if (FAILED(device_->CreateQuery(&desc, slot.begin.GetAddressOf())) ||
            FAILED(device_->CreateQuery(&desc, slot.end.GetAddressOf()))) {
            return MP_E_D3D;
        }
    }
    return MP_OK;
}

void renderer::begin_gpu_timing() {
    if (!gpu_timing_ok_) {
        return;
    }
    gpu_timing_slot& slot = gpu_timing_[gpu_timing_slot_];
    context_->Begin(slot.disjoint.Get());
    context_->End(slot.begin.Get());
}

void renderer::end_gpu_timing() {
    if (!gpu_timing_ok_) {
        return;
    }
    gpu_timing_slot& slot = gpu_timing_[gpu_timing_slot_];
    context_->End(slot.end.Get());
    context_->End(slot.disjoint.Get());
    slot.issued = true;
    // Three slots and a read two frames behind, so GetData below never has to wait for the GPU: by the time
    // this slot comes round again its results are long finished.
    gpu_timing_slot_ = (gpu_timing_slot_ + 1) % k_gpu_timing_slots;
}

bool renderer::take_frame_cost(int64_t now_qpc, double& out_ms, bool& out_from_gpu) {
    if (gpu_timing_ok_) {
        // gpu_timing_slot_ has just been advanced past the frame we issued, so it is the oldest issued slot.
        gpu_timing_slot& slot = gpu_timing_[gpu_timing_slot_];
        bool got = false;
        double ms = 0.0;
        if (slot.issued) {
            D3D11_QUERY_DATA_TIMESTAMP_DISJOINT disjoint{};
            UINT64 begin = 0;
            UINT64 end = 0;
            const bool ready =
                context_->GetData(slot.disjoint.Get(), &disjoint, sizeof disjoint, D3D11_ASYNC_GETDATA_DONOTFLUSH) ==
                    S_OK &&
                context_->GetData(slot.begin.Get(), &begin, sizeof begin, D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK &&
                context_->GetData(slot.end.Get(), &end, sizeof end, D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK;
            if (ready) {
                slot.issued = false;
                // A disjoint interval is one the clock changed frequency across, so the numbers in it are not
                // a duration and the pair is dropped.
                if (disjoint.Disjoint == 0 && disjoint.Frequency != 0 && end >= begin) {
                    ms = static_cast<double>(end - begin) * 1000.0 / static_cast<double>(disjoint.Frequency);
                    got = true;
                }
            }
        }
        if (got) {
            gpu_timing_misses_ = 0;
            // A frame can be cheaper than the timestamp clock can resolve, and on WARP at a low tier it
            // routinely is - the two stamps come back equal. Zero is not a cost the controller will accept
            // (it refuses anything that is not a positive number), and a controller fed nothing at all stops
            // deciding and reports no cost, which is what a first cut of this did at 960x540. So an
            // unresolvably cheap frame is reported as the smallest cost there is rather than as no reading.
            out_ms = std::max(ms, 1.0e-4);
            out_from_gpu = true;
            return true;
        }
        // Not ready yet is normal for the first frames after creation. Never usable - never ready, or always
        // disjoint - is a device whose timestamps do not work, and two seconds at 60 Hz tells the two apart.
        if (++gpu_timing_misses_ < 120) {
            return false;
        }
        gpu_timing_ok_ = false;
        log(MP_LOG_WARN,
            "timestamp queries produced no usable reading in %u frames; adaptive quality falls back "
            "to the frame interval",
            gpu_timing_misses_);
    }
    if (last_frame_qpc_ == 0 || now_qpc <= last_frame_qpc_) {
        return false;
    }
    out_ms = static_cast<double>(now_qpc - last_frame_qpc_) * 1000.0 / static_cast<double>(qpc_freq());
    out_from_gpu = false;
    return true;
}

void renderer::apply_quality(double now_s, int64_t now_qpc) {
    if (const uint32_t generation = quality_tuning_generation_.load(std::memory_order_acquire);
        generation != quality_tuning_seen_) {
        std::lock_guard lock{quality_tuning_mutex_};
        quality_.set_tuning(quality_tuning_);
        quality_tuning_seen_ = quality_tuning_generation_.load(std::memory_order_relaxed);
    }
    if (const uint32_t policy = quality_policy_.load(std::memory_order_acquire); policy != quality_policy_seen_) {
        quality_.set_policy(static_cast<mp_quality_policy>(policy));
        quality_policy_seen_ = policy;
    }
    if (quality_model_dirty_) {
        quality_.reset(now_s);
        quality_model_dirty_ = false;
    }

    double cost_ms = 0.0;
    bool from_gpu = false;
    if (take_frame_cost(now_qpc, cost_ms, from_gpu)) {
        quality_.observe(now_s, cost_ms);
        stat_cost_source_.store(
            static_cast<uint8_t>(from_gpu ? MP_RENDER_COST_GPU_TIMESTAMP : MP_RENDER_COST_FRAME_INTERVAL),
            std::memory_order_relaxed);
    }
    stat_quality_tier_.store(static_cast<uint8_t>(quality_.tier()), std::memory_order_relaxed);
    stat_quality_changes_.store(quality_.changes(), std::memory_order_relaxed);
    stat_frame_cost_ns_.store(static_cast<uint32_t>(std::min(quality_.smoothed_ms() * 1'000'000.0, 4.0e9)),
                              std::memory_order_relaxed);
}

void renderer::apply_render_scale() {
    const double scale = static_cast<double>(quality_.render_scale());
    const uint32_t rw =
        std::clamp(static_cast<uint32_t>(std::lround(static_cast<double>(width_) * scale)), 1u, std::max(1u, width_));
    const uint32_t rh =
        std::clamp(static_cast<uint32_t>(std::lround(static_cast<double>(height_) * scale)), 1u, std::max(1u, height_));
    if (rw == render_width_ && rh == render_height_) {
        return;
    }
    render_width_ = rw;
    render_height_ = rh;
    if (swap_chain_) {
        // A flip-model swap chain scales its source rectangle onto the panel, so drawing smaller costs one
        // call: no intermediate render target, no upscale pass, no sampler, and nothing new for AC-118's
        // device-reference count to see. The buffers stay the panel's size, which is what makes going back up
        // free too.
        swap_chain_->SetSourceSize(rw, rh);
    }
    stat_render_width_.store(rw, std::memory_order_relaxed);
    stat_render_height_.store(rh, std::memory_order_relaxed);
    stat_render_scale_x1000_.store(static_cast<uint32_t>(std::lround(scale * 1000.0)), std::memory_order_relaxed);
}

void renderer::collect_dxgi_statistics() {
    DXGI_FRAME_STATISTICS stats{};
    if (FAILED(swap_chain_->GetFrameStatistics(&stats))) {
        return; // DXGI_ERROR_FRAME_STATISTICS_DISJOINT right after creation or a mode change
    }
    dxgi_present_count_.store(stats.PresentCount, std::memory_order_relaxed);
    if (have_refresh_count_ && stats.PresentRefreshCount > last_refresh_count_ + 1) {
        dxgi_missed_.fetch_add(stats.PresentRefreshCount - last_refresh_count_ - 1, std::memory_order_relaxed);
    }
    if (stats.PresentRefreshCount != 0) {
        last_refresh_count_ = stats.PresentRefreshCount;
        have_refresh_count_ = true;
    }
}

} // namespace mp::render
