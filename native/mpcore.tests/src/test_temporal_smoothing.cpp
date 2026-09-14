// Temporal smoothing (T-184, ABI 0.19): the attack and decay envelope the renderer applies to the analysis frame
// before a preset sees it.
//
// Two halves. The envelope itself is a pure object (render/temporal_envelope.h) that takes a frame, two time
// constants and a step in seconds, so everything about its CURVE is asserted here on synthetic frames with no
// device and no clock: a step up and a step down drawn at 60 Hz and at 144 Hz, attack and decay moved one at a
// time, off, silence, NaN and a discontinuity. The export is asserted on a headless renderer below that. What the
// envelope does to a PICTURE - that off is byte-identical, that eased captures agree in time across frame rates,
// and that no shipped preset can flash at the extremes - needs the presets and their helpers, and is in
// test_preset_golden.cpp beside the flash tests it extends.
#include "mpcore.h"

#include "render/temporal_envelope.h"

#include <algorithm>
#include <catch2/catch_amalgamated.hpp>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <memory>
#include <vector>

namespace {

using mp::render::envelope_times;
using mp::render::temporal_envelope;

// 6.5 KB each, so on the heap: Catch2's sections re-enter the test body and a handful of these on the stack of a
// test thread is a stack the ASan build notices.
std::unique_ptr<mp_analysis_frame> frame_at(float level) {
    auto f = std::make_unique<mp_analysis_frame>();
    std::memset(f.get(), 0, sizeof *f);
    f->struct_size = sizeof(mp_analysis_frame);
    for (float& bin : f->spectrum) {
        bin = level;
    }
    for (float& band : f->bands) {
        band = level;
    }
    for (size_t i = 0; i < MP_ANALYSIS_WAVEFORM_SAMPLES; ++i) {
        f->waveform[i] = (i % 2 == 0 ? level : -level); // signed, and never smoothed
    }
    f->rms = level;
    f->peak = level;
    f->spectral_centroid_hz = 1600.0f + 4000.0f * level;
    f->harmonic_ratio = level;
    f->onset = level > 0.5f ? 1 : 0;
    return f;
}

// One point of a drawn curve: when it was drawn, and the value a smoothed field had then.
struct point {
    double t = 0.0;
    float spectrum0 = 0.0f;
    float band0 = 0.0f;
    float rms = 0.0f;
};

// Silence until t = 0, full scale from 0 to `fall_at`, silence again after - drawn at `hz` for `seconds`. The first
// frame primes the envelope on silence, which is what a renderer that has been drawing silence holds.
std::vector<point> draw_step(double hz, envelope_times times, double fall_at, double seconds) {
    temporal_envelope envelope;
    const auto quiet = frame_at(0.0f);
    const auto loud = frame_at(1.0f);
    auto out = std::make_unique<mp_analysis_frame>();
    envelope.apply(*quiet, times, 0.0, *out);

    std::vector<point> curve;
    const double dt = 1.0 / hz;
    const auto frames = static_cast<long>(std::llround(seconds * hz));
    for (long n = 1; n <= frames; ++n) {
        // The input held over the interval ENDING at this frame: the frame at t sees what arrived before it.
        const double start = static_cast<double>(n - 1) * dt;
        const bool is_loud = start >= 0.0 && start < fall_at - 1e-9;
        envelope.apply(is_loud ? *loud : *quiet, times, dt, *out);
        curve.push_back({static_cast<double>(n) * dt, out->spectrum[0], out->bands[0], out->rms});
    }
    return curve;
}

// The value a curve has at time t, found by instant rather than by index.
const point& at_time(const std::vector<point>& curve, double t) {
    for (const point& p : curve) {
        if (std::fabs(p.t - t) < 1e-6) {
            return p;
        }
    }
    FAIL("no frame at t = " << t);
    return curve.front();
}

bool finite_and_normal(float v) {
    return std::isfinite(v) && std::fpclassify(v) != FP_SUBNORMAL;
}

} // namespace

