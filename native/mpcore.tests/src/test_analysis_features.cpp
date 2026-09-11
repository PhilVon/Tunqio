// E4-S2: the four features extracted from the spectrum, each against a signal generated here rather than
// fixtured, so what is being measured is the extraction and not a recording of it.
//
// AC-115 is four claims and each gets its own signal: a 1 kHz sine for the centroid, a sine against white noise
// for the harmonic ratio, a click train for onset timing, and sustained tones for the false positives an
// over-eager detector earns by passing the first three.
//
// AC-116 is the cost of the extraction per hop. It is gated by a timed loop in the ordinary suite, the way
// E1-S8's tap budget is, and reported as a Catch2 benchmark that is hidden from the default run so that CI does
// not spend a minute on bootstrap resampling three times over: `mpcore.tests [!benchmark]` prints it.
#include "mpcore.h"

#include "analysis/analyzer.h"
#include "analysis/features.h"
#include "common/rt_guard.h"

#include <algorithm>
#include <catch2/catch_amalgamated.hpp>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <functional>
#include <numbers>
#include <random>
#include <string>
#include <vector>

namespace {

using mp::analysis::analyzer;
using mp::analysis::features;
using mp::analysis::tap_block;

constexpr uint32_t k_rate = 48000;
constexpr uint32_t k_hop = analyzer::k_hop;          // 512 frames
constexpr double k_hop_ms = 1000.0 * k_hop / k_rate; // 10.667 ms

// A hop of interleaved stereo whose value at each frame comes from `f`, with the absolute frame index counted
// from `first` so a test can feed one continuous signal hop after hop.
tap_block hop_of(int64_t first_frame, const std::function<float(int64_t)>& f) {
    tap_block block{};
    block.frames = tap_block::k_frames;
    block.channels = 2;
    block.mixer_byte_pos = first_frame * 2 * static_cast<int64_t>(sizeof(float));
    for (uint32_t i = 0; i < tap_block::k_frames; ++i) {
        const float value = f(first_frame + i);
        block.samples[static_cast<size_t>(i) * 2] = value;
        block.samples[static_cast<size_t>(i) * 2 + 1] = value;
    }
    return block;
}

// Runs `hops` hops of `f` through the analyzer and hands back every published frame, so a test can look at the
// sequence of them and not only the last.
std::vector<mp_analysis_frame> run(analyzer& a, uint32_t hops, const std::function<float(int64_t)>& f) {
    std::vector<mp_analysis_frame> frames;
    frames.reserve(hops);
    for (uint32_t h = 0; h < hops; ++h) {
        a.analyze(hop_of(static_cast<int64_t>(h) * k_hop, f));
        mp_analysis_frame frame{};
        frame.struct_size = sizeof frame;
        REQUIRE(a.try_get_latest(frame));
        frames.push_back(frame);
    }
    return frames;
}

float sine(int64_t t, double hz, double amplitude = 0.5, double phase = 0.0) {
    return static_cast<float>(amplitude *
                              std::sin(2.0 * std::numbers::pi * hz * static_cast<double>(t) / k_rate + phase));
}

// A spectrum with a peak and a floor, which is what the extraction's cost is measured over: every branch taken,
// no bin left at an exact zero.
std::vector<float> busy_spectrum(uint32_t seed) {
    std::mt19937 rng{seed};
    std::uniform_real_distribution<float> uniform{0.0f, 0.01f};
    std::vector<float> spectrum(features::k_bins);
    for (uint32_t k = 0; k < features::k_bins; ++k) {
        spectrum[k] = uniform(rng) + (k == 43 ? 1.0f : 0.0f);
    }
    return spectrum;
}

} // namespace

// ---- AC-115, first claim: the centroid of a 1 kHz sine ---------------------------------------------------

