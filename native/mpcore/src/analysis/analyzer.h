// The analysis thread (E4-S1): what turns the tap's hops into mp_analysis_frame.
//
// The tap (E1-S8) publishes fixed 512-frame hops of mixed PCM through a lock-free ring. This owns the other end:
// one thread that drains the ring, and for each hop produces a spectrum, a waveform, RMS and peak, then
// publishes the whole frame to a triple buffer that any number of consumers can copy from - the managed theming
// poll over mp_analysis_try_get_latest, and from E4-S3 the render thread in-process.
//
// Shape of the analysis (ADR-010 and docs/roadmap-and-backlog.md E4-S1): a 2048-point real FFT over a Hann
// window, advanced 512 samples per hop. The window is four hops long and the hop is one, so successive frames
// overlap by 75%: the FFT needs 2048 samples to resolve 23 Hz bins, but a visualizer wants a new frame every
// 10.7 ms, and a sliding window is how both are true at once. What arrives is mixed down to mono first, because
// the spectrum of a stereo mix is one spectrum and not two half-loud ones.
//
// Real-time rules apply on this thread even though it is not the audio thread: every buffer is a member sized at
// construction, nothing is allocated or locked per hop, and rt::scope asserts it in Debug (AC-112). It is not
// that a missed analysis frame would glitch the audio - it would not - but that this thread runs at Pro Audio
// priority, and a thread that can block at that priority is a thread that can hold up the one that matters.
//
// A sliding window is only a window on one signal while the hops sliding through it are contiguous, and the tap
// drops a hop rather than blocking the mixer when this thread falls 170 ms behind. So a dropped hop is not a
// missing frame, it is a splice: the window would hold three quarters of one piece of audio and a quarter of
// another, and the spectrum of that is a perfectly plausible picture of something nobody played. The window is
// therefore restarted on the hop the tap says is not continuous with the last one, and no frame is published
// until it is four contiguous hops again - a frame withheld is a visualizer holding its last picture for 32 ms,
// where a torn one is a visualizer confidently drawing a lie. The count of restarts rides out on every frame as
// mp_analysis_frame.discontinuities, because a consumer that computes anything across frames - E4-S2's onset
// history, E4-S6's theming - cannot see the gap in a stream it samples at 30 Hz (T-135).
//
// Why polling and not a semaphore the tap signals: the tap's producer is the WASAPI mix thread inside BASS's own
// DSP callback, and signalling from there is a kernel transition on the audio path to save this thread a 2 ms
// sleep. The ring holds sixteen hops - 170 ms - so a poll that is late by a scheduling quantum loses nothing,
// and the frames it then finds are all still there to be produced, in order.
#pragma once

#include "mpcore.h"

#include "analysis/features.h"
#include "analysis/tap.h"
#include "common/triple_buffer.h"

#include <atomic>
#include <cstdint>
#include <thread>

struct PFFFT_Setup;

namespace mp::analysis {

// C4324: "structure was padded due to alignment specifier" - the FFT buffers are 16-byte aligned because pffft's
// SIMD path requires it, and the padding that costs is the point.
#pragma warning(push)
#pragma warning(disable : 4324)

class analyzer {
public:
    // 2048-point FFT advanced by one 512-frame hop: 23.44 Hz bins at 48 kHz, a new frame every 10.67 ms.
    static constexpr uint32_t k_fft_size = 2048;
    static constexpr uint32_t k_hop = tap_block::k_frames;
    static constexpr uint32_t k_window_hops = k_fft_size / k_hop;        // 4: the hops a full window is made of
    static constexpr uint32_t k_bins = MP_ANALYSIS_SPECTRUM_BINS;        // 1024: bins 0..1023, Nyquist dropped
    static constexpr uint32_t k_waveform = MP_ANALYSIS_WAVEFORM_SAMPLES; // 512: exactly one hop, mono
    static_assert(k_bins * 2 == k_fft_size, "the spectrum is the real FFT's bins below Nyquist");
    static_assert(k_waveform == k_hop, "the waveform is the hop itself, so no decimation is needed");

    // Magnitudes are scaled so a full-scale sine reads 1.0 in its own bin: two-sided to one-sided (x2), the
    // transform's own N (/k_fft_size), and the Hann window's coherent gain (/0.5). Chosen here rather than left
    // raw because every consumer of the spectrum wants "how loud is this bin", and a scale that depends on the
    // FFT size is a trap for the preset author.
    static constexpr float k_hann_coherent_gain = 0.5f;
    static constexpr float k_spectrum_scale = 2.0f / (static_cast<float>(k_fft_size) * k_hann_coherent_gain);

