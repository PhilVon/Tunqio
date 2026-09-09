// D3D11 renderer behind a WinUI 3 SwapChainPanel (ADR-002): flip-model composition swap chain with a frame-latency
// waitable object, one dedicated render thread, ResizeBuffers on that thread. First cut from the E0-S5 spike:
// draws 64 animated instanced bars. E4-S3 replaces the fixed scene with the preset loader.
//
// Threading: create/destroy/resize/set_visible/get_stats are control-plane (any thread; create on the UI thread
// because ISwapChainPanelNative::SetSwapChain wants it). Only the render thread touches the device context.
#pragma once

#include "mpcore.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <wrl/client.h>

namespace mp::render {

class renderer {
public:
    static mp_result create(void* swap_chain_panel_native, const mp_renderer_config& config,
                            std::unique_ptr<renderer>& out);
    ~renderer();

    renderer(const renderer&) = delete;
    renderer& operator=(const renderer&) = delete;

    void resize(uint32_t width, uint32_t height, float scale_x, float scale_y) noexcept;
    void set_visible(bool visible) noexcept;
    void get_stats(mp_render_stats& out) const noexcept;

private:
    renderer() = default;
    mp_result init(void* swap_chain_panel_native, const mp_renderer_config& config);
    mp_result create_device(bool force_warp);
    mp_result create_swap_chain(void* swap_chain_panel_native, uint32_t width, uint32_t height);
    mp_result create_scene();
    mp_result create_targets(uint32_t width, uint32_t height);
    void apply_pending_resize();
    void render_frame(double seconds);
    void record_frame_time(int64_t now_qpc);
    void collect_dxgi_statistics();
    void run();

    template <typename T> using com_ptr = Microsoft::WRL::ComPtr<T>;

    com_ptr<ID3D11Device> device_;
    com_ptr<ID3D11DeviceContext> context_;
    com_ptr<IDXGISwapChain2> swap_chain_;
    HANDLE waitable_ = nullptr;
    com_ptr<ID3D11Texture2D> offscreen_; // headless only
    com_ptr<ID3D11RenderTargetView> rtv_;
    com_ptr<ID3D11VertexShader> vs_;
    com_ptr<ID3D11PixelShader> ps_;
    com_ptr<ID3D11Buffer> constants_;

    bool headless_ = false;
    bool vsync_ = true;
    std::atomic<bool> warp_{false};
    std::string adapter_;

    // Pending resize written by the control plane, consumed by the render thread.
    std::atomic<uint32_t> pending_width_{0};
    std::atomic<uint32_t> pending_height_{0};
    std::atomic<float> pending_scale_x_{1.0f};
    std::atomic<float> pending_scale_y_{1.0f};
    std::atomic<bool> resize_pending_{false};
    uint32_t width_ = 0;
    uint32_t height_ = 0;

    std::atomic<bool> visible_{true};
    std::atomic<bool> stop_{false};
    std::atomic<bool> device_lost_{false};
    std::thread thread_;

    // Statistics (render thread writes, control plane reads).
    std::atomic<uint64_t> frames_{0};
    std::atomic<uint64_t> resizes_{0};
    std::atomic<uint32_t> fps_x100_{0};
    std::atomic<uint32_t> frame_us_last_{0};
    std::atomic<uint32_t> frame_us_max_{0};
    std::atomic<uint64_t> frame_us_total_{0};
    std::array<std::atomic<uint32_t>, MP_RENDER_HISTOGRAM_BUCKETS> histogram_{};
    std::atomic<uint64_t> dxgi_present_count_{0};
    std::atomic<uint64_t> dxgi_missed_{0};
    int64_t last_frame_qpc_ = 0;
    int64_t fps_window_start_qpc_ = 0;
    uint64_t fps_window_frames_ = 0;
    uint32_t last_refresh_count_ = 0;
    bool have_refresh_count_ = false;
};

} // namespace mp::render
