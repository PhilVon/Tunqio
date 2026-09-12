#include "analysis/analyzer.h"

#include "common/log.h"
#include "common/rt_guard.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <numbers>
#include <pffft/pffft.h>

#include <windows.h>

// clang-format off
// After windows.h, which avrt.h needs for HANDLE and DWORD and does not include itself. The include regrouping
// in native/.clang-format sorts every other <angle> header above the windows block, so this one is kept out of it.
#include <avrt.h>
// clang-format on

namespace mp::analysis {

namespace {

// "Pro Audio" is what the MMCSS scheduler calls a thread that must not be starved by a busy foreground
// application (docs/decisions.md, thread table). It is a request: a machine with MMCSS disabled, or a service
// that is not running, simply says no, and above-normal priority is the honest fallback rather than nothing.
struct mmcss_membership {
    HANDLE task = nullptr;
    DWORD index = 0;

    mmcss_membership() {
        task = AvSetMmThreadCharacteristicsW(L"Pro Audio", &index);
        if (task == nullptr) {
            SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL);
        }
    }
    ~mmcss_membership() {
        if (task != nullptr) {
            AvRevertMmThreadCharacteristics(task);
        }
    }
    mmcss_membership(const mmcss_membership&) = delete;
    mmcss_membership& operator=(const mmcss_membership&) = delete;
};

} // namespace

analyzer::analyzer() {
    // Periodic Hann (i/N, not i/(N-1)): the symmetric window is for filter design, the periodic one is what makes
    // a sinusoid at a bin centre land in that bin alone, which is the whole reason the window is here.
    for (uint32_t i = 0; i < k_fft_size; ++i) {
        const double phase = 2.0 * std::numbers::pi * static_cast<double>(i) / static_cast<double>(k_fft_size);
        hann_[i] = static_cast<float>(0.5 * (1.0 - std::cos(phase)));
    }
    // The only allocation in the analysis path, and it happens once, here: pffft's twiddle factors.
    fft_ = pffft_new_setup(static_cast<int>(k_fft_size), PFFFT_REAL);
    staging_.struct_size = sizeof(mp_analysis_frame);
}

analyzer::~analyzer() {
    stop();
    if (fft_ != nullptr) {
        pffft_destroy_setup(fft_);
    }
}

void analyzer::start(tap& source, uint32_t sample_rate) {
    if (running_.load(std::memory_order_acquire)) {
        return;
    }
    source_ = &source;
    sample_rate_ = sample_rate != 0 ? sample_rate : 48000;
    // A new mixer is a new stream of audio; the window must not carry four hops of the previous one into it,
    // and the extractor must not read the difference between the two streams' spectra as an onset.
    std::memset(history_, 0, sizeof history_);
    features_.reset(sample_rate_);
    // The window starting empty at the head of a stream is not a discontinuity: the silence in front of the
    // first hop is true - nothing was mixed before it - so those first frames are published, unlike the ones a
    // restart withholds, where the silence would be standing in for audio that really happened.
    refill_ = 0;
    dropped_hops_ = 0;
    discontinuities_at_start_ = discontinuities_.load(std::memory_order_acquire);
    stop_.store(false, std::memory_order_release);
    running_.store(true, std::memory_order_release);
    thread_ = std::thread{[this] { run(); }};
}

void analyzer::stop() noexcept {
    stop_.store(true, std::memory_order_release);
    if (thread_.joinable()) {
        thread_.join();
    }
    running_.store(false, std::memory_order_release);
    // The one place a dropped hop is reported in words, and it is here rather than where it is noticed because
    // where it is noticed is a Pro Audio thread inside rt::scope: log() formats and hands the message to a
    // managed sink, which is exactly the "do nothing but enqueue" that thread is not allowed to break (nothing
    // on the audio path logs, for the same reason). By the time this runs the thread is joined and the caller
    // is the control thread, so the cost is a line per mixer and only when there was something to say.
    if (dropped_hops_ != 0) {
        log(MP_LOG_WARN,
            "analysis: the tap dropped %llu hop(s); the window was restarted %llu time(s). The analysis thread "
            "fell more than 170 ms behind the mixer, which cannot happen at playback speed.",
            static_cast<unsigned long long>(dropped_hops_),
            static_cast<unsigned long long>(discontinuities_.load(std::memory_order_acquire) -
                                            discontinuities_at_start_));
    }
}

bool analyzer::try_get_latest(mp_analysis_frame& out) const noexcept {
    // struct_size comes with the frame: staging_ carries it from construction, and the export has already
    // refused any caller whose struct is a different size.
    return published_.try_get(out);
}

void analyzer::run() noexcept {
    const mmcss_membership priority;
    tap_block block;
    while (!stop_.load(std::memory_order_acquire)) {
        bool worked = false;
        // Everything the ring holds, in order: a wake that was late still produces every hop it finds, so the
        // frame rate over any interval is the hop rate and not the poll rate.
        while (source_->try_read(block)) {
            analyze(block);
            worked = true;
            if (stop_.load(std::memory_order_acquire)) {
                return;
            }
        }
        if (!worked) {
            std::this_thread::sleep_for(std::chrono::milliseconds(k_idle_poll_ms));
        }
    }
}