    // Idle poll interval. Bounded above by the ring's 170 ms of slack and below by wanting a paused stream to
    // stop producing promptly (AC-111: within 100 ms); Windows will round it up to the timer resolution, which
    // costs nothing because a wake drains every hop that accumulated.
    static constexpr int k_idle_poll_ms = 2;

    analyzer();
    ~analyzer();

    analyzer(const analyzer&) = delete;
    analyzer& operator=(const analyzer&) = delete;

    // Control thread. Starts the thread on `source`, which must outlive the analyzer; already running is a no-op.
    void start(tap& source, uint32_t sample_rate);
    // Control thread. Joins the thread; safe to call when not running, and safe to call twice. Must be called
    // before anything resets or destroys the tap: draining the ring is a consumer's move and there is only one.
    void stop() noexcept;
    bool running() const noexcept { return running_.load(std::memory_order_acquire); }

    // Any thread. Copies the newest complete frame; false when none has been produced yet.
    bool try_get_latest(mp_analysis_frame& out) const noexcept;

    // Frames produced since construction. Monotonic across a stop/start, because the managed side watches
    // mp_analysis_frame.sequence to know whether what it holds is new.
    uint64_t frames() const noexcept { return frames_.load(std::memory_order_acquire); }

    // Times the analyzer has had to restart its window because the tap lost hops (T-135). Zero in real-time
    // playback, where the ring holds 170 ms and the producer is the WASAPI thread; non-zero only where audio is
    // made faster than it is heard. The low byte of this is what mp_analysis_frame.discontinuities carries.
    uint64_t discontinuities() const noexcept { return discontinuities_.load(std::memory_order_acquire); }

    // ---- the per-hop work, exposed so the tests can drive it without a thread ----

    // One hop into the sliding window, and a frame out of it when the window holds four contiguous hops.
    // Publishes on every hop of an unbroken stream; on the hop after a gap it restarts the window instead, and
    // then publishes nothing until the window is whole again - k_window_hops - 1 hops later. Real-time: no
    // allocation, no lock (rt::scope asserts it in Debug).
    void analyze(const tap_block& block) noexcept;

    // The Hann window the analysis applies, k_fft_size long.
    const float* window() const noexcept { return hann_; }

    // Magnitude spectrum of k_fft_size already-windowed samples, into k_bins floats scaled by k_spectrum_scale.
    // This is the pffft call and the unpacking of its output order, with nothing else in the way, so a test can
    // hold it against a reference DFT of the same input (AC-113).
    void forward(const float* windowed, float* out_bins) noexcept;

    // The extractor the frame's bands, centroid, harmonic ratio and onset come from (E4-S2), so a test can time
    // it on its own (AC-116) and read the flux and threshold behind an onset flag (AC-115).
    features& extraction() noexcept { return features_; }

private:
    void run() noexcept;
    // Mixes one hop down to mono at the end of the sliding window, advancing it by k_hop.
    void slide(const tap_block& block) noexcept;
    // Throws the window and the extraction's history away because the hop about to arrive does not follow the
    // last one, and suppresses publishing until the window is four contiguous hops again.
    void restart() noexcept;

    PFFFT_Setup* fft_ = nullptr;
    alignas(16) float hann_[k_fft_size]{};
    alignas(16) float history_[k_fft_size]{};  // the sliding window, mono, oldest first
    alignas(16) float windowed_[k_fft_size]{}; // history_ times hann_
    alignas(16) float spectrum_[k_fft_size]{}; // pffft's own output order, unpacked into the frame
    alignas(16) float work_[k_fft_size]{};     // pffft scratch; passing it keeps 8 KB off the stack

    features features_;           // E4-S2's extraction, analysis thread only
    mp_analysis_frame staging_{}; // analysis thread only, until it is published
    triple_buffer<mp_analysis_frame> published_;

    tap* source_ = nullptr;
    uint32_t sample_rate_ = 48000;
    std::thread thread_;
    std::atomic<bool> stop_{false};
    std::atomic<bool> running_{false};
    std::atomic<uint64_t> frames_{0};
    // Hops the window still needs before it is whole again after a restart; 0 whenever it is. Analysis thread.
    uint32_t refill_ = 0;
    // Monotonic since construction, like frames_ and for the same reason: the frame carries it and a consumer
    // decides whether what it holds is continuous with what it holds next by whether the number moved.
    std::atomic<uint64_t> discontinuities_{0};
    uint64_t dropped_hops_ = 0;             // hops lost to the tap since start(), for the log line stop() writes
    uint64_t discontinuities_at_start_ = 0; // so that line counts this run's restarts and not every run's
};

#pragma warning(pop)

} // namespace mp::analysis
