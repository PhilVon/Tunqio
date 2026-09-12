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
            const double angle =
                -2.0 * std::numbers::pi * static_cast<double>(k) * static_cast<double>(t) / static_cast<double>(n);
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
    INFO("largest disagreement " << worst << " at bin " << worst_bin << ", peak magnitude " << peak << ", relative "
                                 << relative);
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

    // E4-S2's fields are filled in now, and the frame carries them alongside the spectrum they came from. What
    // they are worth is test_analysis_features.cpp's business; what is checked here is that the frame E4-S1
    // publishes is the one the extraction wrote into, which a zero would not show.
    CHECK(frame.spectral_centroid_hz == Catch::Approx(128.0 * 48000.0 / k_n).epsilon(0.02));
    CHECK(frame.harmonic_ratio > 0.9f);
    CHECK(frame.bands[7] == Catch::Approx(0.5f).epsilon(0.01)); // bin 128 is 3000 Hz: the 2860-5695 Hz octave
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
        hops.push_back(hop_of(static_cast<int64_t>(i) * analyzer::k_hop, 2,
                              [](int64_t t) { return static_cast<float>(std::sin(0.01 * static_cast<double>(t))); }));
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

TEST_CASE("restarting the window after a dropped hop allocates nothing either", "[analysis][frame][rt]") {
    // The recovery path is on the same thread under the same rule (T-135), and it is the one that does the most
    // work per hop: an 8 KB memset, the extraction's own reset, and the band edges recomputed. Every hop here
    // says a hop was lost in front of it, so the counted loop is nothing but restarts.
    analyzer a;
    std::vector<tap_block> hops;
    hops.reserve(64);
    for (int i = 0; i < 64; ++i) {
        hops.push_back(hop_of(static_cast<int64_t>(i) * analyzer::k_hop, 2,
                              [](int64_t t) { return static_cast<float>(std::sin(0.01 * static_cast<double>(t))); }));
        hops.back().dropped_before = 1;
    }
    a.analyze(hops[0]);

    mp::rt::reset_violations();
    {
        mp::rt::scope inside;
        for (const tap_block& hop : hops) {
            a.analyze(hop);
        }
    }
    CHECK(mp::rt::violations() == 0);
    CHECK(a.discontinuities() == 65);
    CHECK(a.frames() == 0); // nothing was ever four contiguous hops, so there was never a frame to publish
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
    // The settling loop is allowed to fall behind its deadlines - the first pulls open a decoder and the analysis
    // thread takes its first FFTs through cold pages - and `deadline` accumulates that debt. Carried into the
    // measured window it would be spent there: pulls run with no wait until the deadline catches up, so 1.5 s of
    // audio is delivered in less than 1.5 s of wall clock and the measured rate comes out above the arithmetic
    // one (seen once in 25 runs: 95.88 Hz over 1.4705 s, a 29 ms debt, and 1.5/1.4705 is exactly the 1.02 that
    // failed). Re-basing the deadline on `started` is what makes the claim below a fact: the last pull cannot
    // return before started + 1.5 s, so the audio can be late but never early.
    deadline = started;
    for (int i = 0; i < 150; ++i) { // 1.5 s measured
        pull();
    }
    const double seconds = std::chrono::duration<double>(clock::now() - started).count();
    const double hz = static_cast<double>(a.frames() - before) / seconds;

    INFO("frames arrived at " << hz << " Hz over " << seconds << " s (48000/" << analyzer::k_hop << " = "
                              << k_expected_hz << ")");
    // One-sided in effect: frames cannot outrun the audio they are made from, and with the deadline re-based on
    // `started` the audio cannot outrun the clock either, so the upper bound only fires if that stops being
    // true. What the lower bound catches is the analysis thread failing to keep up, or the pacer not pacing.
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
    //
    // T-143: measured in AUDIO, not in wall clock. The claim is "frames stop within 100 ms of pause", and the
    // engine's part of that is how much more audio it mixes into the tap after the pause - the guard fade, and
    // the hop that fade lands in. Wall clock adds this loop's own scheduling to that number, so on a machine
    // that is also building, the pull stage misses its deadlines and the bound moves for a reason that has
    // nothing to do with the engine: 112.21 ms was read once in four runs on a busy box, against 54.9 ms
    // measured for AC-111. Counting the audio delivered instead is the same claim with the machine taken out of
    // it - the number is a property of the engine on any machine at any load. The wall clock is still reported,
    // because when the two disagree the difference IS this loop falling behind and a reader should see it.
    //
    // WHAT WAS AND WAS NOT MEASURED FOR THIS CHANGE, because the difference matters. The audio number is exact
    // and load-independent: 60 ms every run - the 50 ms guard fade plus the one hop it lands in - idle and with
    // 24 spinning threads on 8 cores alike, with the pull loop holding 0.999x to 1.003x of real time throughout.
    // The 112.21 ms that T-143 recorded was NOT reproduced here: on this machine the old wall-clock bound stayed
    // green under every load that could be applied to it. So this is not a fix for a failure seen today; it is
    // the removal of a term the engine does not control from an assertion about the engine, and the reason the
    // old number could move at all.
    const double ms_per_pull = 1000.0 * mp::tests::k_buffer_frames / mp::tests::k_rate;
    REQUIRE(mp_engine_pause(fx.engine) == MP_OK);
    const auto paused_at = clock::now();
    uint64_t last_count = a.frames();
    auto last_change = paused_at;
    uint32_t pulls = 0;
    uint32_t pulls_at_last_change = 0;
    while (clock::now() - paused_at < std::chrono::milliseconds{400}) {
        pull();
        ++pulls;
        if (a.frames() != last_count) {
            last_count = a.frames();
            last_change = clock::now();
            pulls_at_last_change = pulls;
        }
    }
    const double stopped_audio_ms = pulls_at_last_change * ms_per_pull;
    const double stopped_wall_ms = std::chrono::duration<double, std::milli>(last_change - paused_at).count();
    const double wall_ms = std::chrono::duration<double, std::milli>(clock::now() - paused_at).count();
    const double lateness = wall_ms / (pulls * ms_per_pull);

    INFO("the last frame arrived " << stopped_audio_ms << " ms of AUDIO after the pause (" << pulls_at_last_change
                                   << " pulls of " << ms_per_pull << " ms), at " << stopped_wall_ms
                                   << " ms of wall clock; the " << mp::audio::engine::k_guard_fade_ms
                                   << " ms guard fade is still real audio and is part of it. This loop delivered "
                                   << pulls * ms_per_pull << " ms of audio in " << wall_ms << " ms of wall clock ("
                                   << lateness
                                   << "x real time), which is the whole of the difference between the "
                                      "two numbers and is a fact about the machine, not the engine.");
    CHECK(stopped_audio_ms < 100.0);

    mp_engine_stop(fx.engine, MP_FADE_NONE);
    mp_track_close(track);
}

