// The adaptive quality controller (E4-S7, AC-128 and AC-129), driven by a synthetic cost series.
//
// Why synthetic. AC-129's "without oscillating" is a property of a controller over time, and the only way to
// measure a property over time on a real rasteriser is to wait for it on a machine somebody else is also
// using - which is precisely how T-150 went red with nothing wrong. So the controller is a pure object taking
// a clock reading and a cost, and this file hands it a cost series it chose. The end-to-end proof that a real
// slow rasteriser reaches the same conclusion is in test_renderer.cpp, on WARP, with the real render thread.
//
// What the series is a model of. T-148 measured the four shipped presets on WARP: ambient-glow 150 fps against
// 1331-2596 for the other three, because it is the only one whose cost is per-pixel rather than per-primitive.
// So the model here has both terms - a part that scales with the rendered area and a part that does not - and
// the two extremes of it are the two kinds of preset this renderer actually has. Every interesting difference
// in this file is a difference between those two extremes.
#include "machine_lock.h"
#include "render/quality.h"
#include "render/renderer.h"
#include "source_root.h"

#include <algorithm>
#include <catch2/catch_amalgamated.hpp>
#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <limits>
#include <string>
#include <thread>

#include <windows.h>

using mp::render::quality_controller;
using mp::render::quality_tier;
using mp::render::quality_tuning;
using mp::render::render_scale_for;

namespace {

const char* tier_name(quality_tier t) {
    switch (t) {
    case quality_tier::low:
        return "Low";
    case quality_tier::medium:
        return "Medium";
    default:
        return "High";
    }
}

// What a frame costs at a tier. `per_pixel_ms` is the part that scales with the rendered area - ambient-glow's
// three lobes per octave-band group, computed once per pixel - and `per_primitive_ms` the part that does not,
// which is what the other three presets are almost entirely made of.
struct cost_model {
    double per_pixel_ms = 0.0;
    double per_primitive_ms = 0.0;

    double at(quality_tier tier) const {
        const double s = static_cast<double>(render_scale_for(tier));
        return per_pixel_ms * s * s + per_primitive_ms;
    }
};

struct drive_result {
    double elapsed_s = 0.0;
    uint32_t changes_at_end = 0;
    quality_tier tier_at_end = quality_tier::high;
    // When each tier was first reached during this stretch, or -1.
    double reached_low_s = -1.0;
    double reached_high_s = -1.0;
};

// Runs the controller for `seconds`, drawing frames as fast as they cost (which is what the headless render
// path does) unless `fixed_dt_s` says otherwise. `now_s` is carried in so a test can drive one controller
// through several stretches with a changing cost model and keep one clock.
drive_result drive(quality_controller& q, const cost_model& model, double& now_s, double seconds,
                   double fixed_dt_s = 0.0) {
    drive_result out;
    const double start = now_s;
    const uint32_t changes_at_start = q.changes();
    while (now_s - start < seconds) {
        const double cost = model.at(q.tier());
        const double dt = fixed_dt_s > 0.0 ? fixed_dt_s : std::max(0.001, cost / 1000.0);
        now_s += dt;
        q.observe(now_s, cost);
        if (out.reached_low_s < 0.0 && q.tier() == quality_tier::low) {
            out.reached_low_s = now_s - start;
        }
        if (out.reached_high_s < 0.0 && q.tier() == quality_tier::high && q.changes() > changes_at_start) {
            out.reached_high_s = now_s - start;
        }
    }
    out.elapsed_s = now_s - start;
    out.changes_at_end = q.changes();
    out.tier_at_end = q.tier();
    return out;
}

void note(const std::string& text) {
    WARN(text);
}

std::string fmt(const char* format, ...) {
    char buffer[512];
    va_list args;
    va_start(args, format);
    std::vsnprintf(buffer, sizeof buffer, format, args);
    va_end(args);
    return buffer;
}

// The number of raises from ONE tier that the escalating dwell can physically allow inside `window_s`: the
// k-th needs `base * 2^(k-1)` seconds of quiet, capped at `cap`, and they cannot overlap. This is the whole
// anti-oscillation guarantee written as arithmetic, and it is what the adversarial test asserts against -
// rather than against a number read off a run, which would be a fit and not a property.
int raises_possible(double window_s, double base_s, double cap_s) {
    int k = 0;
    double spent = 0.0;
    double dwell = base_s;
    while (spent + dwell <= window_s) {
        spent += dwell;
        dwell = std::min(dwell * 2.0, cap_s);
        ++k;
    }
    return k;
}

} // namespace

// ---- AC-128 -------------------------------------------------------------------------------------

TEST_CASE("a renderer that cannot meet its budget reaches Low well inside four seconds", "[render][quality]") {
    // The ambient-glow shape: all of the cost is pixels. 40 ms at full scale is 25 fps against a 16.667 ms
    // budget - the kind of number a software rasteriser at 1080p produces on a machine that is busy.
    const cost_model glow{40.0, 0.0};
    quality_controller q;
    double now = 0.0;
    const drive_result run = drive(q, glow, now, 6.0);

    note(fmt("AC-128: 40.0 ms at High (25 fps against a 16.667 ms budget) reached Low after %.3f s, in %u tier "
             "changes; the frame then costs %.2f ms",
             run.reached_low_s, run.changes_at_end, glow.at(quality_tier::low)));
    REQUIRE(run.reached_low_s >= 0.0);
    CHECK(run.reached_low_s < 4.0);
    CHECK(run.tier_at_end == quality_tier::low);
    CHECK(run.changes_at_end == 2); // High -> Medium -> Low and no further

    // ...and the drop bought what it was for: the cost at Low is inside the budget.
    CHECK(glow.at(quality_tier::low) < q.tuning().budget_ms);
}

TEST_CASE("a renderer inside its budget never leaves High", "[render][quality]") {
    const cost_model comfortable{10.0, 1.0};
    quality_controller q;
    double now = 0.0;
    const drive_result run = drive(q, comfortable, now, 30.0);
    note(fmt("30 s at %.2f ms against a 16.667 ms budget: %u tier changes, ends at %s",
             comfortable.at(quality_tier::high), run.changes_at_end, tier_name(run.tier_at_end)));
    CHECK(run.changes_at_end == 0);
    CHECK(run.tier_at_end == quality_tier::high);
}