TEST_CASE("a 1 kHz sine puts the spectral centroid within 2% of 1 kHz", "[analysis][features][centroid]") {
    // 1000 Hz is bin 42.67 of a 2048-point FFT at 48 kHz - deliberately not a bin centre, so the Hann window's
    // skirt is asymmetrically sampled and the centroid has to survive that rather than being handed a single
    // non-zero bin. The window is four hops long, so the first three frames are still filling it and only the
    // fourth onwards sees a whole window of tone.
    analyzer a;
    const std::vector<mp_analysis_frame> frames = run(a, 12, [](int64_t t) { return sine(t, 1000.0); });

    double worst = 0.0;
    for (size_t i = 3; i < frames.size(); ++i) {
        worst = std::max(worst, std::fabs(frames[i].spectral_centroid_hz - 1000.0) / 1000.0);
    }
    INFO("worst centroid error over the settled frames: " << worst * 100.0 << "% (last frame "
                                                          << frames.back().spectral_centroid_hz << " Hz)");
    CHECK(worst < 0.02);

    // Not a coincidence of one frequency: the centroid follows the tone.
    for (const double hz : {200.0, 3000.0, 7000.0}) {
        analyzer b;
        const std::vector<mp_analysis_frame> f = run(b, 8, [hz](int64_t t) { return sine(t, hz); });
        INFO(hz << " Hz reads " << f.back().spectral_centroid_hz << " Hz");
        CHECK(f.back().spectral_centroid_hz == Catch::Approx(hz).epsilon(0.02));
    }
}

TEST_CASE("silence has no centroid rather than a wrong one", "[analysis][features][centroid]") {
    analyzer a;
    const std::vector<mp_analysis_frame> frames = run(a, 4, [](int64_t) { return 0.0f; });
    CHECK(frames.back().spectral_centroid_hz == 0.0f);
    CHECK(frames.back().harmonic_ratio == 0.0f);
    CHECK(frames.back().onset == 0);
}

// ---- AC-115, second claim: noise against a sine, harmonic ratio ------------------------------------------

TEST_CASE("the harmonic ratio separates a sine from noise by more than 0.4", "[analysis][features][harmonic]") {
    // What this field measures, since the name does not say: one minus the spectral flatness (Wiener entropy),
    // the geometric mean of the bin magnitudes over their arithmetic mean. A flat spectrum has the two means
    // equal and reads 0; a spectrum that is three loud bins and a thousand quiet ones reads ~1. It is "is there
    // a note here", not a count of harmonics - an inharmonic bell reads high and a detuned unison reads high.
    analyzer tonal;
    const auto tone = run(tonal, 8, [](int64_t t) { return sine(t, 1000.0); });

    std::mt19937 rng{20250911};
    std::uniform_real_distribution<float> uniform{-0.5f, 0.5f};
    std::vector<float> noise(16 * k_hop);
    for (float& s : noise) {
        s = uniform(rng);
    }
    analyzer noisy;
    const auto hiss = run(noisy, 8, [&noise](int64_t t) { return noise[static_cast<size_t>(t)]; });

    const float sine_ratio = tone.back().harmonic_ratio;
    const float noise_ratio = hiss.back().harmonic_ratio;
    INFO("sine " << sine_ratio << ", white noise " << noise_ratio << ", separation " << sine_ratio - noise_ratio);
    CHECK(sine_ratio > 0.9f);
    CHECK(noise_ratio < 0.5f);
    CHECK(sine_ratio - noise_ratio > 0.4f);

    // Both ends stay inside the range the preset contract promises, whatever the signal.
    for (const mp_analysis_frame& frame : {tone.back(), hiss.back()}) {
        CHECK(frame.harmonic_ratio >= 0.0f);
        CHECK(frame.harmonic_ratio <= 1.0f);
    }
}

// ---- AC-115, third claim: click train onsets within 10 ms ------------------------------------------------

