#include "render/renderer.h"

#include "abi/last_error.h"
#include "common/log.h"
#include "render/swapchainpanel_native.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <d3dcompiler.h>

#include <windows.h>

namespace mp::render {
namespace {

constexpr uint32_t k_bars = 64;

// Runtime-compiled (D3DCompile) as the design prescribes for presets; the spike scene is the first preset.
constexpr const char* k_shader = R"hlsl(
cbuffer Frame : register(b0) {
    float4 heights[16];
    float2 viewport;
    float time;
    float pad;
};
struct VSOut { float4 pos : SV_Position; float3 color : COLOR; };
VSOut VSMain(uint vid : SV_VertexID, uint iid : SV_InstanceID) {
    float h = heights[iid / 4][iid % 4];
    float w = 2.0 / 64.0;
    float x0 = -1.0 + iid * w + w * 0.1;
    float x1 = x0 + w * 0.8;
    float y0 = -1.0;
    float y1 = -1.0 + h * 2.0;
    float2 corners[6] = { float2(x0, y0), float2(x0, y1), float2(x1, y0), float2(x1, y0), float2(x0, y1), float2(x1, y1) };
    VSOut o;
    o.pos = float4(corners[vid], 0.0, 1.0);
    float t = iid / 64.0;
    o.color = float3(0.15 + 0.85 * t, 0.55 + 0.2 * h, 1.0 - 0.8 * t);
    return o;
}
float4 PSMain(VSOut i) : SV_Target { return float4(i.color, 1.0); }
)hlsl";

struct frame_constants {
    float heights[k_bars];
    float viewport[2];
    float time;
    float pad;
};

mp_result d3d_fail(const char* what, HRESULT hr) {
    char text[256];
    std::snprintf(text, sizeof text, "%s failed: HRESULT 0x%08lX", what, static_cast<unsigned long>(hr));
    mp::abi::set_last_error(text);
    log(MP_LOG_ERROR, "%s", text);
    return MP_E_D3D;
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

} // namespace

// ---- lifetime -----------------------------------------------------------------------------------

mp_result renderer::create(void* swap_chain_panel_native, const mp_renderer_config& config,
                           std::unique_ptr<renderer>& out) {
    std::unique_ptr<renderer> r{new renderer{}};
    const mp_result result = r->init(swap_chain_panel_native, config);
    if (result != MP_OK) {
        return result;
    }
    out = std::move(r);
    return MP_OK;
}

mp_result renderer::init(void* swap_chain_panel_native, const mp_renderer_config& config) {
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

    mp_result r = create_device(config.force_warp != 0);
    if (r != MP_OK) {
        return r;
    }
    r = headless_ ? create_targets(width, height) : create_swap_chain(swap_chain_panel_native, width, height);
    if (r != MP_OK) {
        return r;
    }
    r = create_scene();
    if (r != MP_OK) {
        return r;
    }
    resize_pending_.store(true); // applies the scale transform on the first frame
    log(MP_LOG_INFO, "renderer created: %s, %ux%u, %s%s", adapter_.c_str(), width, height,
        headless_ ? "headless" : "composition swap chain", warp_.load() ? " (WARP)" : "");
    thread_ = std::thread([this] { run(); });
    return MP_OK;
}

renderer::~renderer() {
    stop_.store(true, std::memory_order_release);
    if (thread_.joinable()) {
        thread_.join();
    }
    if (waitable_ != nullptr) {
        CloseHandle(waitable_);
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

mp_result renderer::create_scene() {
    com_ptr<ID3DBlob> vs_blob;
    com_ptr<ID3DBlob> ps_blob;
    com_ptr<ID3DBlob> errors;
    const UINT compile_flags = D3DCOMPILE_OPTIMIZATION_LEVEL3;
    HRESULT hr = D3DCompile(k_shader, std::strlen(k_shader), "spike.hlsl", nullptr, nullptr, "VSMain", "vs_5_0",
                            compile_flags, 0, &vs_blob, &errors);
    if (FAILED(hr)) {
        log(MP_LOG_ERROR, "VS compile: %s", errors ? static_cast<const char*>(errors->GetBufferPointer()) : "?");
        return d3d_fail("D3DCompile(VSMain)", hr);
    }
    hr = D3DCompile(k_shader, std::strlen(k_shader), "spike.hlsl", nullptr, nullptr, "PSMain", "ps_5_0", compile_flags,
                    0, &ps_blob, &errors);
    if (FAILED(hr)) {
        log(MP_LOG_ERROR, "PS compile: %s", errors ? static_cast<const char*>(errors->GetBufferPointer()) : "?");
        return d3d_fail("D3DCompile(PSMain)", hr);
    }
    hr = device_->CreateVertexShader(vs_blob->GetBufferPointer(), vs_blob->GetBufferSize(), nullptr, &vs_);
    if (FAILED(hr)) {
        return d3d_fail("CreateVertexShader", hr);
    }
    hr = device_->CreatePixelShader(ps_blob->GetBufferPointer(), ps_blob->GetBufferSize(), nullptr, &ps_);
    if (FAILED(hr)) {
        return d3d_fail("CreatePixelShader", hr);
    }

    D3D11_BUFFER_DESC bd{};
    bd.ByteWidth = sizeof(frame_constants);
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    hr = device_->CreateBuffer(&bd, nullptr, &constants_);
    if (FAILED(hr)) {
        return d3d_fail("CreateBuffer(constants)", hr);
    }
    return MP_OK;
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
    last_frame_qpc_ = 0;
    fps_window_start_qpc_ = start;
    while (!stop_.load(std::memory_order_acquire)) {
        if (!visible_.load(std::memory_order_acquire)) {
            Sleep(50);
            last_frame_qpc_ = 0; // do not count the pause as a frame gap
            continue;
        }
        if (waitable_ != nullptr) {
            WaitForSingleObjectEx(waitable_, 1000, TRUE);
        }
        if (stop_.load(std::memory_order_acquire)) {
            break;
        }
        apply_pending_resize();
        if (!rtv_) {
            Sleep(5);
            continue;
        }
        const int64_t now = qpc();
        render_frame(static_cast<double>(now - start) / static_cast<double>(qpc_freq()));
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

void renderer::render_frame(double seconds) {
    frame_constants c{};
    for (uint32_t i = 0; i < k_bars; ++i) {
        c.heights[i] = static_cast<float>(0.5 + 0.45 * std::sin(seconds * 2.0 + i * 0.3));
    }
    c.viewport[0] = static_cast<float>(width_);
    c.viewport[1] = static_cast<float>(height_);
    c.time = static_cast<float>(seconds);

    D3D11_MAPPED_SUBRESOURCE mapped{};
    if (SUCCEEDED(context_->Map(constants_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) {
        std::memcpy(mapped.pData, &c, sizeof c);
        context_->Unmap(constants_.Get(), 0);
    }

    const float clear[4] = {0.04f, 0.04f, 0.06f, 1.0f};
    ID3D11RenderTargetView* rtv = rtv_.Get();
    context_->OMSetRenderTargets(1, &rtv, nullptr);
    context_->ClearRenderTargetView(rtv, clear);
    D3D11_VIEWPORT vp{};
    vp.Width = static_cast<float>(width_);
    vp.Height = static_cast<float>(height_);
    vp.MaxDepth = 1.0f;
    context_->RSSetViewports(1, &vp);
    context_->IASetInputLayout(nullptr);
    context_->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context_->VSSetShader(vs_.Get(), nullptr, 0);
    ID3D11Buffer* cb = constants_.Get();
    context_->VSSetConstantBuffers(0, 1, &cb);
    context_->PSSetShader(ps_.Get(), nullptr, 0);
    context_->DrawInstanced(6, k_bars, 0, 0);
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
