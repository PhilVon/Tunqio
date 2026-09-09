// The BASS-backed engine (E0-S4 first cut, E1-S1 skeleton). This header exposes only mpcore ABI types;
// BASS headers are included by bass_engine.cpp alone (docs/solution-structure.md dependency rule,
// enforced by the include-grep test).
#pragma once

#include "mpcore.h"

#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace mp::audio {

class engine;

struct track {
    uint32_t stream = 0; // HSTREAM (decode)
    mp_track_info info{};
    std::wstring path;
    engine* owner = nullptr;
};

class engine {
public:
    // Guard fade length (ADR-003 item 5): pause, resume, stop, seek and a manual play over a running source.
    static constexpr uint32_t k_guard_fade_ms = 50;

    // One engine per process (BASS is process-global). Returns MP_E_STATE when one already exists.
    static mp_result create(const mp_engine_config& config, std::unique_ptr<engine>& out);
    ~engine();

    engine(const engine&) = delete;
    engine& operator=(const engine&) = delete;

    mp_result set_output(const mp_output_config& config);
    mp_result enum_devices(mp_device_info* out, uint32_t* count);
    void set_event_callback(mp_event_cb callback, void* user);

    mp_result open_track(const char* utf8_path, track*& out);
    mp_result close_track(track* t);
    bool owns(const track* t) const;

    mp_result play(track* t, int64_t start_ms);
    mp_result pause();
    mp_result resume();
    mp_result stop(mp_fade_mode fade);
    mp_result seek(int64_t position_ms);
    void set_volume(float slider);

    mp_result get_clock(mp_clock& out) const;
    mp_result get_stats(mp_engine_stats& out) const;

    // MP_DEVICE_NONE only: what the output thread would have pulled. `frames` interleaved float frames.
    mp_result render(float* out_interleaved, uint32_t frames);

    // Slider position to gain (audio taper, see mp_engine_set_volume). Exposed for the tests.
    static float volume_taper(float slider) noexcept;

private:
    engine() = default;
    mp_result init(const mp_engine_config& config);
    mp_result create_mixer(uint32_t rate, uint32_t channels);
    void load_plugins(const std::wstring& dir);
    void free_output() noexcept;
    void emit(mp_event_type type, int64_t a, int64_t b, const char* message) noexcept;

    // Control plane: fade the envelope to silence and, on a live device, wait for the audio thread to get there.
    void guard_out();
    void begin_fade_in() noexcept;

    // The pull stage shared by the WASAPI callback and render(): mixer -> envelope -> volume. Real-time.
    void pull(void* buffer, uint32_t bytes) noexcept;

    static unsigned long __stdcall output_proc(void* buffer, unsigned long length, void* user);
    static void __stdcall end_sync(unsigned long handle, unsigned long channel, unsigned long data, void* user);

    mutable std::mutex control_; // control-plane calls; never taken on the audio thread

    uint32_t mixer_ = 0; // HSTREAM (decode, nonstop)
    uint32_t mixer_rate_ = 48000;
    uint32_t mixer_channels_ = 2;
    std::vector<uint32_t> plugins_;
    std::vector<std::unique_ptr<track>> tracks_;

    track* current_ = nullptr;         // control plane
    std::atomic<bool> playing_{false}; // read by the audio thread for underrun accounting

    // Envelope (guard fades) and volume. The audio thread owns env_level_ and volume_current_; the control
    // thread only writes targets and reads the level back to know when a fade has landed.
    std::atomic<float> env_target_{1.0f};
    std::atomic<float> env_level_{1.0f};
    std::atomic<uint32_t> fade_frames_{2400};
    std::atomic<bool> pause_pending_{false}; // fade out, then hold
    std::atomic<bool> hold_{false};          // paused: the pull stage emits silence and leaves the mixer alone
    std::atomic<float> volume_target_{1.0f}; // taper applied
    float volume_current_ = 1.0f;            // audio thread only

    bool output_open_ = false;
    bool output_started_ = false;
    bool offline_ = false; // MP_DEVICE_NONE: no WASAPI; render() pulls
    bool exclusive_ = false;
    uint32_t output_rate_ = 0;
    uint32_t output_channels_ = 0;
    uint32_t output_buffer_bytes_ = 0;
    uint32_t output_format_ = 0;

    std::atomic<uint64_t> callbacks_{0};
    std::atomic<uint64_t> underruns_{0};
    std::atomic<uint32_t> callback_max_us_{0};

    mp_event_cb event_cb_ = nullptr;
    void* event_user_ = nullptr;
    std::mutex event_mutex_;
};

} // namespace mp::audio