TEST_CASE("a click train is detected hop by hop within 10 ms of each click", "[analysis][features][onset]") {
    // The clock this can be read against. The frame carries `mixer_byte_pos`, the first frame of the hop it was
    // made from, and the flag says "an onset happened during this hop" - a hop being 10.67 ms, that is the
    // resolution of the answer and no convention improves on it. The point estimate is therefore the hop's
    // midpoint, and the claim checked here is that the true click is within 10 ms of it. What has to be true for
    // that to hold is that the click is flagged in the hop it actually fell in and not the one after, which is
    // checked on its own below and is the harder half: a click at the end of a hop sits where the Hann taper is
    // near zero, so it is nearly invisible in that frame and unmissable in the next. It is flagged in its own
    // hop anyway because the threshold is a fraction of the frame's own spectrum - the click is faint there, but
    // so is everything else, and it is the ratio that fires.
    //
    // The period is 5000 frames - 104.2 ms, deliberately not a multiple of the 512-frame hop - so successive
    // clicks land at 29 different offsets inside their hop, from 0 to 488 frames.
    constexpr int64_t k_period = 5000;
    constexpr int64_t k_first = 1000;
    constexpr uint32_t k_hops = 280; // ~3 s
    constexpr int64_t k_width = 16;  // 0.33 ms: short enough to be a point in time at this resolution

    std::vector<int64_t> clicks;
    for (int64_t t = k_first; t + k_width < static_cast<int64_t>(k_hops) * k_hop; t += k_period) {
        clicks.push_back(t);
    }
    REQUIRE(clicks.size() >= 25);

    const auto click_train = [&clicks](int64_t t) {
        for (const int64_t start : clicks) {
            if (t >= start && t < start + k_width) {
                return static_cast<float>(0.9 * std::exp(-static_cast<double>(t - start) / 4.0));
            }
        }
        return 0.0f;
    };

    analyzer a;
    const std::vector<mp_analysis_frame> frames = run(a, k_hops, click_train);

    std::vector<size_t> flagged;
    for (size_t i = 0; i < frames.size(); ++i) {
        if (frames[i].onset != 0) {
            flagged.push_back(i);
        }
    }

    INFO("clicks " << clicks.size() << ", onsets flagged " << flagged.size());
    CHECK(flagged.size() == clicks.size());
    REQUIRE_FALSE(flagged.empty());

    double worst_ms = 0.0;
    int64_t worst_click = 0;
    size_t wrong_hop = 0;
    for (const int64_t click : clicks) {
        // The flagged frame whose hop midpoint is nearest the click, which is the reading a consumer takes.
        size_t best = flagged.front();
        double best_ms = 1e9;
        for (const size_t frame : flagged) {
            const double midpoint_ms = 1000.0 * (static_cast<double>(frame) * k_hop + k_hop / 2.0) / k_rate;
            const double error = std::fabs(midpoint_ms - 1000.0 * static_cast<double>(click) / k_rate);
            if (error < best_ms) {
                best_ms = error;
                best = frame;
            }
        }
        if (best != static_cast<size_t>(click / k_hop)) {
            ++wrong_hop;
        }
        if (best_ms > worst_ms) {
            worst_ms = best_ms;
            worst_click = click;
        }
    }

    INFO("worst error " << worst_ms << " ms (click at frame " << worst_click << ", " << worst_click % k_hop
                        << " frames into its hop); half a hop is " << k_hop_ms / 2.0
                        << " ms; clicks flagged in a hop other than their own: " << wrong_hop);
    CHECK(wrong_hop == 0);
    CHECK(worst_ms < 10.0);
}

// ---- AC-115, fourth claim: no false positives on a sustained tone ----------------------------------------