// ---- T-135: a ring overrun must not be slid across -----------------------------------------------------

namespace {

// Bin 130 of the analysis window, as a function of the absolute frame index, so consecutive hops of it are one
// signal. The bin matters: 130 cycles per 2048 samples is 32.5 per 512-frame hop, so an odd number of hops lost
// puts the splice half a cycle out of phase - the worst tear there is, and the one a window cannot hide. A bin
// centre also means the right answer is exact: one bin holds the tone and the Hann skirt holds two more.
constexpr uint32_t k_tone_bin = 130;
constexpr double k_tone_hz = 48000.0 * k_tone_bin / k_n; // 3046.875 Hz

float tone_at(int64_t frame) {
    return static_cast<float>(std::sin(2.0 * std::numbers::pi * k_tone_bin * static_cast<double>(frame) / k_n));
}

uint32_t loudest_bin(const mp_analysis_frame& frame) {
    uint32_t best = 0;
    for (uint32_t k = 1; k < analyzer::k_bins; ++k) {
        if (frame.spectrum[k] > frame.spectrum[best]) {
            best = k;
        }
    }
    return best;
}

} // namespace

TEST_CASE("a hop lost to an overrun restarts the window instead of being spliced into it",
          "[analysis][frame][overrun]") {
    // The whole defect, with no threads in it: the tap is filled past its ring and then drained, so the hop the
    // analyzer sees after the gap is genuinely not the one after the hop before it. What the analyzer used to do
    // was slide it in anyway, publishing a window of three hops of one piece of audio and one of another.
    mp::analysis::tap source;
    source.reset(2);
    analyzer a;

    std::vector<float> hop(static_cast<size_t>(analyzer::k_hop) * 2);
    const auto write_hops = [&](int64_t first_hop, int count) {
        for (int h = 0; h < count; ++h) {
            const int64_t first = (first_hop + h) * analyzer::k_hop;
            for (uint32_t f = 0; f < analyzer::k_hop; ++f) {
                hop[static_cast<size_t>(f) * 2] = hop[static_cast<size_t>(f) * 2 + 1] = tone_at(first + f);
            }
            source.write(hop.data(), analyzer::k_hop, 0);
        }
    };
    const auto drain = [&](int expected) {
        tap_block block;
        int read = 0;
        while (source.try_read(block)) {
            a.analyze(block);
            ++read;
        }
        REQUIRE(read == expected);
    };

    constexpr auto k_ring = static_cast<int64_t>(mp::analysis::tap::k_blocks);
    constexpr int k_lost = 5; // odd, so the splice is half a cycle of the tone out of phase

    write_hops(0, static_cast<int>(k_ring)); // fills the ring
    write_hops(k_ring, k_lost);              // nowhere to put these: the tap drops them
    REQUIRE(source.dropped() == k_lost);
    drain(static_cast<int>(k_ring)); // hops 0..15, contiguous, and a true spectrum out of them

    mp_analysis_frame before{};
    REQUIRE(a.try_get_latest(before));
    REQUIRE(loudest_bin(before) == k_tone_bin);
    REQUIRE(before.sequence == k_ring);
    REQUIRE(a.discontinuities() == 0);

    // The next hop the analyzer is given is hop 21, and the window holds hops 13, 14 and 15. Sliding 21 in
    // makes a spectrum of a signal that jumps phase three quarters of the way through - which is what this
    // measured before the fix: the loudest bin wandered off 130 and the centroid read about 4 kHz.
    write_hops(k_ring + k_lost, 1);
    drain(1);

    mp_analysis_frame across{};
    REQUIRE(a.try_get_latest(across));
    CHECK(a.discontinuities() == 1);
    INFO("across the gap: loudest bin " << loudest_bin(across) << ", centroid " << across.spectral_centroid_hz
                                        << " Hz, sequence " << across.sequence);
    // Nothing new was published, because there was nothing true to publish: the newest frame is still the last
    // contiguous one, unchanged, and a consumer polling sees the picture it already had rather than a wrong one.
    CHECK(across.sequence == before.sequence);
    CHECK(loudest_bin(across) == k_tone_bin);
    CHECK(across.spectral_centroid_hz == Catch::Approx(k_tone_hz).epsilon(0.02));

    // Three more hops and the window is four contiguous hops again, so frames resume - and the one that comes
    // out carries the restart, which is how a consumer holding anything across frames learns to start again.
    write_hops(k_ring + k_lost + 1, 3);
    drain(3);

    mp_analysis_frame after{};
    REQUIRE(a.try_get_latest(after));
    INFO("after refilling: loudest bin " << loudest_bin(after) << ", centroid " << after.spectral_centroid_hz << " Hz");
    CHECK(after.sequence == before.sequence + 1); // three hops made no frame; the fourth made one
    CHECK(loudest_bin(after) == k_tone_bin);
    CHECK(after.spectral_centroid_hz == Catch::Approx(k_tone_hz).epsilon(0.02));
    CHECK(after.discontinuities == 1);
    CHECK(after.onset == 0); // the flux across a gap is a difference between two signals, not an onset
    CHECK(a.discontinuities() == 1);
}

