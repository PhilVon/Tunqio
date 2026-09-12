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
#include "render/quality.h"

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
    // The parameters one catalogue entry declares, by id, on the same two-call protocol (T-142). const, and
    // about any preset rather than the active one, because a settings page describes a preset before it
    // switches to it.
    mp_result enum_preset_params(const char* utf8_preset_id, mp_preset_param_info* out, uint32_t* count) const;
    // A second root scanned in addition to the one beside the module (the shell's user preset directory), and
    // the rescan that makes a preset dropped in while the app runs visible. Both rebuild the catalogue; neither
    // touches what is drawing, which is already compiled and stays on the target.
    mp_result set_user_preset_root(const char* utf8_path);
    uint32_t rescan_presets();
    // Compiles `id` and, only if that succeeds, hands it to the render thread. On a compile error the previous
    // preset keeps drawing and the compiler's diagnostic is the thread's last error.
    mp_result set_preset(const char* utf8_id);
    // Sets a parameter the active preset declares. MP_E_INVALID_ARG names the parameter when it declares none.
    mp_result set_param(const char* utf8_name, float value);
    // The renderer-wide theme (E4-S6): four RGBA colours into b0's `theme`, surviving preset switches, so every
    // preset and the shell's own background gradient are painted from one palette.
    mp_result set_theme(const mp_theme_colors& colors);
    // Adaptive quality (E4-S7). MP_QUALITY_AUTO hands the tier to the controller in render/quality.h; the
    // other three pin it. Takes effect on the render thread's next frame.
    mp_result set_quality(mp_quality_policy policy);
    // Audio-to-picture sync (E4-S8): which of the analysis frames it has seen the render thread draws, and
    // whether it keeps a record of what it drew. Both take effect on the next frame.
    mp_result set_av_sync(const mp_av_sync_config& config);
    mp_result drain_latency(mp_latency_sample* out, uint32_t* count);
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
    // The controller's numbers, so a test can set a budget this machine provably cannot meet at High and
    // provably can meet after it changes - which is what lets AC-128 be an end-to-end measurement on the real
    // rasteriser without asserting an absolute frame rate on a machine somebody else is also using (T-150).
    void set_quality_tuning(const quality_tuning& tuning);
    quality_tuning quality_tuning_now() const;

