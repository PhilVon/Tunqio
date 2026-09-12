// D3D11 renderer behind a WinUI 3 SwapChainPanel (ADR-002): flip-model composition swap chain with a frame-latency
// waitable object, one dedicated render thread, ResizeBuffers on that thread. The device, swap chain, resize/DPI
// and statistics came from the E0-S5 spike; E4-S3 replaced the spike's fixed scene with the preset loader
// (render/preset.h), so what is drawn is whatever preset.json and its HLSL say, fed from mp_analysis_frame.
//
// Threading: create/destroy/resize/set_visible/get_stats/enum_presets/set_preset/set_param are control-plane (any
// thread; create on the UI thread because ISwapChainPanelNative::SetSwapChain wants it). Only the render thread
// touches the device context. set_preset compiles on the *calling* thread - ID3D11Device is free-threaded, and
// compiling there is what lets a compile error come back to the caller as a return value and a message instead of
// arriving on the render thread with nowhere to go (AC-117).
#pragma once

#include "mpcore.h"

#include "render/preset.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <d3d11.h>
#include <dxgi1_3.h>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <wrl/client.h>

namespace mp::render {

class renderer {
public:
    static mp_result create(mp_engine* engine, void* swap_chain_panel_native, const mp_renderer_config& config,
                            std::unique_ptr<renderer>& out);
    ~renderer();

    renderer(const renderer&) = delete;
    renderer& operator=(const renderer&) = delete;

    void resize(uint32_t width, uint32_t height, float scale_x, float scale_y) noexcept;
    void set_visible(bool visible) noexcept;
    void get_stats(mp_render_stats& out) const noexcept;

    // Two-call enumeration, as mp_engine_enum_devices does it: out == nullptr reports the total in *count;
    // otherwise at most *count entries are written and *count becomes how many were.
    mp_result enum_presets(mp_preset_info* out, uint32_t* count) const;
    // Compiles `id` and, only if that succeeds, hands it to the render thread. On a compile error the previous
    // preset keeps drawing and the compiler's diagnostic is the thread's last error.
    mp_result set_preset(const char* utf8_id);
    // Sets a parameter the active preset declares. MP_E_INVALID_ARG names the parameter when it declares none.
    mp_result set_param(const char* utf8_name, float value);
    std::string active_preset_id() const;

    // ---- diagnostics, not on the ABI (mpcore.tests compiles these sources directly) ----

    // The device's reference count. Every ID3D11DeviceChild holds one, so this is a live count of the device
    // objects that exist - which is how a leak across a visibility toggle shows up as a number (AC-118).
    uint32_t device_references() const noexcept;
    // The device itself, so a test can prove the counter above moves before trusting it to stay still.
    ID3D11Device* device() const noexcept { return device_.Get(); }
    // Copies one pixel out of the next frame the render thread produces, as B, G, R, A. False on timeout or
    // when the renderer is stopped. Used to assert *what* is on screen, not merely that frames are counted.
    bool capture_pixel(uint32_t x, uint32_t y, uint8_t out_bgra[4], int timeout_ms = 3000) noexcept;
    // The whole of the next frame, B, G, R, A per pixel, row-major and tightly packed. Same handshake and the
    // same rule: one capture in flight at a time. This is the readback half of the golden-image test (E4-S4).
    bool capture_frame(std::vector<uint8_t>& out_bgra, uint32_t& out_width, uint32_t& out_height,
                       int timeout_ms = 5000) noexcept;
    // Feeds the render thread one fixed mp_analysis_frame in place of whatever the engine has, and nullptr puts
    // it back. A golden image has to be a function of data the test chose, not of what was playing when it ran.
    void set_analysis_override(const mp_analysis_frame* frame);

private:
    renderer() = default;
    mp_result init(mp_engine* engine, void* swap_chain_panel_native, const mp_renderer_config& config);
    mp_result create_device(bool force_warp);
    mp_result create_swap_chain(void* swap_chain_panel_native, uint32_t width, uint32_t height);
    mp_result create_frame_resources();
    mp_result create_targets(uint32_t width, uint32_t height);
    void load_catalog();
    void apply_pending_resize();
    void apply_pending_preset();
    void update_frame_resources(double seconds, double delta);
    void render_frame(double seconds, double delta);
    void serve_capture();
    void serve_full_capture(ID3D11Texture2D* source);
    void record_frame_time(int64_t now_qpc);
    void collect_dxgi_statistics();
    void run();