TEST_CASE("an unbroken stream publishes a frame per hop and never reports a discontinuity",
          "[analysis][frame][overrun]") {
    // The other half of the claim: the cost of all this in the normal case is nothing. Sixty-four contiguous
    // hops through the tap, drained as they arrive, are sixty-four frames.
    mp::analysis::tap source;
    source.reset(2);
    analyzer a;

    std::vector<float> hop(static_cast<size_t>(analyzer::k_hop) * 2);
    tap_block block;
    for (int h = 0; h < 64; ++h) {
        const int64_t first = static_cast<int64_t>(h) * analyzer::k_hop;
        for (uint32_t f = 0; f < analyzer::k_hop; ++f) {
            hop[static_cast<size_t>(f) * 2] = hop[static_cast<size_t>(f) * 2 + 1] = tone_at(first + f);
        }
        source.write(hop.data(), analyzer::k_hop, 0);
        REQUIRE(source.try_read(block));
        a.analyze(block);
    }

    CHECK(source.dropped() == 0);
    CHECK(a.frames() == 64);
    CHECK(a.discontinuities() == 0);

    mp_analysis_frame frame{};
    REQUIRE(a.try_get_latest(frame));
    CHECK(frame.sequence == 64);
    CHECK(frame.discontinuities == 0);
    CHECK(loudest_bin(frame) == k_tone_bin);
}