TEST_CASE("temporal smoothing off is an exact pass-through", "[render][smoothing]") {
    temporal_envelope envelope;
    auto out = std::make_unique<mp_analysis_frame>();
    const envelope_times off{};
    REQUIRE(off.off());
    // A frame whose every field is different from its neighbour's, so a field that is eased or copied from the
    // wrong place shows up as bytes that differ.
    for (int k = 0; k < 20; ++k) {
        const auto in = frame_at(static_cast<float>((k * 37) % 11) / 10.0f);
        in->sequence = static_cast<uint32_t>(k + 1);
        in->spectrum[k] = 0.123f * static_cast<float>(k);
        envelope.apply(*in, off, 1.0 / 60.0, *out);
        INFO("frame " << k);
        REQUIRE(std::memcmp(in.get(), out.get(), sizeof(mp_analysis_frame)) == 0);
    }
}

TEST_CASE("a step up and a step down trace the same curve in time at 60 Hz and at 144 Hz", "[render][smoothing]") {
    const envelope_times times{60.0f, 400.0f};
    constexpr double k_fall_at = 0.5;
    // 1/12 s is a whole number of frames at both rates (5 and 12), so these instants are drawn by both.
    const std::vector<point> slow = draw_step(60.0, times, k_fall_at, 1.5);
    const std::vector<point> fast = draw_step(144.0, times, k_fall_at, 1.5);

    for (int k = 1; k <= 18; ++k) {
        const double t = static_cast<double>(k) / 12.0;
        const point& a = at_time(slow, t);
        const point& b = at_time(fast, t);
        // The analytic curve: a rise toward 1 with tau = attack, then from where it stood at the fall, a decay with
        // tau = decay.
        const double risen_at_fall = 1.0 - std::exp(-k_fall_at * 1000.0 / times.attack_ms);
        const double expected = t <= k_fall_at ? 1.0 - std::exp(-t * 1000.0 / times.attack_ms)
                                               : risen_at_fall * std::exp(-(t - k_fall_at) * 1000.0 / times.decay_ms);
        INFO("t = " << t << " s: 60 Hz " << a.spectrum0 << ", 144 Hz " << b.spectrum0 << ", analytic " << expected);
        CHECK(std::fabs(a.spectrum0 - b.spectrum0) < 2e-5f);
        CHECK(std::fabs(a.band0 - b.band0) < 2e-5f);
        CHECK(std::fabs(a.rms - b.rms) < 2e-5f);
        CHECK(std::fabs(static_cast<double>(a.spectrum0) - expected) < 2e-5);
    }
    // And it is a curve: half way up the rise is neither the start nor the end, which is what would fail if the
    // step were ignored (0) or passed straight through (1).
    const float mid_rise = at_time(fast, 1.0 / 12.0).spectrum0;
    CHECK(mid_rise > 0.2f);
    CHECK(mid_rise < 0.95f);
}

TEST_CASE("attack and decay are independent", "[render][smoothing]") {
    constexpr double k_fall_at = 0.25;
    const std::vector<point> base = draw_step(144.0, {40.0f, 300.0f}, k_fall_at, 1.0);
    const std::vector<point> other_decay = draw_step(144.0, {40.0f, 1200.0f}, k_fall_at, 1.0);
    const std::vector<point> other_attack = draw_step(144.0, {10.0f, 300.0f}, k_fall_at, 1.0);

    SECTION("changing the decay leaves every rising frame exactly as it was") {
        size_t rising = 0;
        for (size_t i = 0; i < base.size() && base[i].t <= k_fall_at + 1e-9; ++i) {
            CHECK(base[i].spectrum0 == other_decay[i].spectrum0);
            ++rising;
        }
        CHECK(rising >= 30);
        // and the fall does move, so the comparison above is between two settings that do differ
        CHECK(at_time(base, 0.5).spectrum0 < at_time(other_decay, 0.5).spectrum0);
    }
    SECTION("changing the attack leaves the fall's own time constant alone") {
        CHECK(at_time(other_attack, 1.0 / 24.0).spectrum0 > at_time(base, 1.0 / 24.0).spectrum0); // 6 frames at 144 Hz
        // After the fall, each curve is (its value at the fall) * exp(-dt / decay): the ratio between two instants
        // of the fall is the decay's alone, whatever the rise left behind.
        const double ratio_base = at_time(base, 0.75).spectrum0 / at_time(base, 0.5).spectrum0;
        const double ratio_other = at_time(other_attack, 0.75).spectrum0 / at_time(other_attack, 0.5).spectrum0;
        CHECK(std::fabs(ratio_base - ratio_other) < 1e-4);
        CHECK(std::fabs(ratio_base - std::exp(-250.0 / 300.0)) < 1e-4);
    }
    SECTION("a zero attack follows a rise at once and still eases the fall") {
        const std::vector<point> instant = draw_step(144.0, {0.0f, 300.0f}, k_fall_at, 1.0);
        CHECK(instant.front().spectrum0 == 1.0f);
        CHECK(at_time(instant, 0.5).spectrum0 > 0.0f);
        CHECK(at_time(instant, 0.5).spectrum0 < 1.0f);
    }
}