TEST_CASE("a sustained tone produces one onset when it starts and none after", "[analysis][features][onset]") {
    // The claim the first three do not make. A detector that fires on a click train because its threshold is
    // near zero fires on a stationary signal too: the flux of a steady tone is not exactly zero, it is float
    // noise plus whatever the sliding window does, and a threshold made only of the recent mean and standard
    // deviation of a stationary flux crosses itself every few dozen frames by construction.
    //
    // Five signals, each ~2 s: tones on a bin centre and off it, a tone with a little noise under it (the case
    // that is stationary but not silent in the flux), and a three-partial tone. Frames 0-7 are the window
    // filling and the attack, which is a real onset and must be flagged; everything after is sustained.
    struct sustained {
        const char* name;
        std::function<float(int64_t)> signal;
    };

    std::mt19937 rng{424242};
    std::normal_distribution<float> gaussian{0.0f, 0.002f};
    std::vector<float> hiss(200 * k_hop);
    for (float& s : hiss) {
        s = gaussian(rng);
    }

    const std::vector<sustained> cases{
        {"440 Hz", [](int64_t t) { return sine(t, 440.0); }},
        {"1000 Hz, bin 42.67", [](int64_t t) { return sine(t, 1000.0); }},
        {"1007.8125 Hz, bin 43 exactly", [](int64_t t) { return sine(t, 1007.8125); }},
        {"440 Hz with hiss under it", [&hiss](int64_t t) { return sine(t, 440.0) + hiss[static_cast<size_t>(t)]; }},
        {"220/440/660 Hz",
         [](int64_t t) { return sine(t, 220.0, 0.4) + sine(t, 440.0, 0.25, 1.0) + sine(t, 660.0, 0.15, 2.2); }},
    };

    for (const sustained& c : cases) {
        analyzer a;
        const std::vector<mp_analysis_frame> frames = run(a, 180, c.signal);

        uint32_t attack = 0;
        std::vector<size_t> late;
        for (size_t i = 0; i < frames.size(); ++i) {
            if (frames[i].onset == 0) {
                continue;
            }
            if (i < 8) {
                ++attack;
            } else {
                late.push_back(i);
            }
        }
        std::string where;
        for (const size_t frame : late) {
            where += " " + std::to_string(frame);
        }
        // Not a near miss: the last hop's flux is well under the bar it was held against, so this passes with
        // margin rather than by a hair, and a change that halved the margin would show here before it showed as
        // a red test somewhere else.
        const float flux = a.extraction().flux();
        const float threshold = a.extraction().onset_threshold();
        INFO(c.name << ": onsets in the attack " << attack << ", onsets in the sustain " << late.size() << where
                    << "; the last hop's flux was " << flux << " against a threshold of " << threshold);
        CHECK(attack >= 1);  // the tone starting is an onset and must not be missed
        CHECK(late.empty()); // the tone continuing is not
        CHECK(flux < threshold * 0.5f);
    }
}

TEST_CASE("white noise is not a stream of onsets either", "[analysis][features][onset]") {
    // Noise is the other way to earn false positives: every hop's spectrum differs from the last, so the flux is
    // large and never settles. What keeps it under the bar is that the threshold is a fraction of the frame's
    // own spectrum, which is large too.
    std::mt19937 rng{991};
    std::uniform_real_distribution<float> uniform{-0.4f, 0.4f};
    std::vector<float> noise(200 * k_hop);
    for (float& s : noise) {
        s = uniform(rng);
    }

    analyzer a;
    const std::vector<mp_analysis_frame> frames =
        run(a, 180, [&noise](int64_t t) { return noise[static_cast<size_t>(t)]; });

    size_t late = 0;
    for (size_t i = 8; i < frames.size(); ++i) {
        late += frames[i].onset != 0 ? 1 : 0;
    }
    INFO("onsets in 180 hops of white noise after the first eight: " << late);
    CHECK(late == 0);
}

// ---- the octave bands ------------------------------------------------------------------------------------