// ---- AC-129 -------------------------------------------------------------------------------------

TEST_CASE("restoring headroom returns to High inside ten seconds", "[render][quality]") {
    quality_controller q;
    double now = 0.0;
    drive(q, cost_model{40.0, 0.0}, now, 6.0);
    REQUIRE(q.tier() == quality_tier::low);
    const uint32_t before = q.changes();

    // Headroom comes back: the same preset on a machine that is no longer busy, or a quarter of the pixels.
    const cost_model fast{8.0, 0.0};
    const drive_result up = drive(q, fast, now, 15.0);

    note(fmt("AC-129: with the cost at full scale back to %.1f ms, High was reached after %.3f s in exactly %u "
             "further tier changes (Low -> Medium -> High)",
             fast.at(quality_tier::high), up.reached_high_s, up.changes_at_end - before));
    REQUIRE(up.reached_high_s >= 0.0);
    CHECK(up.reached_high_s < 10.0);
    CHECK(up.tier_at_end == quality_tier::high);
    CHECK(up.changes_at_end - before == 2); // no overshoot, no undoing
}

// The case the whole design exists for, and the one a fixed pair of thresholds cannot survive: a cost at High
// just over the budget and a cost at Medium comfortably under it. Every controller that decides on the cost it
// can see alternates here for ever. This one decides on the cost it PREDICTS at the tier above, and the
// prediction is the ratio it measured crossing that boundary a moment ago.
TEST_CASE("the controller does not oscillate where a threshold pair would", "[render][quality]") {
    // 18 ms at High (over the 16.667 budget), 10.125 ms at Medium (under it). Pure pixels: the ambient-glow
    // shape, which is the only one of the four that can be here at all (T-148).
    const cost_model trap{18.0, 0.0};

    struct variant {
        const char* name;
        bool projection;
        bool escalation;
    };
    const variant variants[] = {
        {"controller as shipped", true, true},
        {"gain projection only", true, false},
        {"escalating dwell only", false, true},
        {"neither: the sketch in docs/performance-optimization.md", false, false},
    };

    uint32_t shipped = 0;
    uint32_t sketch = 0;
    for (const variant& v : variants) {
        quality_tuning tuning;
        tuning.use_gain_projection = v.projection;
        tuning.escalate_raise_dwell = v.escalation;
        quality_controller q{tuning};
        double now = 0.0;
        const drive_result run = drive(q, trap, now, 120.0);
        note(fmt("120 s at 18.00 ms High / 10.13 ms Medium against a 16.667 ms budget - %s: %u tier changes, "
                 "ends at %s",
                 v.name, run.changes_at_end, tier_name(run.tier_at_end)));
        if (v.projection && v.escalation) {
            shipped = run.changes_at_end;
        }
        if (!v.projection && !v.escalation) {
            sketch = run.changes_at_end;
        }
        if (v.projection) {
            // The prediction is right here - it measured the ratio itself - so the raise never happens.
            CHECK(run.changes_at_end == 1);
            CHECK(run.tier_at_end == quality_tier::medium);
        } else if (v.escalation) {
            // With the prediction gone the controller does raise, and is wrong every time. The escalating
            // dwell is what turns "for ever" into a number, and the number is derivable: at most
            // raises_possible(120 s) raises across the one boundary in play, each matched by a drop, plus the
            // first descent.
            const quality_tuning t;
            const auto bound =
                static_cast<uint32_t>(2 * raises_possible(120.0, t.raise_dwell_s, t.max_raise_dwell_s) + 1);
            note(fmt("  ...bounded by the escalating dwell alone: %u changes against a derived bound of %u",
                     run.changes_at_end, bound));
            CHECK(run.changes_at_end <= bound);
            CHECK(run.changes_at_end > 1); // it really did keep trying, so the bound is doing the work
        }
    }

    // The half of this test that makes the other half mean something. A bound nothing has been seen to
    // violate is indistinguishable from an assertion that cannot fail, so the same series is run through the
    // controller with both guards removed - which is the design the foundation doc sketches - and it must
    // thrash. Two changes every three and a quarter seconds over two minutes.
    note(fmt("proof the bound can be violated: the same series with both guards removed changes tier %u times "
             "against the shipped controller's %u",
             sketch, shipped));
    CHECK(sketch >= 20);
    CHECK(shipped == 1);
}

// The projection is a guarantee only while the estimate holds, so there is a second bound that does not
// depend on it: a raise undone inside the regret window doubles the wait before the next one. Here is a cost
// series no predictor can be right about - the machine gets slower and faster on a period of its own - and
// the property is that the number of tier changes is logarithmic in the window rather than linear in it.
TEST_CASE("an unpredictable machine still changes tier a bounded number of times", "[render][quality]") {
    const double window = 300.0;
    const double period = 8.0;

    auto run_variant = [&](bool escalation) {
        quality_tuning tuning;
        tuning.escalate_raise_dwell = escalation;
        quality_controller q{tuning};
        double now = 0.0;
        double next_flip = period;
        bool mean = true;
        cost_model model{40.0, 0.0};
        while (now < window) {
            if (now >= next_flip) {
                mean = !mean;
                model.per_pixel_ms = mean ? 40.0 : 12.0;
                next_flip += period;
            }
            const double cost = model.at(q.tier());
            now += std::max(0.001, cost / 1000.0);
            q.observe(now, cost);
        }
        return q.changes();
    };

    const uint32_t bounded = run_variant(true);
    const uint32_t unbounded = run_variant(false);

    // The bound, derived rather than observed: from each of the two tier boundaries, the k-th raise needs
    // base * 2^(k-1) seconds of quiet (capped), and they cannot overlap; every drop must be preceded by a
    // raise except the two of the first descent.
    const quality_tuning t;
    const int per_step = raises_possible(window, t.raise_dwell_s, t.max_raise_dwell_s);
    const auto bound = static_cast<uint32_t>(2 * (2 * per_step) + 2);

    note(fmt("300 s of a machine flipping between 40.0 and 12.0 ms every 8 s: %u tier changes with the "
             "escalating dwell and %u without it. The derived bound for this window is %u (at most %d raises "
             "from each of two boundaries, each matched by a drop, plus the first descent).",
             bounded, unbounded, bound, per_step));
    CHECK(bounded <= bound);
    // And the bound is a real constraint rather than a number nothing was ever going to reach: without the
    // escalation the same series sails past it.
    CHECK(unbounded > bound);
}

