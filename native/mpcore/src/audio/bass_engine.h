// The BASS-backed engine (E0-S4 first cut, E1-S1 skeleton). This header exposes only mpcore ABI types;
// BASS headers are included by bass_engine.cpp alone (docs/solution-structure.md dependency rule,
// enforced by the include-grep test).
#pragma once

#include "mpcore.h"

#include <atomic>
#include <cstdint>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace mp::audio {

class engine;

struct track {
    uint32_t stream = 0; // HSTREAM (decode): what the mixer plays
    mp_track_info info{};
    std::wstring path;
    engine* owner = nullptr;

    // T-102: an MP4 whose decoder (Media Foundation) hands out the encoder priming and padding. `stream` is then
    // a user decode stream whose STREAMPROC reads `inner`, drops trim_priming frames after a rewind and ends
    // after trim_valid frames. A user stream cannot seek, only reset to 0, so a seek resets it, moves `inner`,
    // and records in trim_origin the frame its counter 0 now stands for; every position read adds it back.
    // The audio thread owns trim_skip and trim_delivered while the wrapper is in the mixer.
    uint32_t inner = 0;
    uint64_t trim_priming = 0;
    uint64_t trim_valid = 0;
    uint64_t trim_skip = 0;
    uint64_t trim_delivered = 0;
    uint64_t trim_origin = 0;
    uint32_t frame_bytes = 0;

    // ReplayGain (E1-S5): what mp_track_set_replaygain was given and the linear gain it resolved to after clipping
    // prevention. The gain lives on the source channel (BASS_ATTRIB_VOL, applied by the mixer), so it follows the
    // track through seeks, re-attachment and the gapless join; these are for get-style reads and the tests.
    float gain_db = 0.0f;
    float peak = 0.0f; // <= 0: unknown
    float gain = 1.0f; // linear, as applied

    // Crossfade (E1-S4): an equal-power envelope in the source's own frames, applied by fade_proc, a DSP on `stream`
    // that the mixer runs as it decodes the source; it therefore multiplies with the source's BASS_ATTRIB_VOL
    // (ReplayGain) rather than replacing it. dsp_pos is the source frame the DSP will see next: the control thread
    // sets it whenever it positions the source, the audio thread advances it. A fade-in runs from frame 0 (a
    // preloaded track starts there) over fade_in_frames; a fade-out runs from fade_out_start over fade_out_frames.
    // Both are positions, so a seek needs no bookkeeping: the gain is a function of where the source is.
    static constexpr uint64_t k_no_fade = std::numeric_limits<uint64_t>::max();
    std::atomic<uint64_t> dsp_pos{0};
    std::atomic<uint64_t> fade_out_start{k_no_fade};
    std::atomic<uint32_t> fade_out_frames{0};
    std::atomic<uint32_t> fade_in_frames{0}; // 0 = none
    uint32_t fade_sync = 0;                  // HSYNC of the mix-time POS sync that starts the crossfade; 0 = none
};

class engine {
public:
    // Guard fade length (ADR-003 item 5): pause, resume, stop, seek and a manual play over a running source.
    static constexpr uint32_t k_guard_fade_ms = 50;
    // Longest user crossfade (product-scope.md: 0-12 s); mp_engine_set_crossfade clamps to it.
    static constexpr uint32_t k_max_crossfade_ms = 12000;

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
    // Queues `next`. MP_JOIN_GAPLESS: it starts at mix time exactly where the current source ends (E1-S2 join).
    // MP_JOIN_CROSSFADE: with a crossfade set and the current source's length known, it starts crossfade_ms before
    // that end and the two overlap on an equal-power curve; otherwise it is the gapless join. NULL clears.
    mp_result preload_next(track* next, mp_join_mode mode);
    // User crossfade length, 0 = off, clamped to k_max_crossfade_ms. Applies to the queued join too.
    mp_result set_crossfade(uint32_t ms);
    mp_result pause();
    mp_result resume();
    mp_result stop(mp_fade_mode fade);
    mp_result seek(int64_t position_ms);
    void set_volume(float slider);
    // Per-track ReplayGain (see mp_track_set_replaygain): the resolved gain becomes the source's mixer volume,
    // which the mixer ramps when the source is being heard.
    mp_result set_replaygain(track* t, float gain_db, float peak);
    // The linear gain mp_track_set_replaygain resolves for (gain_db, peak): 10^(gain_db/20), reduced to 1/peak when a
    // known peak would exceed full scale. Exposed for the tests.
    static float replaygain_linear(float gain_db, float peak) noexcept;

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
    static unsigned long __stdcall trim_proc(unsigned long handle, void* buffer, unsigned long length, void* user);
    static void free_track_streams(track& t) noexcept;
    // Positions a source that is not in the mixer (bytes of its own float format); handles the wrapper.
    static bool set_source_position(track& t, uint64_t bytes) noexcept;
    // The source's mixer position (latency-compensated by `delay` bytes) including a wrapper's origin.
    static uint64_t source_position(const track& t, uint32_t delay) noexcept;
    // Attaches a positioned source to the mixer with the END sync.
    mp_result attach_source(track& t);
    static void __stdcall end_sync(unsigned long handle, unsigned long channel, unsigned long data, void* user);

    // Crossfade (E1-S4). arm_crossfade makes the current source's fade-out and its mix-time POS sync match what is
    // queued (next_, its join mode, crossfade_ms_): control thread, under control_. fade_sync runs on the audio
    // thread at the fade point and adds the queued source. fade_proc is the per-source envelope DSP. remove_outgoing
    // takes a still-fading predecessor out of the mixer (control thread).
    void arm_crossfade();
    void remove_outgoing() noexcept;
    static void __stdcall fade_sync(unsigned long handle, unsigned long channel, unsigned long data, void* user);
    static void __stdcall fade_proc(unsigned long handle, unsigned long channel, void* buffer, unsigned long length,
                                    void* user);

    mutable std::mutex control_; // control-plane calls; never taken on the audio thread

    uint32_t mixer_ = 0; // HSTREAM (decode, nonstop)
    uint32_t mixer_rate_ = 48000;
    uint32_t mixer_channels_ = 2;
    std::vector<uint32_t> plugins_;
    std::vector<std::unique_ptr<track>> tracks_;

    // The control plane writes current_ under control_; the audio thread replaces it at a gapless join
    // (end_sync) and reads next_ there. Both are only ever pointers into tracks_, which the control plane owns.
    std::atomic<track*> current_{nullptr};
    std::atomic<track*> next_{nullptr};
    std::atomic<track*> join_pending_{nullptr}; // set by end_sync, raised as events by pull() after the read
    std::atomic<track*> join_ended_{nullptr};   // the source that ran out at that join (none for a crossfade)
    std::atomic<int64_t> join_at_{0};           // mixer byte position a crossfade started at, captured by fade_sync
    std::atomic<track*> outgoing_{nullptr};     // the predecessor still fading out under current_ during a crossfade
    std::atomic<bool> next_crossfade_{false};   // next_ was queued with MP_JOIN_CROSSFADE
    std::atomic<uint32_t> crossfade_ms_{0};
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
