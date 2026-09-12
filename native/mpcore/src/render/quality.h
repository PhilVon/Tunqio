// The adaptive quality controller (E4-S7). A pure object: it takes a clock reading and a per-frame cost in
// milliseconds and answers with a tier. No D3D, no threads, no clock of its own - which is what lets the
// interesting properties be measured against a synthetic series instead of against a busy machine (T-150's
// lesson: a frame rate on a shared machine measures the machine).
//
// ---- what the lever is, and why it is the only one -----------------------------------------------
//
// The story asked for "render scale, update rate and preset complexity". T-148 measured the four shipped
// presets on WARP at 1080p and the answer is that only one of them can exhaust a rasteriser at all:
// ambient-glow at 150 fps (idle dev machine) against 1331-2596 for the other three, because it is the only
// one whose cost is per-pixel rather than per-primitive - one screen-covering quad computing three soft lobes
// per octave-band group in the pixel shader, because the pipeline sets no blend state and a wash has to be
// additive. So:
//
//   - RENDER SCALE is the lever, because it is the only one whose saving is proportional to pixels, and the
//     one preset that needs saving is the one whose cost is pixels. Fewer bars would save nothing on it.
//   - UPDATE RATE is not a lever here. Presenting less often does not make a frame cheaper; it makes the
//     picture judder, and it would corrupt the controller's own input on any build that reads the frame
//     interval rather than a GPU timestamp - a tier that deliberately halves the frame rate reads as a tier
//     that is failing its budget, and the controller could never climb out of it.
//   - PRESET COMPLEXITY has no representation in the preset schema and would need one (a schema 3 bump) to
//     buy nothing on the only preset that is ever in trouble.
//
// ---- what the hysteresis is, and why it is not a constant ---------------------------------------
//
// The sketch in docs/performance-optimization.md drops below 20 ms, raises above 14 ms and refuses to decide
// more than once every two seconds. A cooldown bounds how *fast* a controller oscillates and not *whether* it
// does: if the cost at High is over the drop threshold and the cost at Medium is under the raise threshold,
// that controller alternates for ever, one change every two seconds, and every one of them is a visible step
// in the picture.
//
// Whether that trap is reachable depends on the ratio between the cost at one tier and the cost at the next,
// and T-148 is the measurement that says the ratio is not a constant across presets. For a per-pixel preset
// (ambient-glow) going from 0.75 to 1.0 scale multiplies the cost by (1/0.75)^2 = 1.78; for a per-primitive
// preset (the other three) it multiplies it by about 1. A raise threshold tuned on the second kind is tuned
// on the wrong preset and walks straight into the trap on the first.
//
// So the raise rule is not a threshold on the cost being observed. It is a threshold on the cost the
// controller PREDICTS at the tier it is thinking of moving to:
//
//     raise only if  smoothed_cost * gain(tier -> tier+1)  <=  budget * raise_margin
//
// `gain` starts at the analytic worst case - the pure per-pixel ratio, which is the ambient-glow case and an
// upper bound for any preset - and is then replaced by what the controller actually measured the last time it
// crossed that boundary. A per-primitive preset teaches it 1.0 within one transition and gets a responsive
// controller; a per-pixel one teaches it 1.78 and gets a cautious one. Neither is tuned by hand.
//
// The projection is a guarantee only while the gain estimate holds. So there is a second, unconditional
// bound: a raise that has to be undone within `regret_window_s` doubles the dwell required before the next
// raise from that tier, up to a cap. That makes the number of tier changes in a window logarithmic in the
// window rather than linear in it, whatever the cost series does - which is the property AC-129's "without
// oscillating" is measured as, and it is the property `quality_tuning::use_gain_projection` and
// `escalate_raise_dwell` exist to be able to turn off, so the test can watch it fail.
#pragma once

#include "mpcore.h"

#include <array>
#include <cstdint>

namespace mp::render {

// The three tiers. The values are mp_quality_policy's, minus MP_QUALITY_AUTO: auto is a policy - "you choose" -
// and never a tier, because something is always being drawn at one of these three.
enum class quality_tier : uint8_t {
    low = static_cast<uint8_t>(MP_QUALITY_LOW),
    medium = static_cast<uint8_t>(MP_QUALITY_MEDIUM),
    high = static_cast<uint8_t>(MP_QUALITY_HIGH),
};

// The render scale each tier draws at: the back buffer stays the size of the panel and the picture is drawn
// into this fraction of it, which the compositor stretches back out (IDXGISwapChain2::SetSourceSize).
constexpr float render_scale_for(quality_tier tier) noexcept {
    switch (tier) {
    case quality_tier::low:
        return 0.5f;
    case quality_tier::medium:
        return 0.75f;
    case quality_tier::high:
    default:
        return 1.0f;
    }
}

// Every number the controller uses, in one struct, so a test can state the ones it depends on and so the two
// guards can be switched off to prove the tests that measure them can fail.
struct quality_tuning {
    // The per-frame cost the renderer is trying to stay under. One refresh at 60 Hz.
    double budget_ms = 1000.0 / 60.0;
    // Time constant of the cost EMA. In seconds rather than frames, because T-148's spread means a frame is
    // 15 times longer on ambient-glow than on radial-spectrum and a window counted in frames would be a
    // different window on each of them - while AC-128 and AC-129 are stated in seconds.
    double smoothing_tau_s = 0.25;
    // How long the smoothed cost must stay over budget before a tier is dropped, and under the projected
    // budget before one is raised. Two drops of (settle + drop_dwell) fit inside AC-128's four seconds; two
    // raises of (settle + raise_dwell) fit inside AC-129's ten.
    double drop_dwell_s = 0.75;
    double raise_dwell_s = 2.0;
    // After a tier change the cost being measured is a different system's. Samples inside this window are fed
    // to nothing; the first sample after it becomes the EMA outright rather than being blended with the old
    // tier's readings.
    double settle_s = 0.2;
    // How far inside the budget the predicted cost at the next tier up has to land. The gap between this and
    // 1.0 is what a raise has to be wrong by before it becomes a drop.
    double raise_margin = 0.8;
    // A raise undone within this many seconds is a regret: it inflates the gain estimate and doubles the dwell.
    double regret_window_s = 10.0;
    double max_raise_dwell_s = 60.0;
    // The two guards. Both on is the controller; either off is a controller this project has a test for.
    bool use_gain_projection = true;
    bool escalate_raise_dwell = true;
};

class quality_controller {
public:
    explicit quality_controller(const quality_tuning& tuning = {}) noexcept;