// ---- what T-148 actually changed --------------------------------------------------------------

// The measurement in T-148 is a ratio between presets, and this is where it lands: two presets can be at the
// same tier showing the same frame cost and the right answer to "may I go up?" is different for each, because
// what going up costs them is different. A single raise threshold cannot express that. The gain can.
TEST_CASE("the same observed cost is a raise for one preset shape and not for the other", "[render][quality]") {
    // Both start over budget and settle at Low. The first is all pixels (ambient-glow); the second is all
    // primitives (spectrum-bars, waveform, radial-spectrum).
    quality_controller pixels;
    quality_controller primitives;
    double a = 0.0;
    double b = 0.0;
    drive(pixels, cost_model{40.0, 0.0}, a, 6.0);
    drive(primitives, cost_model{0.0, 40.0}, b, 6.0);
    REQUIRE(pixels.tier() == quality_tier::low);
    REQUIRE(primitives.tier() == quality_tier::low);

    note(fmt("gain learned across Low -> Medium: %.3f for a per-pixel preset (analytic worst case 2.250) and "
             "%.3f for a per-primitive one",
             pixels.gain_above(quality_tier::low), primitives.gain_above(quality_tier::low)));
    CHECK(pixels.gain_above(quality_tier::low) == Catch::Approx(2.25).epsilon(0.02));
    CHECK(primitives.gain_above(quality_tier::low) == Catch::Approx(1.0).epsilon(0.02));

    // Now hand both of them exactly the same observed cost at Low - 10 ms, comfortably inside the 16.667 ms
    // budget and inside the 13.33 ms a threshold pair would raise on - and let them decide.
    const drive_result up_pixels = drive(pixels, cost_model{40.0, 0.0}, a, 20.0);         // 10.0 ms at Low
    const drive_result up_primitives = drive(primitives, cost_model{0.0, 10.0}, b, 20.0); // 10.0 ms at Low
    REQUIRE(cost_model{40.0, 0.0}.at(quality_tier::low) == Catch::Approx(10.0));

    note(fmt("both showing 10.00 ms at Low, well inside the budget: the per-pixel preset stays at %s (going up "
             "would cost it %.2f ms) and the per-primitive one reaches %s (going up costs it %.2f ms)",
             tier_name(up_pixels.tier_at_end), 10.0 * pixels.gain_above(quality_tier::low),
             tier_name(up_primitives.tier_at_end), 10.0 * primitives.gain_above(quality_tier::low)));
    CHECK(up_pixels.tier_at_end == quality_tier::low);
    CHECK(up_primitives.tier_at_end == quality_tier::high);
}

// T-148's other consequence. Ambient-glow produces 150 samples a second on WARP and radial-spectrum 2287, so a
// window counted in frames is a window fifteen times longer in seconds on one of them than on the other -
// while AC-128 and AC-129 are both stated in seconds. Hence `alpha = 1 - exp(-dt / tau)` rather than a fixed
// alpha, and hence dwells in seconds rather than frames.
TEST_CASE("the reaction time is a property of seconds and not of frames", "[render][quality]") {
    const cost_model glow{40.0, 0.0};
    double slow_at = 0.0;
    double fast_at = 0.0;
    for (int which = 0; which < 2; ++which) {
        const double rate = which == 0 ? 150.0 : 2287.0; // T-148's two measured rates
        quality_controller q;
        double now = 0.0;
        const drive_result run = drive(q, glow, now, 6.0, 1.0 / rate);
        (which == 0 ? slow_at : fast_at) = run.reached_low_s;
    }
    // The reaching of Low crosses six boundaries the controller can only notice on a frame - the end of each
    // settle window, the first sample after it, and the expiry of each drop dwell, twice over - and each of
    // those rounds up to the next frame. So the two runs may differ by six frames at the slower rate and by
    // nothing else. That is the bound; it is arithmetic about the sampling and not a number read off a run.
    const double quantisation = 6.0 / 150.0;
    note(fmt("time to Low sampled at T-148's two measured frame rates: %.4f s at 150 Hz and %.4f s at 2287 Hz. "
             "A difference of %.1f ms (%.2f%%), against the %.1f ms six frames at 150 Hz allow. A dwell counted "
             "in frames instead of seconds would have differed by %.1fx.",
             slow_at, fast_at, std::abs(slow_at - fast_at) * 1000.0, std::abs(slow_at - fast_at) / fast_at * 100.0,
             quantisation * 1000.0, 2287.0 / 150.0));
    REQUIRE(slow_at > 0.0);
    REQUIRE(fast_at > 0.0);
    CHECK(std::abs(slow_at - fast_at) < quantisation);
    // ...and the same thing stated as the ratio T-148's spread would have produced: 15.2, not 1.02.
    CHECK(slow_at / fast_at < 1.05);
}

// ---- the policy ---------------------------------------------------------------------------------

TEST_CASE("a pinned policy stops the controller deciding", "[render][quality]") {
    quality_controller q;
    q.set_policy(MP_QUALITY_HIGH);
    double now = 0.0;
    const drive_result run = drive(q, cost_model{60.0, 0.0}, now, 30.0);
    note(fmt("pinned to High at 60.0 ms a frame for 30 s: tier %s, %u changes; the smoothed cost is still "
             "reported (%.2f ms) so the overlay has a number",
             tier_name(run.tier_at_end), run.changes_at_end, q.smoothed_ms()));
    CHECK(run.tier_at_end == quality_tier::high);
    CHECK(run.changes_at_end == 0);
    CHECK(q.smoothed_ms() == Catch::Approx(60.0).epsilon(0.01));

    // ...and handing it back to AUTO starts it deciding again from where the pin left it.
    q.set_policy(MP_QUALITY_AUTO);
    const drive_result after = drive(q, cost_model{60.0, 0.0}, now, 6.0);
    CHECK(after.tier_at_end == quality_tier::low);
}

