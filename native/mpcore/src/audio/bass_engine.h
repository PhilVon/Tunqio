// The BASS-backed engine (E0-S4 first cut, E1-S1 skeleton). This header exposes only mpcore ABI types;
// BASS headers are included by bass_engine.cpp alone (docs/solution-structure.md dependency rule,
// enforced by the include-grep test).
#pragma once

#include "mpcore.h"

#include "analysis/tap.h"

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace mp::audio {

class engine;

#if defined(MP_STATIC)
// Test seams. Neither an exclusive-mode init that a driver refuses (E1-S6) nor a device being unplugged (E1-S7) can
// be provoked from a test on hardware that is behaving, and a CI runner may have no output device at all. The tests
// compile these sources directly (MP_STATIC); the shipped DLL is built without it and carries no hook.
namespace testing {
void force_exclusive_failure(bool on) noexcept;
// Posts a BASSWASAPI device notification as if the driver had raised it, and returns once the watch thread has
// finished acting on it, so a test never has to sleep to observe the result.
void simulate_device_notification(uint32_t notify, uint32_t device) noexcept;
// BASS_WASAPI_NOTIFY_* as the tests name them, so a test needs no BASS header (the include-grep rule).
inline constexpr uint32_t k_notify_enabled = 0;
inline constexpr uint32_t k_notify_disabled = 1;
inline constexpr uint32_t k_notify_default_output = 2;
inline constexpr uint32_t k_notify_fail = 0x100;
} // namespace testing
#endif

struct track {
    uint32_t stream = 0; // HSTREAM (decode): what the mixer plays
    mp_track_info info{};
    std::wstring path;
    engine* owner = nullptr;

    // T-102: an MP4 whose decoder (Media Foundation) hands out the encoder priming and padding. `stream` is then
    // a user decode stream whose STREAMPROC reads `inner`, drops trim_skip frames it is still owed and ends
    // after trim_valid frames. A user stream cannot seek, only reset to 0, so a seek resets it, moves `inner`,
    // and records in trim_origin the frame its counter 0 now stands for; every position read adds it back.
    // trim_skip is how set_source_position corrects for `inner`'s own positions running trim_priming frames
    // ahead of its data (T-109, see there); at position 0 that is the priming itself.
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

    // Opens the device in the requested mode. An exclusive mode that the driver refuses falls back to shared
    // (MP_EVENT_ERROR says why) rather than leaving the user without output: exclusive is an opt-in.
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

#if defined(MP_STATIC)
    // testing::simulate_device_notification: posts the note and returns once the watch thread has handled it.
    void simulate_device_notification_for_test(uint32_t notify, uint32_t device) noexcept;
#endif

private:
    engine() = default;
    mp_result init(const mp_engine_config& config);
    mp_result create_mixer(uint32_t rate, uint32_t channels);
    void load_plugins(const std::wstring& dir);
    void free_output() noexcept;
    // One BASSWASAPI init attempt in one mode, through to BASS_WASAPI_Start: adopts the device's format, rebuilds
    // the mixer at it when it differs and re-attaches the playing source. On failure the output is freed again and
    // why is copied into fail_text, so the caller can name the exclusive failure while retrying in shared mode.
    mp_result open_output(const mp_output_config& config, bool exclusive, char* fail_text, size_t fail_cap);
    // Rebuilds the mixer at the output's rate and channels, carrying the playing source over at its position.
    mp_result adopt_output_format();
    // set_output with control_ already held, which is how the device watch reopens an output for itself.
    mp_result set_output_locked(const mp_output_config& config);

    // ---- device changes (E1-S7) ----------------------------------------------------------------------------
    // BASSWASAPI raises its notifications on a Windows notification thread, and reopening a device from inside one
    // is how that thread deadlocks against the audio thread it is trying to stop. The callback therefore does
    // nothing but enqueue; watch_loop drains the queue on its own thread and does the work under control_, which is
    // also what stops a migration racing a set_output the user asked for.
    struct device_note {
        uint32_t notify;
        uint32_t device;
    };
    void start_device_watch();
    void stop_device_watch() noexcept;
    void post_device_note(uint32_t notify, uint32_t device) noexcept;
    void watch_loop() noexcept;
    void handle_device_note(const device_note& note);
    // Parks playback where it stands when the open device disappears. Not pause(): there is no audio thread left to
    // run the ramp, so the hold is engaged here and resume() fades back in once an output exists again.
    void park_for_lost_device() noexcept;
    // Records what was actually opened, so a lost device can be recognised and a "default" migration knows whether
    // the default has really moved.
    void remember_open_device();
    static void __stdcall notify_proc(unsigned long notify, unsigned long device, void* user);

    // The analysis tap (E1-S8): a DSP on the mixer, so it sees the mix as the mixer makes it and before the
    // engine's own envelope and volume - the visualization follows the music, not the volume slider.
    static void __stdcall tap_proc(unsigned long handle, unsigned long channel, void* buffer, unsigned long length,
                                   void* user);
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

    // Fed by tap_proc on the audio thread, drained by the analysis thread (E4-S1). Public so the tests and, later,
    // the analysis thread can read it; the engine only ever writes it through the DSP.
public:
    mp::analysis::tap& analysis_tap() noexcept { return tap_; }

private:
    mp::analysis::tap tap_;

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

    // The output the caller last asked for (E1-S7): a migration reopens this rather than guessing, so the mode, the
    // buffer and the event-driven choice survive the device moving underneath it.
    mp_output_config wanted_{};
    bool have_wanted_ = false;
    int32_t open_device_ = MP_DEVICE_NONE; // BASS index of the device actually open (MP_DEVICE_DEFAULT resolved)
    std::string open_device_id_;           // its endpoint id, which survives the index shifting
    std::string lost_device_id_;           // the endpoint id whose return the UI is waiting to be offered

    std::thread device_watch_;
    std::mutex note_mutex_;
    std::condition_variable note_cv_;
    std::deque<device_note> notes_;
    uint64_t notes_handled_ = 0; // under note_mutex_; the test seam waits on it rather than sleeping
    bool watch_stop_ = false;

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