TEST_CASE("the ten octave bands partition the spectrum", "[analysis][features][bands]") {
    // Ten, not the six the roadmap line says: MP_ANALYSIS_OCTAVE_BANDS is ten, the managed binding is ten, and
    // E4-S3's preset constant buffer already carries ten. Twenty hertz to twenty kilohertz is ten octaves.
    features f{k_rate};
    CHECK(f.band_first_bin(0) == 1); // bin 0 is DC and belongs to no octave
    CHECK(f.band_last_bin(features::k_bands - 1) == features::k_bins);
    for (uint32_t band = 0; band + 1 < features::k_bands; ++band) {
        INFO("band " << band << " is bins [" << f.band_first_bin(band) << ", " << f.band_last_bin(band)
                     << ") = " << f.band_first_bin(band) * f.bin_hz() << " Hz up");
        CHECK(f.band_last_bin(band) == f.band_first_bin(band + 1)); // no gap, no overlap
        CHECK(f.band_first_bin(band + 1) >= f.band_first_bin(band));
    }
    // And they really are octaves once the resolution allows it: above the bottom few bands, each band starts
    // where the one below it started, doubled.
    for (uint32_t band = 4; band + 1 < features::k_bands; ++band) {
        CHECK(f.band_first_bin(band + 1) == Catch::Approx(2.0 * f.band_first_bin(band)).margin(1.0));
    }
}

TEST_CASE("a band carries the amplitude of what is in it", "[analysis][features][bands]") {
    // Summed in quadrature and divided by the Hann window's 1.5-bin noise bandwidth, so a full-scale sine reads
    // 1.0 in its own band wherever inside it the tone sits. 703-1430 Hz is band 5.
    for (const double hz : {750.0, 1000.0, 1007.8125, 1200.0}) {
        analyzer a;
        const auto frames = run(a, 8, [hz](int64_t t) { return sine(t, hz, 1.0); });
        const mp_analysis_frame& frame = frames.back();
        double total = 0.0;
        for (uint32_t band = 0; band < features::k_bands; ++band) {
            total += frame.bands[band];
        }
        INFO(hz << " Hz: band 5 = " << frame.bands[5] << ", every band summed = " << total);
        CHECK(frame.bands[5] == Catch::Approx(1.0f).epsilon(0.01));
        CHECK(total - frame.bands[5] < 0.02); // and essentially nothing anywhere else
    }

    // A tone an octave up moves up a band, which is what makes them octave bands rather than ten numbers.
    analyzer low;
    analyzer high;
    const auto at_500 = run(low, 8, [](int64_t t) { return sine(t, 500.0, 1.0); });
    const auto at_2000 = run(high, 8, [](int64_t t) { return sine(t, 2000.0, 1.0); });
    CHECK(at_500.back().bands[4] == Catch::Approx(1.0f).epsilon(0.01));
    CHECK(at_2000.back().bands[6] == Catch::Approx(1.0f).epsilon(0.01));

    // A tone sitting on a band edge is split between the two, and the split is in quadrature: the two bands do
    // not each read 1.0 and they do not sum to 1.0 either. Worth writing down because a preset author reading a
    // bar chart of these will see it. 1400 Hz is 30 Hz below the 1430 Hz edge.
    analyzer edge;
    const auto at_1400 = run(edge, 8, [](int64_t t) { return sine(t, 1400.0, 1.0); });
    const float below = at_1400.back().bands[5];
    const float above = at_1400.back().bands[6];
    INFO("1400 Hz splits as band 5 = " << below << ", band 6 = " << above << ", in quadrature "
                                       << std::sqrt(below * below + above * above));
    CHECK(below > 0.5f);
    CHECK(above > 0.05f);
    CHECK(std::sqrt(below * below + above * above) == Catch::Approx(1.0f).epsilon(0.01));
}

TEST_CASE("the band edges follow the sample rate", "[analysis][features][bands]") {
    // The edges are frequencies, so at twice the rate the same octave is half as many bins away.
    features at_48{48000};
    features at_96{96000};
    for (uint32_t band = 1; band < features::k_bands; ++band) {
        const uint32_t wide = at_48.band_first_bin(band);
        const uint32_t narrow = at_96.band_first_bin(band);
        INFO("band " << band << ": bin " << wide << " at 48 kHz, bin " << narrow << " at 96 kHz");
        CHECK(narrow <= wide);
    }
    CHECK(at_96.bin_hz() == Catch::Approx(2.0f * at_48.bin_hz()));
    CHECK(at_96.band_last_bin(features::k_bands - 1) == features::k_bins);
}

