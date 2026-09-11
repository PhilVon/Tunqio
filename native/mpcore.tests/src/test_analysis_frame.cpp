// E4-S1: the analysis thread and the frame it publishes.
//
// Four things have to be true and each is measured rather than asserted by inspection: the FFT is a real FFT
// (against a DFT written out longhand here, which is slow and obviously correct and therefore a reference);
// its scaling is the one the header promises, which a reference sharing the same mistake could not show, so a
// full-scale sine is checked to read 1.0 in absolute terms; a hop costs no allocation; and frames come out at
// the hop rate while the mixer is being pulled and stop when it is not.
#include "mpcore.h"

#include "analysis/analyzer.h"
#include "audio/bass_engine.h"
#include "common/rt_guard.h"
#include "offline_engine.h"
#include "wav_fixture.h"

#include <algorithm>
#include <catch2/catch_amalgamated.hpp>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <functional>
#include <numbers>
#include <thread>
#include <vector>

#include <windows.h>

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

namespace {

using mp::analysis::analyzer;
using mp::analysis::tap_block;

constexpr uint32_t k_n = analyzer::k_fft_size;

// The textbook DFT, in double, with nothing clever in it. X[k] = sum_t x[t] e^(-2 pi i k t / N); the magnitude
// is what a spectrum is. Slow on purpose: a reference that shared an optimisation with the thing it is checking
// would not be a reference.
std::vector<double> naive_dft_magnitudes(const float* x, uint32_t n, uint32_t bins) {
    std::vector<double> magnitude(bins);
    for (uint32_t k = 0; k < bins; ++k) {
        double re = 0.0;
        double im = 0.0;
        for (uint32_t t = 0; t < n; ++t) {
            const double angle = -2.0 * std::numbers::pi * static_cast<double>(k) * static_cast<double>(t) /
                                 static_cast<double>(n);
            re += static_cast<double>(x[t]) * std::cos(angle);
            im += static_cast<double>(x[t]) * std::sin(angle);
        }
        magnitude[k] = std::sqrt(re * re + im * im);
    }
    return magnitude;
}

// A hop of interleaved stereo whose value at each frame comes from `f`, with the absolute frame index counted
// from `first` so a test can hand the analyzer four consecutive hops of one continuous signal.
tap_block hop_of(int64_t first_frame, uint32_t channels, const std::function<float(int64_t)>& f) {
    tap_block block{};
    block.frames = tap_block::k_frames;
    block.channels = channels;
    block.mixer_byte_pos = first_frame * channels * static_cast<int64_t>(sizeof(float));
    for (uint32_t i = 0; i < tap_block::k_frames; ++i) {
        const float value = f(first_frame + i);
        for (uint32_t c = 0; c < channels; ++c) {
            block.samples[static_cast<size_t>(i) * channels + c] = value;
        }
    }
    return block;
}

// Fills the analyzer's sliding window with `f` by feeding it the four hops the window is made of, and returns
// the windowed samples the last of those hops was analysed with - which is what the FFT actually saw.
std::vector<float> fill_window(analyzer& a, const std::function<float(int64_t)>& f) {
    for (int64_t hop = 0; hop < static_cast<int64_t>(k_n / analyzer::k_hop); ++hop) {
        a.analyze(hop_of(hop * analyzer::k_hop, 2, f));
    }
    std::vector<float> windowed(k_n);
    for (uint32_t i = 0; i < k_n; ++i) {
        windowed[i] = f(static_cast<int64_t>(i)) * a.window()[i];
    }
    return windowed;
}

// A wait that actually waits the length it is asked for. std::this_thread::sleep_for rounds up to the system
// timer resolution - 15.6 ms by default, half again as long as the 10 ms buffer the rate test renders - and a
// test that produces 10 ms of audio per 15.6 ms of wall clock is not measuring the rate audio is made at. A
// high-resolution waitable timer waits to well under a millisecond and does not burn a core doing it; where the
// flag is not honoured the yield loop is still correct, only hot.
class pacer {
public:
    pacer()
        : timer_(CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                                        TIMER_MODIFY_STATE | SYNCHRONIZE)) {}
    ~pacer() {
        if (timer_ != nullptr) {
            CloseHandle(timer_);
        }
    }
    pacer(const pacer&) = delete;
    pacer& operator=(const pacer&) = delete;