TEST_CASE("only the magnitudes are eased", "[render][smoothing]") {
    temporal_envelope envelope;
    auto out = std::make_unique<mp_analysis_frame>();
    const envelope_times times{100.0f, 100.0f};
    envelope.apply(*frame_at(0.0f), times, 0.0, *out);
    const auto loud = frame_at(1.0f);
    loud->sequence = 9;
    envelope.apply(*loud, times, 1.0 / 60.0, *out);

    CHECK(out->spectrum[100] > 0.0f);
    CHECK(out->spectrum[100] < 1.0f);
    CHECK(out->bands[3] < 1.0f);
    CHECK(out->rms < 1.0f);
    CHECK(out->peak < 1.0f);
    // Copied as the analysis measured them.
    CHECK(std::memcmp(out->waveform, loud->waveform, sizeof out->waveform) == 0);
    CHECK(out->spectral_centroid_hz == loud->spectral_centroid_hz);
    CHECK(out->harmonic_ratio == loud->harmonic_ratio);
    CHECK(out->onset == loud->onset);
    CHECK(out->sequence == 9u);
}

TEST_CASE("silence after full scale decays to exactly zero with no NaN and no denormal", "[render][smoothing]") {
    temporal_envelope envelope;
    auto out = std::make_unique<mp_analysis_frame>();
    // The slowest decay the ABI allows, at the fastest refresh here, so the tail is as long and as finely stepped as
    // it can be: this is the case that walks down through the small floats if anything does.
    const envelope_times times{0.0f, mp::render::k_max_decay_ms};
    const auto quiet = frame_at(0.0f);
    envelope.apply(*frame_at(1.0f), times, 0.0, *out);

    constexpr double k_dt = 1.0 / 144.0;
    size_t bad = 0;
    long settled_at = -1;
    for (long n = 0; n < 144L * 120; ++n) { // two simulated minutes; bounded, and no wall clock
        envelope.apply(*quiet, times, k_dt, *out);
        bool all_zero = true;
        for (float v : out->spectrum) {
            bad += finite_and_normal(v) ? 0 : 1;
            all_zero = all_zero && v == 0.0f;
        }
        for (float v : out->bands) {
            bad += finite_and_normal(v) ? 0 : 1;
        }
        bad += finite_and_normal(out->rms) ? 0 : 1;
        bad += finite_and_normal(out->peak) ? 0 : 1;
        if (all_zero && settled_at < 0) {
            settled_at = n;
        }
    }
    INFO("settled to exact zero after " << settled_at << " frames at 144 Hz");
    CHECK(bad == 0);
    CHECK(settled_at > 0);
    CHECK(out->rms == 0.0f);

    SECTION("a NaN in the input does not become a state that outlives its frame") {
        const auto poisoned = frame_at(0.5f);
        poisoned->spectrum[7] = std::numeric_limits<float>::quiet_NaN();
        poisoned->rms = std::numeric_limits<float>::infinity();
        envelope.apply(*poisoned, times, k_dt, *out);
        envelope.apply(*frame_at(0.5f), times, k_dt, *out);
        envelope.apply(*frame_at(0.5f), times, k_dt, *out);
        CHECK(std::isfinite(out->spectrum[7]));
        CHECK(std::isfinite(out->rms));
    }
}