TEST_CASE("pinning a tier moves the picture to it", "[render][quality]") {
    quality_controller q;
    q.set_policy(MP_QUALITY_LOW);
    CHECK(q.tier() == quality_tier::low);
    CHECK(q.render_scale() == Catch::Approx(0.5f));
    q.set_policy(MP_QUALITY_MEDIUM);
    CHECK(q.render_scale() == Catch::Approx(0.75f));
    q.set_policy(MP_QUALITY_HIGH);
    CHECK(q.render_scale() == Catch::Approx(1.0f));
}

TEST_CASE("a preset switch discards what was learned about the preset that left", "[render][quality]") {
    quality_controller q;
    double now = 0.0;
    drive(q, cost_model{40.0, 0.0}, now, 6.0);
    REQUIRE(q.gain_above(quality_tier::low) == Catch::Approx(2.25).epsilon(0.02));

    // A different preset is a different cost model, and T-148 measured the difference at 10 to 17 times.
    q.reset(now);
    CHECK(q.gain_above(quality_tier::low) == Catch::Approx(2.25).epsilon(0.001)); // back to the analytic bound
    CHECK(q.raise_dwell_from(quality_tier::low) == Catch::Approx(q.tuning().raise_dwell_s));
    // The oscillation counter is not part of the model and survives, so a switch cannot hide thrashing.
    CHECK(q.changes() == 2);
}

TEST_CASE("a cost that is not a number changes nothing", "[render][quality]") {
    quality_controller q;
    double now = 0.0;
    drive(q, cost_model{40.0, 0.0}, now, 6.0);
    const uint32_t changes = q.changes();
    const double smoothed = q.smoothed_ms();
    for (int i = 0; i < 100; ++i) {
        now += 0.01;
        CHECK_FALSE(q.observe(now, std::numeric_limits<double>::quiet_NaN()));
        CHECK_FALSE(q.observe(now, 0.0));
        CHECK_FALSE(q.observe(now, -1.0));
    }
    CHECK(q.changes() == changes);
    CHECK(q.smoothed_ms() == Catch::Approx(smoothed));
}

// ---- the same conclusions, on the real rasteriser ------------------------------------------------
//
// Everything above is the controller reasoning about a series this file invented. What follows is the whole
// renderer - WARP, the render thread, ambient-glow, D3D11 timestamp queries - reaching the same conclusions
// about its own frame times.
//
// The one thing these tests deliberately do NOT do is assert an absolute frame rate on WARP. That is what
// T-150 fixed: WARP renders on the CPU, so its frame rate measures how busy the machine is at least as much as
// it measures the renderer, and this very preset has been observed at 150 fps on an idle desktop, 51.9 on this
// one while another build ran, and 21.4 on a shared CI runner. So instead each test MEASURES what the machine
// can do at each tier and then sets a budget strictly between two of those measurements. The controller's
// input is (cost, budget) and a budget the machine cannot meet is the same evidence as a machine that got
// slower - with the difference that this one is true on any machine and at any load.

namespace {

namespace fs = std::filesystem;

// The same cast exports_render.cpp makes; the handle is the object.
mp::render::renderer* core(mp_renderer* r) {
    return reinterpret_cast<mp::render::renderer*>(r);
}

struct preset_root_override {
    explicit preset_root_override(const fs::path& root) {
        REQUIRE(SetEnvironmentVariableW(L"MPCORE_PRESET_ROOT", root.wstring().c_str()) != 0);
    }
    ~preset_root_override() { SetEnvironmentVariableW(L"MPCORE_PRESET_ROOT", nullptr); }
    preset_root_override(const preset_root_override&) = delete;
    preset_root_override& operator=(const preset_root_override&) = delete;
};

fs::path shipped_presets() {
    const auto root = mp::tests::find_shipped_presets();
    if (!root) {
        SKIP("the repository's presets/ was not found (set MPCORE_SOURCE_ROOT)");
    }
    return *root;
}

struct warp_renderer {
    mp_renderer* handle = nullptr;

    warp_renderer(uint32_t width, uint32_t height) {
        mp_renderer_config cfg{};
        cfg.struct_size = sizeof cfg;
        cfg.width = width;
        cfg.height = height;
        cfg.scale_x = 1.0f;
        cfg.scale_y = 1.0f;
        cfg.force_warp = 1;
        cfg.vsync = 0;
        cfg.headless = 1;
        const mp_result r = mp_renderer_create(nullptr, nullptr, &cfg, &handle);
        if (r != MP_OK) {
            char err[512];
            mp_last_error(err, sizeof err);
            FAIL("mp_renderer_create failed: " << err);
        }
    }
    ~warp_renderer() {
        if (handle != nullptr) {
            mp_renderer_destroy(handle);
        }
    }
    warp_renderer(const warp_renderer&) = delete;
    warp_renderer& operator=(const warp_renderer&) = delete;