    void wait_until(std::chrono::steady_clock::time_point until) const {
        for (;;) {
            const auto left = until - std::chrono::steady_clock::now();
            if (left <= std::chrono::steady_clock::duration::zero()) {
                return;
            }
            LARGE_INTEGER due{};
            due.QuadPart = -(std::chrono::duration_cast<std::chrono::nanoseconds>(left).count() / 100);
            if (timer_ == nullptr || due.QuadPart == 0 ||
                SetWaitableTimer(timer_, &due, 0, nullptr, nullptr, FALSE) == 0) {
                std::this_thread::yield();
                continue;
            }
            WaitForSingleObject(timer_, INFINITE);
        }
    }

private:
    HANDLE timer_;
};

} // namespace

// ---- AC-113: pffft against a reference DFT ------------------------------------------------------------

TEST_CASE("the reference DFT puts a whole number of cycles in exactly one bin", "[analysis][frame][dft]") {
    // The reference has to be checked before it can check anything. 64 cycles in 2048 samples is bin 64 exactly,
    // and an unwindowed DFT of that is N/2 in that bin and nothing anywhere else.
    std::vector<float> x(k_n);
    for (uint32_t t = 0; t < k_n; ++t) {
        x[t] = static_cast<float>(std::sin(2.0 * std::numbers::pi * 64.0 * t / k_n));
    }
    const std::vector<double> magnitude = naive_dft_magnitudes(x.data(), k_n, 128);

    CHECK(magnitude[64] == Catch::Approx(static_cast<double>(k_n) / 2.0).epsilon(1e-6));
    double worst_other = 0.0;
    for (uint32_t k = 0; k < 128; ++k) {
        if (k != 64) {
            worst_other = std::max(worst_other, magnitude[k]);
        }
    }
    INFO("bin 64 = " << magnitude[64] << ", largest other bin = " << worst_other);
    CHECK(worst_other < 1e-6 * static_cast<double>(k_n));
}

TEST_CASE("pffft matches the reference DFT to 1e-4 on a fixture block", "[analysis][frame][dft]") {
    analyzer a;

    // Not a single tone: three partials at deliberately non-integer bin offsets, so every bin has leakage in it
    // and the comparison is over the whole spectrum rather than one peak and 1023 zeros. The phases are there so
    // no bin's real and imaginary parts can cancel into a number that is easy to get right by accident.
    const auto signal = [](int64_t t) {
        const double x = static_cast<double>(t);
        return static_cast<float>(0.45 * std::sin(2.0 * std::numbers::pi * 63.37 * x / k_n + 0.3) +
                                  0.30 * std::sin(2.0 * std::numbers::pi * 220.8 * x / k_n + 1.1) +
                                  0.15 * std::sin(2.0 * std::numbers::pi * 701.2 * x / k_n + 2.7));
    };
    const std::vector<float> windowed = fill_window(a, signal);

    std::vector<float> from_pffft(analyzer::k_bins);
    a.forward(windowed.data(), from_pffft.data());
    const std::vector<double> reference = naive_dft_magnitudes(windowed.data(), k_n, analyzer::k_bins);

    // The scale the header promises, written out here from its own reasoning rather than taken from the
    // analyzer: one-sided (x2), the transform's N, and the Hann window's coherent gain of 1/2. If the analyzer's
    // constant were wrong this would disagree, which is the point of not reusing it.
    const double scale = 2.0 / (static_cast<double>(k_n) * 0.5);

    double peak = 0.0;
    double worst = 0.0;
    uint32_t worst_bin = 0;
    for (uint32_t k = 0; k < analyzer::k_bins; ++k) {
        const double want = reference[k] * scale;
        peak = std::max(peak, want);
        const double error = std::fabs(static_cast<double>(from_pffft[k]) - want);
        if (error > worst) {
            worst = error;
            worst_bin = k;
        }
    }

    // Relative to the loudest bin, because that is the only scale-free way to say "matches": an absolute 1e-4 on
    // magnitudes whose size depends on the FFT length would be a statement about N, not about the transform.
    const double relative = worst / peak;
    INFO("largest disagreement " << worst << " at bin " << worst_bin << ", peak magnitude " << peak
                                 << ", relative " << relative);
    CHECK(relative < 1e-4);
}