// ---- AC-112 still holds with E4-S2's work in the hop -----------------------------------------------------

#if defined(MP_DEBUG) && MP_DEBUG
TEST_CASE("extraction allocates nothing on the analysis thread", "[analysis][features][rt]") {
    // E4-S1's AC-112 covers analyze() as a whole and still does; this is the same assertion aimed at the part
    // E4-S2 added, including the nth_element the adaptive threshold's median runs over a fixed local array.
    features f{k_rate};
    const std::vector<float> spectrum = busy_spectrum(7);
    mp_analysis_frame frame{};
    frame.struct_size = sizeof frame;
    f.extract(spectrum.data(), frame); // warm: the first call touches pages, not the heap

    mp::rt::reset_violations();
    {
        mp::rt::scope inside;
        for (int i = 0; i < 128; ++i) {
            f.extract(spectrum.data(), frame);
        }
    }
    CHECK(mp::rt::violations() == 0);
}
#endif

// ---- AC-116: what extraction costs per hop ---------------------------------------------------------------

TEST_CASE("extraction costs well under the 4 ms budget per hop", "[analysis][features][benchmark]") {
    // AC-116. The budget is generous because a hop is 10.67 ms of audio and the analysis thread has to do the
    // FFT as well; what would breach it is an accidental quadratic, not a constant factor. Timed over a spectrum
    // that exercises every branch - the flux history full, the median taken, every band non-empty - and reported
    // for the whole hop too, because the thread pays for both and only one of them is E4-S2's.
    features f{k_rate};
    const std::vector<float> spectrum = busy_spectrum(31337);
    mp_analysis_frame frame{};
    frame.struct_size = sizeof frame;
    for (int i = 0; i < 64; ++i) { // fill the flux history so the median is over a full window
        f.extract(spectrum.data(), frame);
    }

    constexpr int k_iterations = 2000;
    const auto started = std::chrono::steady_clock::now();
    for (int i = 0; i < k_iterations; ++i) {
        f.extract(spectrum.data(), frame);
    }
    const double extract_ms =
        std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started).count() / k_iterations;

    analyzer a;
    const tap_block block = hop_of(0, [](int64_t t) { return sine(t, 1000.0); });
    for (int i = 0; i < 64; ++i) {
        a.analyze(block);
    }
    const auto whole_started = std::chrono::steady_clock::now();
    for (int i = 0; i < k_iterations; ++i) {
        a.analyze(block);
    }
    const double hop_ms =
        std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - whole_started).count() /
        k_iterations;

    INFO("extraction " << extract_ms << " ms per hop, whole hop with the FFT " << hop_ms
                       << " ms; the budget is 4 ms and a hop is " << k_hop_ms << " ms of audio");
    CHECK(extract_ms < 4.0);
    CHECK(hop_ms < 4.0);
}

TEST_CASE("feature extraction benchmark", "[.][!benchmark][analysis][features]") {
    // Hidden from the default run: `mpcore.tests [!benchmark]` reports it. The gate is the timed loop above;
    // this is the distribution behind the number, which is what a Catch2 benchmark is for.
    features f{k_rate};
    const std::vector<float> spectrum = busy_spectrum(31337);
    mp_analysis_frame frame{};
    frame.struct_size = sizeof frame;
    for (int i = 0; i < 64; ++i) {
        f.extract(spectrum.data(), frame);
    }
    analyzer a;
    const tap_block block = hop_of(0, [](int64_t t) { return sine(t, 1000.0); });
    a.analyze(block);

    BENCHMARK("extract one hop") {
        f.extract(spectrum.data(), frame);
        return frame.spectral_centroid_hz;
    };
    BENCHMARK("one whole hop, FFT and extraction") {
        a.analyze(block);
        return a.frames();
    };
}