private:
    // One analysis frame the render thread has seen, and when it first saw it. The stamp is taken once, on the
    // poll that first produced this sequence, so a frame drawn again because nothing newer arrived still
    // reports the moment the picture's data reached this thread rather than the moment it was redrawn.
    struct analysis_slot {
        mp_analysis_frame frame{};
        int64_t first_seen_qpc = 0;
    };

    renderer() = default;
    mp_result init(mp_engine* engine, void* swap_chain_panel_native, const mp_renderer_config& config);
    mp_result create_device(bool force_warp);
    mp_result create_swap_chain(void* swap_chain_panel_native, uint32_t width, uint32_t height);
    mp_result create_frame_resources();
    mp_result create_targets(uint32_t width, uint32_t height);
    void load_catalog();
    void load_catalog_locked(); // preset_mutex_ already held
    void apply_pending_resize();
    void apply_pending_preset();
    void update_frame_resources(double seconds, double delta, int64_t now_qpc);
    void render_frame(double seconds, double delta, int64_t now_qpc);
    void serve_capture();
    void serve_full_capture(ID3D11Texture2D* source);
    void record_frame_time(int64_t now_qpc);
    void collect_dxgi_statistics();
    // Audio-to-picture sync, all three render-thread only. poll_analysis takes whatever the analysis has
    // published into the history ring; choose_analysis_frame decides which of the ring the picture is drawn
    // from; record_latency_sample writes what was drawn to the probe, after the present it is describing.
    void poll_analysis(int64_t now_qpc);
    const analysis_slot* choose_analysis_frame(int64_t now_qpc);
    void record_latency_sample();
    void refresh_mix_format();
    mp_result create_gpu_timing();
    void begin_gpu_timing();
    void end_gpu_timing();
    // The frame cost the controller decides on. True when one was produced this iteration; `out_ms` is then
    // the cost and `out_from_gpu` says whether it came from a GPU timestamp pair or from the frame interval.
    bool take_frame_cost(int64_t now_qpc, double& out_ms, bool& out_from_gpu);
    void apply_quality(double now_s, int64_t now_qpc);
    void apply_render_scale();
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
    std::filesystem::path user_preset_root_; // empty until the shell names one (mp_renderer_set_user_preset_root)
    std::vector<preset_source> catalog_;
    std::shared_ptr<const compiled_preset> current_;
    std::shared_ptr<const compiled_preset> pending_;
    std::atomic<bool> preset_pending_{false};
    std::array<std::atomic<float>, k_max_preset_params> param_values_{};
    // The theme (E4-S6). Deliberately not sixteen more relaxed atomics beside param_values_: four colours stored
    // one float at a time can be read half-applied, and a background that is one frame of someone else's red is
    // exactly the flash the accessibility contract forbids. So it is the handover the analysis override uses -
    // one atomic generation the render thread compares per frame, and the lock taken only when it has moved, so
    // a renderer whose theme never changes pays one relaxed load a frame and copies nothing.
    mutable std::mutex theme_mutex_;
    std::array<float, k_theme_slots> theme_{};
    std::atomic<uint32_t> theme_generation_{0};
    uint32_t theme_seen_ = 0;                              // render thread only
    std::array<float, k_theme_slots> theme_render_{};      // render thread only
    std::shared_ptr<const compiled_preset> render_preset_; // render thread only

    // Adaptive quality (E4-S7). The controller itself belongs to the render thread and nothing else touches
    // it; the control plane writes a policy and a tuning across, and reads the results out of the published
    // atomics below. The policy is one relaxed load a frame and the tuning is the theme's generation handover
    // - a renderer whose tuning is never set (every renderer but a test's) takes the lock exactly never.
    quality_controller quality_; // render thread only
    std::atomic<uint32_t> quality_policy_{MP_QUALITY_AUTO};
    uint32_t quality_policy_seen_ = MP_QUALITY_AUTO; // render thread only
    mutable std::mutex quality_tuning_mutex_;
    quality_tuning quality_tuning_{};
    std::atomic<uint32_t> quality_tuning_generation_{0};
    uint32_t quality_tuning_seen_ = 0; // render thread only
    // What the renderer is actually drawing at: the back buffer stays the panel's size and the picture is
    // drawn into this rectangle of it, stretched back out by the compositor (IDXGISwapChain2::SetSourceSize).
    uint32_t render_width_ = 0;  // render thread only
    uint32_t render_height_ = 0; // render thread only
    // Set when something OTHER than the controller changed what a frame costs - a resize, a preset switch -
    // so everything the controller measured about the old cost model is discarded. A tier change is not one of
    // these: that is the controller's own doing and the ratio across it is exactly what it wants to learn.
    bool quality_model_dirty_ = false; // render thread only
    // Published for mp_render_stats.
    std::atomic<uint8_t> stat_quality_tier_{static_cast<uint8_t>(MP_QUALITY_HIGH)};
    std::atomic<uint32_t> stat_quality_changes_{0};
    std::atomic<uint32_t> stat_render_width_{0};
    std::atomic<uint32_t> stat_render_height_{0};
    std::atomic<uint32_t> stat_render_scale_x1000_{1000};
    std::atomic<uint32_t> stat_frame_cost_ns_{0};
    std::atomic<uint8_t> stat_cost_source_{static_cast<uint8_t>(MP_RENDER_COST_FRAME_INTERVAL)};

    // GPU timestamp queries: the cost signal. The frame-to-frame interval is not one on the shipping path -
    // with vsync it is pinned to the refresh whatever the GPU is doing, so a controller reading it would be
    // blind exactly where the feature is supposed to work. Three slots, read two frames behind so GetData
    // never stalls the loop. When the device will not make the queries, or keeps reporting them disjoint, the
    // frame interval is the fallback and mp_render_stats.cost_source says so.
    static constexpr size_t k_gpu_timing_slots = 3;
    struct gpu_timing_slot {
        com_ptr<ID3D11Query> disjoint;
        com_ptr<ID3D11Query> begin;
        com_ptr<ID3D11Query> end;
        bool issued = false;
    };
    std::array<gpu_timing_slot, k_gpu_timing_slots> gpu_timing_{};
    size_t gpu_timing_slot_ = 0;     // render thread only
    bool gpu_timing_ok_ = false;     // render thread only
    uint32_t gpu_timing_misses_ = 0; // render thread only: consecutive frames with no usable result

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

    // ---- audio-to-picture sync (E4-S8) --------------------------------------------------------------
    // The analysis frames this thread has seen, newest last, so it can draw one that is NOT the newest. The
    // depth is what bounds how far behind the mixer the picture may be asked to sit: the analysis publishes at
    // 93.75 Hz and this thread polls at the frame rate, so 32 entries is half a second at 60 Hz and a third of
    // a second at 93.75, and the offset is clamped inside it rather than silently truncated. One slot more
    // than that is allocated and never counted: it is where a poll lands before its sequence has been read,
    // so a poll that turns out to hold the frame already at the head does not overwrite the OLDEST one.
    static constexpr size_t k_analysis_history = 32;
    // What mp_av_sync_config.probe_capacity is clamped to. 4096 samples is about a minute of 60 fps and 1 MB;
    // a harness that wants longer than that drains as it goes, which is what the ring is for.
    static constexpr uint32_t k_max_probe_samples = 4096;
    std::unique_ptr<std::array<analysis_slot, k_analysis_history + 1>> analysis_history_;
    size_t analysis_history_count_ = 0; // render thread only
    size_t analysis_history_next_ = 0;  // render thread only; also the scratch slot
    int64_t drawn_first_seen_qpc_ = 0;  // render thread only: provenance of what analysis_ now holds
    bool drawn_repeat_ = false;         // render thread only: the picture drew the same frame again
    // The mixer's format, cached because it is how three byte positions become milliseconds and it changes
    // only when the output device does. Refreshed on the first frame and about once a second after it.
    double byte_rate_ = 0.0;        // render thread only: mixer bytes per second
    double bytes_per_frame_ = 0.0;  // render thread only: channels * 4
    uint32_t mix_sample_rate_ = 0;  // render thread only: frames per second
    uint64_t mix_format_frame_ = 0; // render thread only: the frame the cache was last refreshed on
    std::atomic<uint32_t> av_sync_mode_{static_cast<uint32_t>(MP_AV_SYNC_AUDIBLE)};
    std::atomic<float> av_sync_offset_ms_{0.0f};
    // The probe. Capacity 0 - the default, and what ships - is the whole switch: the render thread reads one
    // relaxed atomic per frame and does nothing else, taking neither the lock nor the two clock readings.
    std::atomic<uint32_t> probe_capacity_{0};
    mutable std::mutex probe_mutex_;
    std::vector<mp_latency_sample> probe_;
    size_t probe_head_ = 0;  // index of the oldest sample under probe_mutex_
    size_t probe_count_ = 0; // how many are held under probe_mutex_

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