TEST_CASE("a full-scale sine reads 1.0 in its own bin", "[analysis][frame][dft]") {
    // The absolute check the reference comparison cannot make: if the analyzer and the test agreed on a wrong
    // scale, both would still be wrong, and this is the measurement that would notice. Bin 128 exactly, so the
    // Hann window puts the tone in one bin and its two neighbours and nowhere else.
    analyzer a;
    const auto sine = [](int64_t t) {
        return static_cast<float>(std::sin(2.0 * std::numbers::pi * 128.0 * static_cast<double>(t) / k_n));
    };
    const std::vector<float> windowed = fill_window(a, sine);

    std::vector<float> spectrum(analyzer::k_bins);
    a.forward(windowed.data(), spectrum.data());

    INFO("bin 128 reads " << spectrum[128] << ", neighbours " << spectrum[127] << " and " << spectrum[129]);
    CHECK(spectrum[128] == Catch::Approx(1.0f).epsilon(0.001));
    // And the skirt really is a skirt: three bins wide, then nothing, which is what a Hann window does and what
    // proves the window was applied at all.
    CHECK(spectrum[127] == Catch::Approx(0.5f).epsilon(0.01));
    CHECK(spectrum[129] == Catch::Approx(0.5f).epsilon(0.01));
    CHECK(spectrum[132] < 0.002f);
    CHECK(spectrum[0] < 0.002f);
}

TEST_CASE("DC lands in bin zero and Nyquist is not mistaken for it", "[analysis][frame][dft]") {
    // pffft packs the two real bins together, DC in the first slot and Nyquist in the second, and unpacking that
    // wrongly is the classic way to get a spectrum whose bin 0 is the 24 kHz content. A constant has all of its
    // energy at DC, so bin 0 must be the constant and bin 1 must be empty.
    analyzer a;
    const std::vector<float> windowed = fill_window(a, [](int64_t) { return 0.5f; });
    std::vector<float> spectrum(analyzer::k_bins);
    a.forward(windowed.data(), spectrum.data());

    INFO("bin 0 = " << spectrum[0] << ", bin 1 = " << spectrum[1] << ", bin 2 = " << spectrum[2]);
    CHECK(spectrum[0] == Catch::Approx(1.0f).epsilon(0.001)); // 0.5 through a one-sided scale of 2
    CHECK(spectrum[1] == Catch::Approx(0.5f).epsilon(0.01));  // the window's own skirt, not Nyquist
    CHECK(spectrum[2] < 0.002f);
}

// ---- the frame the hop produces ------------------------------------------------------------------------

TEST_CASE("a hop becomes a frame with the level and the waveform in it", "[analysis][frame]") {
    analyzer a;
    const auto sine = [](int64_t t) {
        return static_cast<float>(0.5 * std::sin(2.0 * std::numbers::pi * 128.0 * static_cast<double>(t) / k_n));
    };
    fill_window(a, sine);

    mp_analysis_frame frame{};
    frame.struct_size = sizeof frame;
    REQUIRE(a.try_get_latest(frame));
    CHECK(frame.struct_size == sizeof frame);
    CHECK(frame.sequence == 4); // one per hop, and the window is four hops long
    CHECK(a.frames() == 4);

    // RMS of a sine of amplitude 0.5 is 0.5/sqrt(2); peak is 0.5, to within where the last hop's samples fell.
    CHECK(frame.rms == Catch::Approx(0.5 / std::numbers::sqrt2).epsilon(0.02));
    CHECK(frame.peak == Catch::Approx(0.5f).epsilon(0.02));

    // The waveform is the newest hop itself, mixed to mono - the same samples, not a resampling of them.
    const int64_t first = 3 * analyzer::k_hop;
    double worst = 0.0;
    for (uint32_t i = 0; i < analyzer::k_waveform; ++i) {
        worst = std::max(worst, std::fabs(static_cast<double>(frame.waveform[i]) - sine(first + i)));
    }
    INFO("largest waveform disagreement " << worst);
    CHECK(worst < 1e-6);

    // E4-S2's fields are zero, not half-computed.
    CHECK(frame.spectral_centroid_hz == 0.0f);
    CHECK(frame.harmonic_ratio == 0.0f);
    CHECK(frame.onset == 0);
}

