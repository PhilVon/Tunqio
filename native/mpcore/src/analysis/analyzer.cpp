#include "analysis/analyzer.h"

#include "common/rt_guard.h"

#include <pffft/pffft.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>
#include <numbers>

#include <windows.h>

#include <avrt.h>

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
    // A new mixer is a new stream of audio; the window must not carry four hops of the previous one into it.
    std::memset(history_, 0, sizeof history_);
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

    slide(block);

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

    // bands, spectral_centroid_hz, harmonic_ratio and onset are E4-S2's: they are extraction over this spectrum,
    // not the spectrum. Left zeroed rather than half-computed, so a consumer reading a zero knows it is a zero.
    staging_.mixer_byte_pos = block.mixer_byte_pos;
    staging_.qpc_ticks = block.qpc_ticks;
    staging_.sequence = static_cast<uint32_t>(frames_.fetch_add(1, std::memory_order_acq_rel) + 1);
    published_.publish(staging_);
}

} // namespace mp::analysis