    mp_render_stats stats() const {
        mp_render_stats s{};
        s.struct_size = sizeof s;
        REQUIRE(mp_renderer_get_stats(handle, &s) == MP_OK);
        return s;
    }
};

constexpr int k_pin_settle_ms = 400;  // for the new rectangle and the smoothing to take effect
constexpr int k_pin_measure_ms = 600; // the window frames are counted over

struct tier_reading {
    // What a frame at this tier actually costs: wall clock divided by the frames the renderer completed.
    double drawn_ms = 0.0;
    // What the CONTROLLER thinks it costs - stats.frame_cost_ms, the number it is deciding on. Kept apart from
    // drawn_ms on purpose; the two are not the same quantity and one of them is unreliable (see below).
    float controller_ms = 0.0f;
    uint64_t frames = 0;
    mp_render_stats stats{};
};

// Pins a tier, lets it take effect, and measures it by COUNTING FRAMES over a fixed window.
//
// T-167, and this is the heart of it. The obvious thing to read is stats.frame_cost_ms - the renderer's own
// number, already smoothed - and that is what this file did. It cannot be trusted on WARP. renderer.cpp's
// take_frame_cost times a frame with a D3D11 timestamp pair, and on WARP the two stamps routinely come back
// EQUAL for a frame the clock cannot resolve; the code floors that to 1e-4 ms (deliberately, so the controller
// is never fed a zero) and the smoothing carries it. The error that produces is a MODE, not a spread. Measured
// on an idle i7-9700K, five interleaved rounds at High/1920x1080 read 6.87, 6.83, 6.60, 6.65, 6.72 ms in one
// process and 6.83, 4.14, 9.78, 2.82, 3.25 ms in the next; Medium/1440x810 flipped between about 4.2 and about
// 7.4 within a single run while Low sat at 2.2 all the way through. No estimator repairs that - a median over
// a bimodal sample just picks whichever mode is more common on the day - and no ordering between tiers
// survives it. "cost.medium < cost.high with expansion 7.81 < 3.91" was this, and it was neither a busy
// machine nor a render-scale regression.
//
// Frames completed per unit of wall clock has none of it. It needs no query, it is what the tier costs to
// draw, it is what a viewer would experience, and its only error term is the machine being busy - which is
// one-sided, which the interleaved rounds below are built for, and which T-150 already taught this file to
// handle by comparing tiers against each other rather than asserting any absolute rate.
tier_reading measure_at(const warp_renderer& fx, mp_quality_policy pin, int settle_ms = k_pin_settle_ms,
                        int window_ms = k_pin_measure_ms) {
    REQUIRE(mp_renderer_set_quality(fx.handle, pin) == MP_OK);
    std::this_thread::sleep_for(std::chrono::milliseconds(settle_ms));
    const uint64_t before = fx.stats().frames;
    const auto start = std::chrono::steady_clock::now();
    std::this_thread::sleep_for(std::chrono::milliseconds(window_ms));
    tier_reading out;
    out.stats = fx.stats();
    const double elapsed_ms =
        std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    out.frames = out.stats.frames - before;
    // A renderer that drew nothing at all in 600 ms is not a slow tier, it is a stopped render thread.
    REQUIRE(out.frames > 0);
    out.drawn_ms = elapsed_ms / static_cast<double>(out.frames);
    out.controller_ms = out.stats.frame_cost_ms;
    return out;
}

// For the callers that only pin a tier and do not measure it.
mp_render_stats settled_at(const warp_renderer& fx, mp_quality_policy pin) {
    return measure_at(fx, pin, k_pin_settle_ms, 300).stats;
}

// How many interleaved rounds a tier is measured over, and how many of them must agree that the render scale
// orders the tiers before the ordering is treated as evidence. Five and three: a simple majority of an odd
// number of rounds, so a single bad round cannot decide it either way.
constexpr int k_rounds = 5;
constexpr int k_rounds_that_must_agree = 3;

struct tier_costs {
    // The MEDIAN round of each tier, as frames drawn per unit of wall clock (see measure_at). What the tier
    // ordering is asserted on, because it is the only one of the two that is a measurement of the render scale.
    double high = 0.0;
    double medium = 0.0;
    double low = 0.0;
    // The same three as the CONTROLLER sees them. What the budgets are built from, because a budget the
    // controller is going to compare its own reading against has to be in the controller's own units - even
    // where those units are the unreliable ones. Nothing is asserted about their ordering.
    float high_c = 0.0f;
    float medium_c = 0.0f;
    float low_c = 0.0f;
    uint32_t high_w = 0, high_h = 0, medium_w = 0, medium_h = 0, low_w = 0, low_h = 0;
    uint32_t cost_source = MP_RENDER_COST_GPU_TIMESTAMP;

    // Every round's readings, so a red run shows WHICH round was the odd one out rather than only that the
    // rounds disagreed. This is the line that found the dropped timestamps described in measure_at.
    std::vector<std::string> notes;
    std::vector<double> highs, mediums, lows;

    // Rounds in which that round ALONE saw High dearer than Medium dearer than Low.
    //
    // This is the witness, and it is deliberately the conclusion itself rather than a dispersion statistic. A
    // spread threshold has to be fitted to a machine and quietly stops meaning anything on a different one;
    // "how many independent rounds reached the same conclusion" asks the question directly, needs no constant
    // fitted to this desktop, and is exactly what makes the median trustworthy or not.
    int ordered_rounds() const {
        int n = 0;
        for (size_t i = 0; i < highs.size(); ++i) {
            n += (highs[i] > mediums[i] && mediums[i] > lows[i]) ? 1 : 0;
        }
        return n;
    }