TEST_CASE("the frame carries the hop's place in the mixer's stream", "[analysis][frame]") {
    analyzer a;
    tap_block block = hop_of(5 * analyzer::k_hop, 2, [](int64_t) { return 0.0f; });
    block.qpc_ticks = 987654321;
    a.analyze(block);

    mp_analysis_frame frame{};
    REQUIRE(a.try_get_latest(frame));
    CHECK(frame.mixer_byte_pos == block.mixer_byte_pos);
    CHECK(frame.qpc_ticks == 987654321);
    CHECK(frame.sequence == 1);
}

TEST_CASE("nothing analysed means nothing to hand out", "[analysis][frame]") {
    analyzer a;
    mp_analysis_frame frame{};
    CHECK_FALSE(a.try_get_latest(frame));
    CHECK(a.frames() == 0);
}

// ---- AC-112: zero allocations per hop ------------------------------------------------------------------

#if defined(MP_DEBUG) && MP_DEBUG
TEST_CASE("a hop allocates nothing (RT_ASSERT_NO_ALLOC)", "[analysis][frame][rt]") {
    // Construction allocates - pffft's twiddle factors, and the thread when one is started. A hop must not:
    // everything it touches is a member sized at construction. The hops are built before the scope opens, so
    // what is counted is analyze() alone.
    analyzer a;
    std::vector<tap_block> hops;
    hops.reserve(64);
    for (int i = 0; i < 64; ++i) {
        hops.push_back(hop_of(static_cast<int64_t>(i) * analyzer::k_hop, 2, [](int64_t t) {
            return static_cast<float>(std::sin(0.01 * static_cast<double>(t)));
        }));
    }
    // Warm: the first call through pffft and the first publish touch pages, not the heap, but proving that is
    // the point of running the counted loop afterwards rather than including them in it.
    a.analyze(hops[0]);

    mp::rt::reset_violations();
    {
        mp::rt::scope inside;
        for (const tap_block& hop : hops) {
            a.analyze(hop);
        }
    }
    CHECK(mp::rt::violations() == 0);
    CHECK(a.frames() == 65);
}
#endif

// ---- AC-111: the rate frames arrive at, and when they stop ---------------------------------------------

