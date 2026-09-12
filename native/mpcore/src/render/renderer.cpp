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
    catalog_.clear();
    catalog_.push_back(builtin_preset());
    preset_root_ = default_preset_root();
    std::vector<std::string> warnings;
    for (auto& found : scan_preset_root(preset_root_, warnings)) {
        if (found.id == builtin_preset().id) {
            warnings.push_back("a preset on disk claims the built-in id \"" + found.id + "\"; ignoring it");
            continue;
        }
        catalog_.push_back(std::move(found));
    }
    for (const auto& warning : warnings) {
        log(MP_LOG_WARN, "preset: %s", warning.c_str());
    }
    log(MP_LOG_INFO, "preset root %s: %zu preset(s) including the built-in",
        preset_root_.empty() ? "(none)" : preset_root_.string().c_str(), catalog_.size());
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
        render_frame(static_cast<double>(now - start) / static_cast<double>(qpc_freq()),
                     static_cast<double>(now - previous) / static_cast<double>(qpc_freq()));
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
            collect_dxgi_statistics();
        } else {
            // Headless: pace to roughly 60 fps so the numbers resemble a real display without burning a core.
            context_->Flush();
            Sleep(vsync_ ? 16 : 0);
        }
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
}

void renderer::update_frame_resources(double seconds, double delta) {
    bool fresh = false;
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
            }
        }
    } else if (engine_ != nullptr) {
        analysis_->struct_size = sizeof(mp_analysis_frame);
        if (mp_analysis_try_get_latest(engine_, analysis_.get()) == MP_OK) {
            fresh = !have_analysis_ || analysis_->sequence != analysis_sequence_;
            analysis_sequence_ = analysis_->sequence;
            have_analysis_ = true;
        }
    }

    frame_constants c{};
    c.viewport[0] = static_cast<float>(width_);
    c.viewport[1] = static_cast<float>(height_);
    c.viewport[2] = width_ > 0 ? 1.0f / static_cast<float>(width_) : 0.0f;
    c.viewport[3] = height_ > 0 ? 1.0f / static_cast<float>(height_) : 0.0f;
    c.timing[0] = static_cast<float>(seconds);
    c.timing[1] = static_cast<float>(delta);
    c.timing[2] = static_cast<float>(frames_.load(std::memory_order_relaxed));
    c.timing[3] = have_analysis_ ? static_cast<float>(analysis_sequence_) : 0.0f;
    if (have_analysis_) {
        c.level[0] = analysis_->rms;
        c.level[1] = analysis_->peak;
        c.level[2] = analysis_->spectral_centroid_hz;
        c.level[3] = analysis_->harmonic_ratio;
        c.counts[0] = analysis_->onset != 0 ? 1.0f : 0.0f;
        std::memcpy(c.bands, analysis_->bands, sizeof analysis_->bands);
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
    if (!fresh) {
        return; // the spectrum and waveform on the GPU are already this frame's
    }
    if (SUCCEEDED(context_->Map(spectrum_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
        std::memcpy(mapped.pData, analysis_->spectrum, sizeof analysis_->spectrum);
        context_->Unmap(spectrum_.Get(), 0);
    }
    if (SUCCEEDED(context_->Map(waveform_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
        std::memcpy(mapped.pData, analysis_->waveform, sizeof analysis_->waveform);
        context_->Unmap(waveform_.Get(), 0);
    }
}

void renderer::render_frame(double seconds, double delta) {
    const compiled_preset* preset = render_preset_.get();
    if (preset == nullptr) {
        return; // nothing has compiled yet; the loop keeps the target clear of stale content below
    }
    update_frame_resources(seconds, delta);

    ID3D11RenderTargetView* rtv = rtv_.Get();
    context_->OMSetRenderTargets(1, &rtv, nullptr);
    context_->ClearRenderTargetView(rtv, preset->source.clear);
    D3D11_VIEWPORT vp{};
    vp.Width = static_cast<float>(width_);
    vp.Height = static_cast<float>(height_);
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