    std::string report() const {
        std::string all;
        for (const auto& r : notes) {
            all += (all.empty() ? "" : "; ") + r;
        }
        return fmt("drawn-frame medians High %.2f ms, Medium %.2f, Low %.2f; %d of %d rounds ordered "
                   "High>Medium>Low; the controller's own smoothed readings were High %.2f, Medium %.2f, Low "
                   "%.2f [%s]",
                   high, medium, low, ordered_rounds(), static_cast<int>(highs.size()), high_c, medium_c, low_c,
                   all.c_str());
    }
};

double median_of(std::vector<double> v) {
    if (v.empty()) {
        return 0.0;
    }
    std::sort(v.begin(), v.end());
    return v[v.size() / 2];
}

// What each tier costs on this machine, INTERLEAVED and taken as the median of the rounds.
//
// Interleaved because three sequential measurements are three measurements of three different machines when
// something else on the box is starting and stopping, and something else on this box usually is.
//
// The median rather than the minimum, which is what this took until T-167. The minimum was chosen on the
// reasoning that contention can only ever make a frame slower, so the cheapest observation of a tier is the
// one closest to what it really costs. The reasoning is sound; its premise was false while the reading came
// from stats.frame_cost_ms, which has a downward mode as well as an upward tail (measure_at). Now that the
// reading is drawn frames per unit of wall clock the premise is true again - but the median costs nothing,
// resists both tails, and does not have to be re-argued the next time something else about the measurement
// turns out to be two-sided.
tier_costs measure_tiers(const warp_renderer& fx, int rounds = k_rounds, int warmup_rounds = 1) {
    tier_costs out;
    // A discarded warm-up round, and not a superstition: on a renderer this young the first pin is measured
    // through lazy driver work and a smoothing still climbing out of its initial state. Measured here, round 0
    // at High read 1.83 ms and 2.14 ms against 6.94 and 7.50 for the same tier moments later.
    for (int round = 0; round < warmup_rounds; ++round) {
        settled_at(fx, MP_QUALITY_HIGH);
        settled_at(fx, MP_QUALITY_MEDIUM);
        settled_at(fx, MP_QUALITY_LOW);
    }
    for (int round = 0; round < rounds; ++round) {
        const tier_reading h = measure_at(fx, MP_QUALITY_HIGH);
        const tier_reading m = measure_at(fx, MP_QUALITY_MEDIUM);
        const tier_reading l = measure_at(fx, MP_QUALITY_LOW);
        out.notes.push_back(fmt("r%d H %.2f@%u(%llu fr, ctrl %.2f) M %.2f@%u(%llu, %.2f) L %.2f@%u(%llu, %.2f)", round,
                                h.drawn_ms, h.stats.render_width, static_cast<unsigned long long>(h.frames),
                                h.controller_ms, m.drawn_ms, m.stats.render_width,
                                static_cast<unsigned long long>(m.frames), m.controller_ms, l.drawn_ms,
                                l.stats.render_width, static_cast<unsigned long long>(l.frames), l.controller_ms));
        out.highs.push_back(h.drawn_ms);
        out.mediums.push_back(m.drawn_ms);
        out.lows.push_back(l.drawn_ms);
        out.high_c = h.controller_ms;
        out.medium_c = m.controller_ms;
        out.low_c = l.controller_ms;
        out.high_w = h.stats.render_width;
        out.high_h = h.stats.render_height;
        out.medium_w = m.stats.render_width;
        out.medium_h = m.stats.render_height;
        out.low_w = l.stats.render_width;
        out.low_h = l.stats.render_height;
        out.cost_source = l.stats.cost_source;
    }
    out.high = median_of(out.highs);
    out.medium = median_of(out.mediums);
    out.low = median_of(out.lows);
    return out;
}

// Polls until the tier is `want`, and returns how long that took, or -1 on timeout. Polled rather than slept
// at, so the number reported is a measurement of the controller and not of the sleep.
double seconds_until_tier(const warp_renderer& fx, uint32_t want, int timeout_ms) {
    const auto start = std::chrono::steady_clock::now();
    const auto deadline = start + std::chrono::milliseconds(timeout_ms);
    while (std::chrono::steady_clock::now() < deadline) {
        if (fx.stats().quality_tier == want) {
            return std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
    }
    return -1.0;
}

const char* source_name(uint32_t source) {
    return source == MP_RENDER_COST_GPU_TIMESTAMP ? "a D3D11 timestamp pair around the draw" : "the frame interval";
}

// T-167, half one: the rounds have to agree before a tier ordering means anything. Called before any assertion
// that compares one tier's cost with another's.
//
// This is a SKIP and not a CHECK on purpose, and the distinction is the whole point of the change: a tier
// ordering that fails when the rounds agreed is a regression in the render scale and must stay red, while the
// same ordering failing when they did not is a fact about the measurement. Asserting the second is what
// produced "cost.medium < cost.high with expansion 7.81 < 3.91" - which reads as a render regression and was
// nothing of the kind.
//
// The witness is deliberately the conclusion itself rather than a dispersion threshold. A spread limit has to
// be fitted to a machine and quietly stops meaning anything on a different one; counting how many independent
// rounds reached the same ordering needs no constant fitted to this desktop.
void require_a_steady_machine(const tier_costs& cost) {
    if (cost.ordered_rounds() >= k_rounds_that_must_agree) {
        return;
    }
    SKIP("the tier measurement did not reach a stable conclusion on this machine: "
         << cost.report() << ". Only " << cost.ordered_rounds() << " of " << cost.highs.size()
         << " independent rounds saw the render scale order the tiers, and " << k_rounds_that_must_agree
         << " are required before the medians are treated as evidence. Both tails are real - a concurrent build "
            "inflates a round, and a D3D11 timestamp pair that comes back equal on WARP deflates one - so a "
            "measurement the rounds do not agree on is not asserted. Re-run with nothing else building.");
}

// T-167, half two: a tier the controller did not reach inside its timeout. Two very different things look
// identical at the call site - the controller is broken, or the budget stopped being meetable because the
// machine got slower after it was set - and `REQUIRE(took >= 0.0)` reading -1.0 says neither.
//
// So measure again, now, at the tier we were waiting for, and let the numbers say which it was.
[[noreturn]] void diagnose_unreached_tier(const warp_renderer& fx, mp_quality_policy want, const char* want_name,
                                          double budget_ms, float measured_ms, int timeout_ms) {
    const mp_render_stats before = fx.stats();
    const float now_ms = settled_at(fx, want).frame_cost_ms;
    const std::string story =
        fmt("AUTO did not reach %s inside %.1f s. The budget was %.2f ms, set from a %s measured at %.2f ms "
            "before the run; %s costs %.2f ms on this machine NOW. The controller was at tier %u with a "
            "smoothed frame cost of %.2f ms and %u changes behind it.",
            want_name, timeout_ms / 1000.0, budget_ms, want_name, measured_ms, want_name, now_ms, before.quality_tier,
            before.frame_cost_ms, before.quality_changes);

    // The budget is only reachable at all if the tier still costs less than it. If it no longer does, no tier
    // meets the budget and a controller that stayed put is behaving correctly: the premise died, not the code.
    if (static_cast<double>(now_ms) >= budget_ms) {
        SKIP(story << " No tier meets that budget any more, so the premise this case set up is gone and the "
                      "controller had nowhere correct to go. That is the machine getting slower under the test, "
                      "not a controller fault. Re-run with nothing else building.");
    }
    FAIL(story << " The tier is affordable and the controller still did not go there, which is the fault this "
                  "case exists to catch.");
}

} // namespace

TEST_CASE("the render scale is the lever T-148 says it has to be", "[render][quality][perf]") {
    const mp::tests::machine_lock gpu_turn{mp::tests::resource::gpu}; // T-167
    const preset_root_override root{shipped_presets()};
    const warp_renderer fx{1920, 1080};
    REQUIRE(mp_renderer_set_preset(fx.handle, "ambient-glow") == MP_OK);
    REQUIRE(fx.stats().warp == 1);
    std::this_thread::sleep_for(std::chrono::milliseconds(400)); // the first frames pay for lazy driver work

    const tier_costs cost = measure_tiers(fx);
    const mp_render_stats last = fx.stats();
    note("how the tiers measured: " + cost.report());

    note(fmt("ambient-glow at 1920x1080 on WARP (%s), cost per frame from frames drawn over wall clock, the "
             "median of %d interleaved rounds: High %.2f ms at %ux%u, Medium %.2f ms at %ux%u, Low %.2f ms at "
             "%ux%u. High/Low is %.2fx against the 4.00x the areas differ by; the shortfall is the part of a "
             "frame that is not pixels (clear, constant buffer, present), which the render scale cannot touch. "
             "The renderer's own cost source is %s, which is what the CONTROLLER decides on and is not what "
             "these three numbers are - see measure_at and T-169.",
             last.adapter, k_rounds, cost.high, cost.high_w, cost.high_h, cost.medium, cost.medium_w, cost.medium_h,
             cost.low, cost.low_w, cost.low_h, cost.high / (std::max)(1e-6, cost.low), source_name(cost.cost_source)));

    // The rectangle really is what the tier says.
    CHECK(cost.high_w == 1920);
    CHECK(cost.high_h == 1080);
    CHECK(cost.medium_w == 1440);
    CHECK(cost.medium_h == 810);
    CHECK(cost.low_w == 960);
    CHECK(cost.low_h == 540);
    CHECK(last.render_scale == Catch::Approx(0.5f)); // the last round left it at Low
    // The buffer is not resized: going back up must cost nothing but a source rectangle.
    CHECK(last.width == 1920);
    CHECK(last.resizes == 0);

    // And it buys what it is for. Deliberately an ordering and not a ratio: the ratio is a property of how
    // busy the machine is and of how much of a frame is not pixels, and asserting one is what T-150 deleted.
    // But a lever that did not lower the cost at all would make everything above this a simulation of a
    // mechanism that does not exist, so the ordering is asserted - once the machine has earned the right to be
    // asked (T-167). Everything above this line is checked either way, because a render rectangle is a fact
    // about the renderer and not about how busy the box is.
    require_a_steady_machine(cost);
    REQUIRE(cost.high > 0.0);
    CHECK(cost.medium < cost.high);
    CHECK(cost.low < cost.medium);
    CHECK(last.device_lost == 0);
}

// AC-128, end to end. The budget is set from what this machine just measured rather than from 16.667 ms,
// because whether WARP misses 60 fps at 1080p depends on who else is using the CPU (150 fps idle, 51.9 under a
// concurrent build, 21.4 on a shared runner) - and a test whose outcome depends on that is the test T-150
// deleted. What is real here is every frame time the controller reads.
TEST_CASE("forcing a slow rasteriser drops quality to Low inside four seconds", "[render][quality][perf]") {
    const mp::tests::machine_lock gpu_turn{mp::tests::resource::gpu}; // T-167
    const preset_root_override root{shipped_presets()};
    const warp_renderer fx{1920, 1080};
    REQUIRE(mp_renderer_set_preset(fx.handle, "ambient-glow") == MP_OK);
    std::this_thread::sleep_for(std::chrono::milliseconds(400));

    const tier_costs cost = measure_tiers(fx);
    note("how the tiers measured: " + cost.report());
    require_a_steady_machine(cost); // the budget below is built out of these numbers (T-167)
    REQUIRE(cost.low_c > 0.0f);

    // Just above the cost at Low and well below the cost at Medium, so Low is the only tier that meets it and
    // both drops are forced. Biased toward Low rather than put halfway between them, because the two errors
    // are not the same size: a budget a busy Medium creeps under leaves the controller at Medium and fails
    // this criterion, while a budget a busy Low creeps over costs nothing at all - Low is the bottom, and
    // there is nowhere for the controller to go.
    quality_tuning tuning = core(fx.handle)->quality_tuning_now();
    // cost.low_c and not cost.low: the controller is going to compare its OWN smoothed reading against this
    // budget, so the budget has to be in that reading's units even though the drawn-frame number is the better
    // measurement of what the tier costs. Mixing the two would set a budget out of one instrument and judge it
    // with another (T-167).
    tuning.budget_ms = static_cast<double>(cost.low_c) * 1.15;
    core(fx.handle)->set_quality_tuning(tuning);

    settled_at(fx, MP_QUALITY_HIGH);
    REQUIRE(mp_renderer_set_quality(fx.handle, MP_QUALITY_AUTO) == MP_OK);
    const double took = seconds_until_tier(fx, MP_QUALITY_LOW, 8000);
    if (took < 0.0) {
        diagnose_unreached_tier(fx, MP_QUALITY_LOW, "Low", tuning.budget_ms, cost.low_c, 8000);
    }
    const mp_render_stats after = fx.stats();

    note(fmt("AC-128: ambient-glow on WARP measured %.2f ms at High, %.2f at Medium and %.2f at Low; against a "
             "budget of %.2f ms - which only Low meets - AUTO reached Low in %.3f s and stopped there. Cost "
             "source: %s. Tier changes: %u.",
             cost.high, cost.medium, cost.low, tuning.budget_ms, took, source_name(after.cost_source),
             after.quality_changes));
    REQUIRE(took >= 0.0);
    CHECK(took < 4.0);
    CHECK(after.quality_tier == MP_QUALITY_LOW);
    CHECK(after.quality_policy == MP_QUALITY_AUTO);
    CHECK(after.render_width == 960);
    CHECK(after.device_lost == 0);
}

// AC-129, end to end.
TEST_CASE("restoring headroom returns to High inside ten seconds without oscillating", "[render][quality][perf]") {
    const mp::tests::machine_lock gpu_turn{mp::tests::resource::gpu}; // T-167
    const preset_root_override root{shipped_presets()};
    const warp_renderer fx{1920, 1080};
    REQUIRE(mp_renderer_set_preset(fx.handle, "ambient-glow") == MP_OK);
    std::this_thread::sleep_for(std::chrono::milliseconds(400));

    const tier_costs cost = measure_tiers(fx);
    note("how the tiers measured: " + cost.report());
    require_a_steady_machine(cost); // both budgets below are built out of these numbers (T-167)
    REQUIRE(cost.low_c > 0.0f);
    quality_tuning tuning = core(fx.handle)->quality_tuning_now();
    tuning.budget_ms = static_cast<double>(cost.low_c) * 1.15; // the controller's units, as above
    core(fx.handle)->set_quality_tuning(tuning);

    settled_at(fx, MP_QUALITY_HIGH);
    REQUIRE(mp_renderer_set_quality(fx.handle, MP_QUALITY_AUTO) == MP_OK);
    if (seconds_until_tier(fx, MP_QUALITY_LOW, 8000) < 0.0) {
        diagnose_unreached_tier(fx, MP_QUALITY_LOW, "Low", tuning.budget_ms, cost.low_c, 8000);
    }
    const uint32_t changes_at_low = fx.stats().quality_changes;

    // Headroom comes back, and both halves of "headroom" move because both halves of it are real. The surface
    // is cut to a sixteenth of the pixels - the physical half, and the one that matters on a preset whose cost
    // is pixels - and the budget goes back to half again more than what the FULL-SIZE High tier measured a
    // moment ago, which is the exact inverse of the slow machine AC-128 forced.
    //
    // Putting the budget back is what makes this a criterion rather than a coin toss. Rendering fewer pixels
    // cannot cost more than rendering more, so High after the resize is at most the cost.high measured above
    // and the budget is 1.5x that: the premise holds by monotonicity on any machine at any load, rather than
    // holding by 1.5x on an idle one and failing on a busy one.
    REQUIRE(mp_renderer_resize(fx.handle, 480, 270, 1.0f, 1.0f) == MP_OK);
    tuning.budget_ms = static_cast<double>(cost.high_c) * 1.5; // the controller's units, as above
    core(fx.handle)->set_quality_tuning(tuning);
    const double took = seconds_until_tier(fx, MP_QUALITY_HIGH, 20000);
    if (took < 0.0) {
        // cost.high is the right comparison here: the budget was set from it, and the surface has since been cut
        // to a sixteenth, so High after the resize costs at most that by monotonicity.
        diagnose_unreached_tier(fx, MP_QUALITY_HIGH, "High", tuning.budget_ms, cost.high_c, 20000);
    }
    const mp_render_stats up = fx.stats();
    CHECK(took < 10.0);
    CHECK(up.quality_tier == MP_QUALITY_HIGH);
    // Two changes and not one more: no overshoot, nothing undone. This is "without oscillating" measured on
    // the real renderer, and the synthetic tests above are what bound it over hours rather than seconds.
    CHECK(up.quality_changes - changes_at_low == 2);

    // ...and it stays there. A controller that had raised on a prediction it could not honour would come back
    // down inside the ten-second regret window.
    std::this_thread::sleep_for(std::chrono::milliseconds(3000));
    const mp_render_stats later = fx.stats();
    note(fmt("AC-129: with the surface cut from 1920x1080 to 480x270 - a sixteenth of the pixels, on the one "
             "preset whose cost is pixels - and the budget put back to %.2f ms (1.5x what the full-size High "
             "tier measured), High was reached in %.3f s in exactly %u tier changes (Low -> Medium -> High) and "
             "held it for three seconds more with no further change. The frame then costs %.2f ms at %ux%u.",
             tuning.budget_ms, took, up.quality_changes - changes_at_low, later.frame_cost_ms, later.render_width,
             later.render_height));
    CHECK(later.quality_tier == MP_QUALITY_HIGH);
    CHECK(later.quality_changes == up.quality_changes);
    CHECK(later.render_width == 480);
    CHECK(later.frame_cost_ms > 0.0f);
    CHECK(static_cast<double>(later.frame_cost_ms) < tuning.budget_ms);
    CHECK(later.device_lost == 0);
}

// ---- the ABI tail --------------------------------------------------------------------------------

TEST_CASE("a caller built against ABI 0.13 is served the prefix it understands", "[render][quality][abi]") {
    const warp_renderer fx{64, 64};
    REQUIRE(mp_renderer_set_quality(fx.handle, MP_QUALITY_LOW) == MP_OK);
    std::this_thread::sleep_for(std::chrono::milliseconds(100));

    // 0.15's mp_render_stats ended at adapter[128]; quality_policy is the first field 0.16 appended.
    constexpr uint32_t k_size_0_13 = offsetof(mp_render_stats, quality_policy);

    // A whole struct with a recognisable pattern behind the prefix, so "the bytes past struct_size are not
    // the caller's struct" is checked rather than believed.
    alignas(mp_render_stats) unsigned char buffer[sizeof(mp_render_stats)];
    std::memset(buffer, 0xAB, sizeof buffer);
    auto* old_caller = reinterpret_cast<mp_render_stats*>(buffer);
    old_caller->struct_size = k_size_0_13;
    REQUIRE(mp_renderer_get_stats(fx.handle, old_caller) == MP_OK);
    CHECK(old_caller->struct_size == k_size_0_13); // told its own size, not this build's
    CHECK(old_caller->warp == 1);
    CHECK(std::string{old_caller->adapter}.size() > 0);
    for (size_t i = k_size_0_13; i < sizeof buffer; ++i) {
        CHECK(buffer[i] == 0xAB);
    }

    // The current caller sees the tail.
    const mp_render_stats now = fx.stats();
    CHECK(now.struct_size == sizeof(mp_render_stats));
    CHECK(now.quality_policy == MP_QUALITY_LOW);
    CHECK(now.quality_tier == MP_QUALITY_LOW);
    CHECK(now.render_width == 32);
    CHECK(now.render_height == 32);

    // A struct_size this build has never heard of is still refused in both directions.
    mp_render_stats too_big{};
    too_big.struct_size = sizeof(mp_render_stats) + 8;
    CHECK(mp_renderer_get_stats(fx.handle, &too_big) == MP_E_INVALID_ARG);
}

TEST_CASE("the quality export refuses a policy that is not one", "[render][quality][abi]") {
    const warp_renderer fx{64, 64};
    CHECK(mp_renderer_set_quality(fx.handle, static_cast<mp_quality_policy>(7)) == MP_E_INVALID_ARG);
    CHECK(mp_renderer_set_quality(nullptr, MP_QUALITY_AUTO) == MP_E_INVALID_ARG);
    for (const mp_quality_policy p : {MP_QUALITY_AUTO, MP_QUALITY_LOW, MP_QUALITY_MEDIUM, MP_QUALITY_HIGH}) {
        CHECK(mp_renderer_set_quality(fx.handle, p) == MP_OK);
    }
}