TEST_CASE("a headless render faster than real time publishes only continuous frames",
          "[analysis][frame][engine][overrun]") {
    // How this was found: the offline engine renders as fast as it is asked to, the tap's ring holds 170 ms of
    // audio, and a second of audio produced in a few milliseconds overruns it. Every frame the analysis thread
    // publishes while that is happening still has to be the transform of 2048 samples that were next to each
    // other, and the only oracle needed is that the tone is where the tone is.
    mp::tests::offline_engine fx;
    auto* core = reinterpret_cast<mp::audio::engine*>(fx.engine);
    analyzer& a = core->analysis_thread();
    REQUIRE(a.running());

    mp::tests::wav_spec spec;
    spec.seconds = 20.0;
    spec.frequency_hz = k_tone_hz; // a bin centre of the analysis window, so the right answer is one bin
    spec.amplitude = 0.5;
    mp_track* track = fx.open(mp::tests::write_sine_wav(spec, "analysis-overrun"));
    REQUIRE(mp_engine_play(fx.engine, track, 0) == MP_OK);

    // 341 ms of audio at a time and then a few milliseconds to watch what comes out. The burst is twice what
    // the ring holds, so the overrun is arithmetic rather than a race with the analysis thread; the pause is
    // what makes the result observable, because rendering all twenty seconds in one go overruns just as surely
    // but leaves how many frames a test gets to see up to the scheduler.
    constexpr uint32_t k_burst_frames = 32 * analyzer::k_hop; // 32 hops into a 16-hop ring
    std::vector<float> buffer(static_cast<size_t>(mp::tests::k_buffer_frames) * mp::tests::k_channels);
    const int64_t settled_frames = 48000 / 4; // past the guard fade, so the window is full of steady tone
    uint32_t checked = 0;
    uint32_t last_sequence = 0;
    uint32_t wrong_bin = 0;
    uint32_t worst_bin = 0;
    const auto bursts = static_cast<int>(spec.seconds * 48000 / k_burst_frames);
    for (int b = 0; b < bursts; ++b) {
        for (uint32_t done = 0; done < k_burst_frames; done += mp::tests::k_buffer_frames) {
            REQUIRE(mp_engine_render(fx.engine, buffer.data(), mp::tests::k_buffer_frames) == MP_OK);
        }
        for (int i = 0; i < 10; ++i) {
            std::this_thread::sleep_for(std::chrono::milliseconds{1});
            mp_analysis_frame frame{};
            frame.struct_size = sizeof frame;
            if (mp_analysis_try_get_latest(fx.engine, &frame) != MP_OK || frame.sequence == last_sequence) {
                continue;
            }
            last_sequence = frame.sequence;
            if (frame.mixer_byte_pos / (mp::tests::k_channels * static_cast<int64_t>(sizeof(float))) < settled_frames) {
                continue;
            }
            ++checked;
            const uint32_t bin = loudest_bin(frame);
            if (bin != k_tone_bin) {
                ++wrong_bin;
                worst_bin = bin;
            }
        }
    }

    const uint64_t dropped = core->analysis_tap().dropped();
    INFO("the tap dropped " << dropped << " hop(s) and the analysis restarted " << a.discontinuities() << " time(s); "
                            << checked << " published frame(s) inspected, " << wrong_bin
                            << " with the tone somewhere other than bin " << k_tone_bin << " (worst " << worst_bin
                            << ")");
    // The condition has to have happened for the rest to mean anything.
    REQUIRE(dropped > 0);
    REQUIRE(a.discontinuities() > 0);
    REQUIRE(checked >= 10);
    CHECK(wrong_bin == 0);

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