TEST_CASE("the envelope's step is the time given, including none and a very long one", "[render][smoothing]") {
    temporal_envelope envelope;
    auto out = std::make_unique<mp_analysis_frame>();
    const envelope_times times{50.0f, 50.0f};
    envelope.apply(*frame_at(0.0f), times, 0.0, *out);

    CHECK_FALSE(envelope.apply(*frame_at(1.0f), times, 0.0, *out)); // no time has passed: nothing moved
    CHECK(out->spectrum[0] == 0.0f);
    CHECK_FALSE(envelope.apply(*frame_at(1.0f), times, -1.0, *out)); // a clock that ran backwards is no time either
    CHECK(envelope.apply(*frame_at(1.0f), times, 3600.0, *out));
    CHECK(out->spectrum[0] == 1.0f); // arrived exactly, rather than asymptotically close
}

TEST_CASE("a discontinuity in the analysis restarts the envelope at its input", "[render][smoothing]") {
    temporal_envelope envelope;
    auto out = std::make_unique<mp_analysis_frame>();
    const envelope_times times{500.0f, 500.0f};
    envelope.apply(*frame_at(0.0f), times, 0.0, *out);
    envelope.apply(*frame_at(1.0f), times, 1.0 / 60.0, *out);
    REQUIRE(out->spectrum[0] < 0.1f);

    const auto after_gap = frame_at(1.0f);
    after_gap->discontinuities = 1; // mpcore.h: anything carried across frames starts again when this moves
    envelope.apply(*after_gap, times, 1.0 / 60.0, *out);
    CHECK(out->spectrum[0] == 1.0f);

    SECTION("and so does a reset, which is what switching smoothing off does") {
        const auto quiet = frame_at(0.0f);
        quiet->discontinuities = 1; // the same count, so this frame eases rather than restarting
        envelope.apply(*quiet, times, 1.0 / 60.0, *out);
        REQUIRE(out->spectrum[0] > 0.9f);
        envelope.reset();
        envelope.apply(*quiet, times, 1.0 / 60.0, *out);
        CHECK(out->spectrum[0] == 0.0f);
    }
}

// ADR-012 (D-13) is "a visual reflects an audible transient within one display refresh at p95". The envelope adds
// to that on top of whatever av-sync measures, and the addition is a property of the curve and the frame interval
// rather than of the machine, so it is computed here from the envelope itself rather than timed. The figure is when
// the drawn value first reaches half the height of a full-scale step - the first frame at or past attack * ln 2 -
// counted from the frame the step arrived on. These numbers are what T-184's card records.
TEST_CASE("the delay the envelope adds to a transient, at the settings the page offers", "[render][smoothing][latency]") {
    struct setting {
        const char* name;
        envelope_times times;
    };
    // Default and fastest as Settings > Visualization offers them (TemporalSmoothingStore): rise 20 ms by default and
    // 0 at the left of its slider. The decay does not delay a transient's arrival, only its departure.
    constexpr setting k_settings[] = {
        {"off", {0.0f, 0.0f}},
        {"fastest (rise 0 ms)", {0.0f, 300.0f}},
        {"default (rise 20 ms)", {20.0f, 300.0f}},
        {"slowest rise (250 ms)", {250.0f, 300.0f}},
    };
    for (const double hz : {60.0, 144.0}) {
        for (const setting& s : k_settings) {
            temporal_envelope envelope;
            auto out = std::make_unique<mp_analysis_frame>();
            envelope.apply(*frame_at(0.0f), s.times, 0.0, *out);
            const auto loud = frame_at(1.0f);
            const double dt = 1.0 / hz;
            long frames = 0;
            for (long n = 1; n <= 1000; ++n) { // bounded: 1000 frames is seven seconds at 144 Hz
                envelope.apply(*loud, s.times, dt, *out);
                if (out->spectrum[0] >= 0.5f) {
                    frames = n;
                    break;
                }
            }
            REQUIRE(frames > 0);
            // The step is drawn on its first frame when nothing delays it, so the delay is the frames AFTER that one.
            const double added_ms = static_cast<double>(frames - 1) * dt * 1000.0;
            char note[200];
            std::snprintf(note, sizeof note,
                          "%.0f Hz, %s: half height on frame %ld after the step, %.1f ms added (one refresh is %.1f ms)",
                          hz, s.name, frames, added_ms, dt * 1000.0);
            WARN(note);
            if (s.times.attack_ms == 0.0f) {
                CHECK(frames == 1); // off and the fastest rise add nothing at all
            }
            if (s.times.attack_ms == 20.0f) {
                CHECK(added_ms <= dt * 1000.0 + 1e-9); // the default costs at most one refresh
            }
        }
    }
}