void analyzer::slide(const tap_block& block) noexcept {
    // The window advances by exactly one hop, so the oldest hop falls off the front and the new one lands at the
    // end. memmove rather than a circular index: 6 KB of contiguous floats is a few hundred nanoseconds, and the
    // FFT wants them contiguous anyway, so a ring would only move the copy to where it is harder to read.
    std::memmove(history_, history_ + k_hop, (k_fft_size - k_hop) * sizeof(float));
    float* const newest = history_ + (k_fft_size - k_hop);
    const uint32_t channels = std::max(1u, block.channels);
    const float inv = 1.0f / static_cast<float>(channels);
    for (uint32_t f = 0; f < k_hop; ++f) {
        const float* frame = block.samples + static_cast<size_t>(f) * channels;
        float sum = 0.0f;
        for (uint32_t c = 0; c < channels; ++c) {
            sum += frame[c];
        }
        newest[f] = sum * inv;
    }
}

void analyzer::restart() noexcept {
    // Zeroing is belt and braces - nothing is published until the last of these samples has been shifted out
    // again - but it is 8 KB of memset on a path taken once per gap, and it means there is no state left in
    // which a future publish could show half of one stream and half of another.
    std::memset(history_, 0, sizeof history_);
    // The extraction's memory is as broken by the gap as the window is: previous_ holds the spectrum from
    // before it, and a flux taken against that is a difference between two pieces of audio rather than a change
    // within one - an onset the music did not have. reset() is the sledgehammer (it also throws away the 43-hop
    // median the threshold adapts with), and it is the right one: the adaptation is worth less than the
    // certainty that nothing pre-gap survives, and the alternative is a second kind of reset to reason about.
    features_.reset(sample_rate_);
    refill_ = k_window_hops - 1;
    discontinuities_.fetch_add(1, std::memory_order_acq_rel);
}

void analyzer::forward(const float* windowed, float* out_bins) noexcept {
    pffft_transform_ordered(fft_, windowed, spectrum_, work_, PFFFT_FORWARD);
    // pffft's ordered real output packs the two purely real bins together: spectrum_[0] is DC and spectrum_[1]
    // is Nyquist, then bins 1..N/2-1 follow as interleaved (re, im). The frame carries bins 0..1023, so Nyquist
    // is the one that is dropped - 24 kHz, where a visualizer has nothing to show and a mix has nothing to put.
    out_bins[0] = std::fabs(spectrum_[0]) * k_spectrum_scale;
    for (uint32_t k = 1; k < k_bins; ++k) {
        const float re = spectrum_[2 * k];
        const float im = spectrum_[2 * k + 1];
        out_bins[k] = std::sqrt(re * re + im * im) * k_spectrum_scale;
    }
}

void analyzer::analyze(const tap_block& block) noexcept {
    [[maybe_unused]] mp::rt::scope rt_guard; // counts allocations in Debug (AC-112); nothing in Release

    // The tap says so when this hop is not the one after the last: the ring overran and what fell out of it is
    // audio that was played and will not be seen here. Sliding across that would put a splice in the middle of
    // the window (T-135).
    if (block.dropped_before != 0) {
        dropped_hops_ += block.dropped_before;
        restart();
    }

    slide(block);

    // Still refilling: the window holds the fabricated silence a restart left in front of this hop, and a
    // spectrum of that is a spectrum of nothing that was played. Three hops - 32 ms - is the whole cost, and it
    // is paid in frames not produced rather than frames produced wrong. The level and the waveform would be
    // honest, but a frame with a spectrum in it that nobody can trust is worse than no frame at all.
    if (refill_ != 0) {
        --refill_;
        return;
    }

    // RMS and peak are of the hop as it was mixed, all channels: that is the level a meter shows, and it must not
    // change because the mix is wide. Taking them from the interleaved block rather than the mono downmix keeps a
    // hard-panned peak a peak instead of averaging it away.
    const uint32_t channels = std::max(1u, block.channels);
    const uint32_t samples = block.frames * channels;
    double sum_squares = 0.0;
    float peak = 0.0f;
    for (uint32_t i = 0; i < samples; ++i) {
        const float s = block.samples[i];
        sum_squares += static_cast<double>(s) * s;
        peak = std::max(peak, std::fabs(s));
    }
    staging_.rms = samples == 0 ? 0.0f : static_cast<float>(std::sqrt(sum_squares / samples));
    staging_.peak = peak;

    // The waveform is the newest hop of the mono window, which is the same 512 samples the spectrum's most
    // recent quarter came from, so a preset drawing both is drawing one moment.
    std::memcpy(staging_.waveform, history_ + (k_fft_size - k_hop), k_waveform * sizeof(float));

    for (uint32_t i = 0; i < k_fft_size; ++i) {
        windowed_[i] = history_[i] * hann_[i];
    }
    forward(windowed_, staging_.spectrum);

    // bands, spectral_centroid_hz, harmonic_ratio and onset (E4-S2): extraction over this spectrum, not the
    // spectrum, which is why it is a separate object reading the frame's own bins back rather than a second
    // pass over the transform's output.
    features_.extract(staging_.spectrum, staging_);

    staging_.mixer_byte_pos = block.mixer_byte_pos;
    staging_.qpc_ticks = block.qpc_ticks;
    // Not a flag on the frame after the gap but a count that rides on every frame: the theming poll samples at
    // 30 Hz and the renderer at 60, so a one-frame flag at 94 Hz is a flag most consumers never see. A number
    // that differs from the one on the frame they held last says the same thing and survives being sampled.
    staging_.discontinuities = static_cast<uint8_t>(discontinuities_.load(std::memory_order_acquire));
    staging_.sequence = static_cast<uint32_t>(frames_.fetch_add(1, std::memory_order_acq_rel) + 1);
    published_.publish(staging_);
}

} // namespace mp::analysis