    template <typename T> using com_ptr = Microsoft::WRL::ComPtr<T>;

    com_ptr<ID3D11Device> device_;
    com_ptr<ID3D11DeviceContext> context_;
    com_ptr<IDXGISwapChain2> swap_chain_;
    HANDLE waitable_ = nullptr;
    HANDLE wake_ = nullptr;              // set_visible(1) and shutdown wake the loop out of its hidden-state wait
    com_ptr<ID3D11Texture2D> offscreen_; // headless only
    com_ptr<ID3D11RenderTargetView> rtv_;
    com_ptr<ID3D11Buffer> constants_;
    com_ptr<ID3D11Buffer> spectrum_;
    com_ptr<ID3D11ShaderResourceView> spectrum_srv_;
    com_ptr<ID3D11Buffer> waveform_;
    com_ptr<ID3D11ShaderResourceView> waveform_srv_;
    com_ptr<ID3D11Texture2D> capture_staging_;       // 1x1, created on first capture_pixel
    com_ptr<ID3D11Texture2D> capture_frame_staging_; // target-sized, created on first capture_frame
    uint32_t capture_frame_staging_width_ = 0;
    uint32_t capture_frame_staging_height_ = 0;

    mp_engine* engine_ = nullptr; // may be NULL: the renderer then draws with no analysis frame
    bool headless_ = false;
    bool vsync_ = true;
    std::atomic<bool> warp_{false};
    std::string adapter_;

    // Presets. `catalog_` and `current_` are control plane under preset_mutex_; `pending_` is the handover to the
    // render thread, which keeps its own reference in render_preset_ and never takes the mutex on a steady frame.
    mutable std::mutex preset_mutex_;
    std::filesystem::path preset_root_;
    std::vector<preset_source> catalog_;
    std::shared_ptr<const compiled_preset> current_;
    std::shared_ptr<const compiled_preset> pending_;
    std::atomic<bool> preset_pending_{false};
    std::array<std::atomic<float>, k_max_preset_params> param_values_{};
    std::shared_ptr<const compiled_preset> render_preset_; // render thread only

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

    // Pixel capture handshake: the control plane publishes a request, the render thread answers it after the
    // frame it just drew and before Present, because a flip-model back buffer is not readable afterwards.
    std::atomic<bool> capture_pending_{false};
    std::atomic<uint32_t> capture_x_{0};
    std::atomic<uint32_t> capture_y_{0};
    std::atomic<uint32_t> capture_value_{0};
    std::atomic<bool> capture_done_{false};
    // Full-frame variant of the same handshake. capture_done_ is the release/acquire edge that publishes these.
    std::atomic<bool> capture_full_{false};
    std::vector<uint8_t> capture_pixels_;
    uint32_t capture_pixels_width_ = 0;
    uint32_t capture_pixels_height_ = 0;

    // Analysis frame the render thread reads each frame; a member because it is 6 KB and this is per-frame code.
    std::unique_ptr<mp_analysis_frame> analysis_;
    uint32_t analysis_sequence_ = 0;
    bool have_analysis_ = false;

    // The fixed frame set_analysis_override installs, if any. Atomics are what a steady frame reads; the mutex
    // is taken only when the generation has moved, so a renderer with no override pays one relaxed load a frame.
    std::mutex analysis_override_mutex_;
    std::unique_ptr<mp_analysis_frame> analysis_override_;
    std::atomic<bool> analysis_override_active_{false};
    std::atomic<uint32_t> analysis_override_generation_{0};
    uint32_t analysis_override_seen_ = 0; // render thread only

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