// ---- the export ------------------------------------------------------------------------------------

namespace {

struct headless {
    mp_renderer* handle = nullptr;
    headless() {
        mp_renderer_config cfg{};
        cfg.struct_size = sizeof cfg;
        cfg.width = 32;
        cfg.height = 32;
        cfg.scale_x = 1.0f;
        cfg.scale_y = 1.0f;
        cfg.force_warp = 1;
        cfg.vsync = 0;
        cfg.headless = 1;
        REQUIRE(mp_renderer_create(nullptr, nullptr, &cfg, &handle) == MP_OK);
    }
    ~headless() {
        if (handle != nullptr) {
            mp_renderer_destroy(handle);
        }
    }
    headless(const headless&) = delete;
    headless& operator=(const headless&) = delete;
};

} // namespace

TEST_CASE("mp_renderer_set_temporal_smoothing stores, clamps and refuses", "[render][smoothing][abi]") {
    const headless fx;
    float attack = -1.0f;
    float decay = -1.0f;

    REQUIRE(mp_renderer_get_temporal_smoothing(fx.handle, &attack, &decay) == MP_OK);
    CHECK(attack == 0.0f); // off until a caller turns it on
    CHECK(decay == 0.0f);

    REQUIRE(mp_renderer_set_temporal_smoothing(fx.handle, 20.0f, 300.0f) == MP_OK);
    REQUIRE(mp_renderer_get_temporal_smoothing(fx.handle, &attack, &decay) == MP_OK);
    CHECK(attack == 20.0f);
    CHECK(decay == 300.0f);

    REQUIRE(mp_renderer_set_temporal_smoothing(fx.handle, 1e9f, 1e9f) == MP_OK);
    REQUIRE(mp_renderer_get_temporal_smoothing(fx.handle, &attack, &decay) == MP_OK);
    CHECK(attack == mp::render::k_max_attack_ms);
    CHECK(decay == mp::render::k_max_decay_ms);

    REQUIRE(mp_renderer_set_temporal_smoothing(fx.handle, 20.0f, 300.0f) == MP_OK);
    CHECK(mp_renderer_set_temporal_smoothing(fx.handle, -1.0f, 300.0f) == MP_E_INVALID_ARG);
    CHECK(mp_renderer_set_temporal_smoothing(fx.handle, 20.0f, std::numeric_limits<float>::quiet_NaN()) ==
          MP_E_INVALID_ARG);
    CHECK(mp_renderer_set_temporal_smoothing(fx.handle, std::numeric_limits<float>::infinity(), 1.0f) ==
          MP_E_INVALID_ARG);
    REQUIRE(mp_renderer_get_temporal_smoothing(fx.handle, &attack, &decay) == MP_OK);
    CHECK(attack == 20.0f); // a refused call changed nothing
    CHECK(decay == 300.0f);

    REQUIRE(mp_renderer_set_temporal_smoothing(fx.handle, 0.0f, 0.0f) == MP_OK);
    REQUIRE(mp_renderer_get_temporal_smoothing(fx.handle, &attack, &decay) == MP_OK);
    CHECK(attack == 0.0f);
    CHECK(decay == 0.0f);

    CHECK(mp_renderer_set_temporal_smoothing(nullptr, 1.0f, 1.0f) == MP_E_INVALID_ARG);
    CHECK(mp_renderer_get_temporal_smoothing(nullptr, &attack, &decay) == MP_E_INVALID_ARG);
    CHECK(mp_renderer_get_temporal_smoothing(fx.handle, nullptr, &decay) == MP_E_INVALID_ARG);
    CHECK(mp_renderer_get_temporal_smoothing(fx.handle, &attack, nullptr) == MP_E_INVALID_ARG);
}