    void set_tuning(const quality_tuning& tuning) noexcept;
    const quality_tuning& tuning() const noexcept { return tuning_; }

    // MP_QUALITY_AUTO lets the controller decide; the other three pin the tier and stop it deciding. Setting a
    // pinned policy takes effect on the next observe(); setting AUTO starts the controller from where the pin
    // left it rather than from High, because the tier that is drawing is the one whose cost is being measured.
    void set_policy(mp_quality_policy policy) noexcept;
    mp_quality_policy policy() const noexcept { return policy_; }

    // One frame's worth of evidence. `now_s` is any monotonic clock in seconds and `cost_ms` is what that
    // frame cost. Returns true when the tier changed as a result.
    bool observe(double now_s, double cost_ms) noexcept;

    // The cost model changed under the controller for a reason that is not the tier - a preset switch, a
    // resize. Everything measured about the old model is discarded; the escalated dwells go with it, because
    // they were regrets about a preset that is no longer drawing.
    void reset(double now_s) noexcept;

    quality_tier tier() const noexcept { return tier_; }
    float render_scale() const noexcept { return render_scale_for(tier_); }
    // The smoothed cost the decisions are made on; 0 only before the first sample and after a reset(). It is
    // deliberately NOT cleared by a tier change: for the two tenths of a second a change settles the last
    // reading is stale but true, and an overlay that blinked to zero on every change would be worse.
    double smoothed_ms() const noexcept { return smoothed_ms_; }
    // Tier changes the CONTROLLER decided, since construction. A tier a caller pinned is not one of them, and
    // reset() deliberately does not clear it: it is the oscillation counter, so a person's own choices would
    // bury its signal and a preset switch that zeroed it would hide the thrashing that switch may itself have
    // started. This is the number AC-129 is measured on.
    uint32_t changes() const noexcept { return changes_; }
    // The gain estimate in force for the step from `lower` to the tier above it: what the controller believes
    // the cost would be multiplied by. Exposed because a test that could not read it could only assert that
    // the controller behaved, never that it believed the right thing.
    double gain_above(quality_tier lower) const noexcept;
    // The raise dwell currently required from `from`, after any escalation.
    double raise_dwell_from(quality_tier from) const noexcept;

private:
    // Index of the step between tier `t` and the tier above it: 0 is low->medium, 1 is medium->high.
    static constexpr size_t step_above(quality_tier t) noexcept { return t == quality_tier::low ? 0u : 1u; }
    static constexpr quality_tier tier_below(quality_tier t) noexcept {
        return t == quality_tier::high ? quality_tier::medium : quality_tier::low;
    }
    static constexpr quality_tier tier_above(quality_tier t) noexcept {
        return t == quality_tier::low ? quality_tier::medium : quality_tier::high;
    }
    // The analytic worst case for one step: the cost ratio of a shader that is entirely per-pixel, which is
    // the area ratio. No preset can beat it, so it is the safe thing to believe before anything is measured.
    static double area_gain(size_t step) noexcept;

    void change_to(quality_tier next, double now_s) noexcept;

    quality_tuning tuning_{};
    mp_quality_policy policy_ = MP_QUALITY_AUTO;
    quality_tier tier_ = quality_tier::high;

    bool have_smoothed_ = false;
    double smoothed_ms_ = 0.0;
    double last_sample_s_ = 0.0;

    // When the current run of over-budget (or of comfortably-under-budget) observations began; negative when
    // there is no run in progress.
    double over_since_s_ = -1.0;
    double under_since_s_ = -1.0;
    // When the last tier change happened, and what the smoothed cost was just before it. The second is what
    // makes the gain a measurement: the ratio of the settled cost after a change to the cost before it.
    double changed_at_s_ = -1.0;
    double cost_before_change_ms_ = 0.0;
    bool pending_gain_ = false;
    size_t pending_gain_step_ = 0;
    bool pending_gain_upward_ = false;
    // When a raise across each step last happened and has not yet been undone, so a drop back across the same
    // step inside the regret window is recognised as one. Per step rather than one "the last raise", because
    // a controller that climbed Low -> Medium -> High and then fell all the way back has had two raises
    // undone and both of them were wrong; charging only the second would leave the Low/Medium pair free to
    // thrash for ever.
    std::array<double, 2> raised_at_s_{};

    std::array<double, 2> gain_{};
    std::array<double, 2> raise_dwell_{};
    uint32_t changes_ = 0;
};

} // namespace mp::render