TEST_CASE("frames arrive at the hop rate while playing and stop soon after a pause",
          "[analysis][frame][engine][rate]") {
    // 93.75 hops per second is 48000/512, so this is arithmetic and not a hope - but only if the audio is made
    // at the speed it is heard at. The offline engine renders as fast as it is asked to, so the test paces the
    // pulls against the clock: 480 frames every 10 ms, which is what a shared-mode device does.
    using clock = std::chrono::steady_clock;
    constexpr auto k_period = std::chrono::microseconds{10000}; // 480 frames at 48 kHz
    constexpr double k_expected_hz = 48000.0 / analyzer::k_hop;

    mp::tests::offline_engine fx;
    auto* core = reinterpret_cast<mp::audio::engine*>(fx.engine);
    analyzer& a = core->analysis_thread();
    REQUIRE(a.running());

    mp::tests::wav_spec spec;
    spec.seconds = 6.0;
    mp_track* track = fx.open(mp::tests::write_sine_wav(spec, "analysis-rate"));
    REQUIRE(mp_engine_play(fx.engine, track, 0) == MP_OK);

    std::vector<float> buffer(static_cast<size_t>(mp::tests::k_buffer_frames) * mp::tests::k_channels);
    const pacer wait;
    auto deadline = clock::now();
    const auto pull = [&] {
        deadline += k_period;
        wait.wait_until(deadline);
        REQUIRE(mp_engine_render(fx.engine, buffer.data(), mp::tests::k_buffer_frames) == MP_OK);
    };

    for (int i = 0; i < 50; ++i) { // 500 ms of settling: the first hops arrive while the guard fade is still in
        pull();
    }

    const uint64_t before = a.frames();
    const auto started = clock::now();
    for (int i = 0; i < 150; ++i) { // 1.5 s measured
        pull();
    }
    const double seconds = std::chrono::duration<double>(clock::now() - started).count();
    const double hz = static_cast<double>(a.frames() - before) / seconds;

    INFO("frames arrived at " << hz << " Hz over " << seconds << " s (48000/" << analyzer::k_hop << " = "
                              << k_expected_hz << ")");
    // One-sided in effect: frames cannot outrun the audio they are made from, and the audio is paced here. What
    // the lower bound catches is the analysis thread failing to keep up, or the pacer not pacing.
    CHECK(hz > k_expected_hz * 0.95);
    CHECK(hz < k_expected_hz * 1.02);

    // And the frame that came out of it is about the audio that went in.
    mp_analysis_frame frame{};
    frame.struct_size = sizeof frame;
    REQUIRE(mp_analysis_try_get_latest(fx.engine, &frame) == MP_OK);
    CHECK(frame.rms > 0.0f);
    CHECK(frame.sequence > 0);

    // Pause: the pull stage stops reading the mixer once the guard fade has run, so the tap stops being fed and
    // the analysis thread runs out of work. What is measured is when the last frame appeared, not when the call
    // returned - a frame produced after the audio stopped is a frame the visualizer would draw late.
    REQUIRE(mp_engine_pause(fx.engine) == MP_OK);
    const auto paused_at = clock::now();
    uint64_t last_count = a.frames();
    auto last_change = paused_at;
    while (clock::now() - paused_at < std::chrono::milliseconds{400}) {
        pull();
        if (a.frames() != last_count) {
            last_count = a.frames();
            last_change = clock::now();
        }
    }
    const double stopped_ms = std::chrono::duration<double, std::milli>(last_change - paused_at).count();

    INFO("last frame " << stopped_ms << " ms after pause (the " << mp::audio::engine::k_guard_fade_ms
                       << " ms guard fade is still real audio, and is part of it)");
    CHECK(stopped_ms < 100.0);

    mp_engine_stop(fx.engine, MP_FADE_NONE);
    mp_track_close(track);
}

TEST_CASE("the export refuses a frame until one has been produced", "[analysis][frame][engine][abi]") {
    mp::tests::offline_engine fx;
    mp_analysis_frame frame{};
    frame.struct_size = sizeof frame;
    // Nothing has pulled the mixer, so the tap has published no hop and there is no frame to copy.
    CHECK(mp_analysis_try_get_latest(fx.engine, &frame) == MP_E_STATE);
    CHECK(mp::tests::last_error().find("no analysis frame yet") != std::string::npos);

    // One hop is 512 frames; 200 ms from the NONSTOP mixer is plenty, and silence is still audio - a frame of
    // zeros is a frame.
    std::vector<float> buffer(static_cast<size_t>(mp::tests::k_buffer_frames) * mp::tests::k_channels);
    for (int i = 0; i < 20; ++i) {
        REQUIRE(mp_engine_render(fx.engine, buffer.data(), mp::tests::k_buffer_frames) == MP_OK);
    }
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds{2};
    while (mp_analysis_try_get_latest(fx.engine, &frame) != MP_OK) {
        REQUIRE(std::chrono::steady_clock::now() < deadline);
        std::this_thread::sleep_for(std::chrono::milliseconds{1});
    }
    CHECK(frame.struct_size == sizeof frame);
    CHECK(frame.sequence > 0);
}
