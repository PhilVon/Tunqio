// The adaptive quality controller. The reasoning is in quality.h; this is the arithmetic.
#include "render/quality.h"

#include <algorithm>
#include <cmath>

namespace mp::render {
namespace {

// The gain estimate is a ratio of two costs and both are measurements, so it is clamped to a range a ratio of
// two frame costs can plausibly be. Below 1 means going up made the frame cheaper, which is noise; above 8 is
// more than the area ratio of the widest step and is also noise.
constexpr double k_min_gain = 1.0;
constexpr double k_max_gain = 8.0;
// How much a regret inflates the gain estimate. The estimate was wrong in a known direction - it predicted a
// cost the tier above did not honour - so it is pushed the other way rather than merely re-measured.
constexpr double k_regret_inflation = 1.5;
// A cost sample has to be a finite positive number of milliseconds to be evidence of anything.
bool usable(double cost_ms) noexcept {
    return std::isfinite(cost_ms) && cost_ms > 0.0;
}

} // namespace

double quality_controller::area_gain(size_t step) noexcept {
    const float from = render_scale_for(step == 0 ? quality_tier::low : quality_tier::medium);
    const float to = render_scale_for(step == 0 ? quality_tier::medium : quality_tier::high);
    const double ratio = static_cast<double>(to) / static_cast<double>(from);
    return ratio * ratio; // 2.25 for low->medium, 1.7778 for medium->high
}

quality_controller::quality_controller(const quality_tuning& tuning) noexcept : tuning_(tuning) {
    reset(0.0);
}

void quality_controller::set_tuning(const quality_tuning& tuning) noexcept {
    tuning_ = tuning;
    // The dwells are per-step state derived from the tuning, so a new tuning starts them again. The tier is
    // not touched: the caller changed the numbers, not the picture.
    for (size_t i = 0; i < raise_dwell_.size(); ++i) {
        raise_dwell_[i] = tuning_.raise_dwell_s;
    }
}

void quality_controller::set_policy(mp_quality_policy policy) noexcept {
    policy_ = policy;
    if (policy == MP_QUALITY_AUTO) {
        // Whatever the pin left drawing is what the next cost sample will be about, so the controller carries
        // on from that tier. What it must not carry is a smoothed cost measured at a different one.
        have_smoothed_ = false;
        over_since_s_ = -1.0;
        under_since_s_ = -1.0;
        raised_at_s_ = {-1.0, -1.0};
        pending_gain_ = false;
        return;
    }
    const auto pinned = static_cast<quality_tier>(static_cast<uint8_t>(policy));
    if (pinned != tier_) {
        tier_ = pinned;
        // Deliberately NOT counted in changes_. That counter is what the overlay shows and what AC-129 is
        // measured on, and it is about the controller changing its mind: a person choosing a tier already
        // knows they did it, and counting their choices would bury the signal it exists to carry.
        have_smoothed_ = false;
        pending_gain_ = false;
        raised_at_s_ = {-1.0, -1.0};
    }
    over_since_s_ = -1.0;
    under_since_s_ = -1.0;
}

void quality_controller::reset(double now_s) noexcept {
    have_smoothed_ = false;
    smoothed_ms_ = 0.0;
    last_sample_s_ = now_s;
    over_since_s_ = -1.0;
    under_since_s_ = -1.0;
    changed_at_s_ = now_s;
    cost_before_change_ms_ = 0.0;
    pending_gain_ = false;
    raised_at_s_ = {-1.0, -1.0};
    // changes_ is deliberately NOT cleared: it counts tier changes over the renderer's life, and a preset
    // switch that zeroed it would hide the thrashing it may itself have started.
    for (size_t i = 0; i < gain_.size(); ++i) {
        gain_[i] = area_gain(i);
        raise_dwell_[i] = tuning_.raise_dwell_s;
    }
    if (policy_ != MP_QUALITY_AUTO) {
        tier_ = static_cast<quality_tier>(static_cast<uint8_t>(policy_));
    }
}

double quality_controller::gain_above(quality_tier lower) const noexcept {
    return lower == quality_tier::high ? 1.0 : gain_[step_above(lower)];
}

double quality_controller::raise_dwell_from(quality_tier from) const noexcept {
    return from == quality_tier::high ? 0.0 : raise_dwell_[step_above(from)];
}

void quality_controller::change_to(quality_tier next, double now_s) noexcept {
    const bool upward = static_cast<uint8_t>(next) > static_cast<uint8_t>(tier_);
    // The boundary being crossed, whichever way it is crossed: 0 is low/medium, 1 is medium/high.
    const size_t step = step_above(upward ? tier_ : next);

    // A raise that has to be undone inside the regret window is the oscillation this controller exists to
    // bound. The estimate that allowed it was wrong in a known direction, so it is pushed that way and the
    // wait before the next raise across this boundary is doubled. Doubling is what makes the number of
    // changes in a window logarithmic in the window instead of linear in it.
    if (!upward && tuning_.escalate_raise_dwell && raised_at_s_[step] >= 0.0 &&
        now_s - raised_at_s_[step] <= tuning_.regret_window_s) {
        gain_[step] = std::clamp(gain_[step] * k_regret_inflation, k_min_gain, k_max_gain);
        raise_dwell_[step] = std::min(raise_dwell_[step] * 2.0, tuning_.max_raise_dwell_s);
    }
    raised_at_s_[step] = upward ? now_s : -1.0;

    // What the cost was at the tier being left, so the settled cost at the new one can be divided by it.
    pending_gain_ = have_smoothed_ && smoothed_ms_ > 0.0;
    pending_gain_step_ = step;
    pending_gain_upward_ = upward;
    cost_before_change_ms_ = smoothed_ms_;

    tier_ = next;
    ++changes_;
    changed_at_s_ = now_s;
    have_smoothed_ = false; // the next sample is about a different system; it becomes the EMA outright
    over_since_s_ = -1.0;
    under_since_s_ = -1.0;
}

bool quality_controller::observe(double now_s, double cost_ms) noexcept {
    if (!usable(cost_ms)) {
        return false;
    }
    if (policy_ != MP_QUALITY_AUTO) {
        // Pinned. The cost is still smoothed, so the overlay has a number and so switching back to AUTO does
        // not start blind, but nothing decides anything.
        const double dt = std::max(0.0, now_s - last_sample_s_);
        last_sample_s_ = now_s;
        if (!have_smoothed_) {
            smoothed_ms_ = cost_ms;
            have_smoothed_ = true;
        } else {
            const double alpha = 1.0 - std::exp(-dt / std::max(1e-6, tuning_.smoothing_tau_s));
            smoothed_ms_ += alpha * (cost_ms - smoothed_ms_);
        }
        return false;
    }

    // Inside the settle window the frame being measured may still be the old tier's, so it is evidence of
    // nothing. The clock still runs, which is what makes the dwells wall-clock rather than frame counts.
    if (now_s - changed_at_s_ < tuning_.settle_s) {
        last_sample_s_ = now_s;
        return false;
    }

    if (!have_smoothed_) {
        // The first sample after a change becomes the EMA rather than being blended into the previous tier's
        // readings, because those readings are about a system that no longer exists.
        smoothed_ms_ = cost_ms;
        have_smoothed_ = true;
        last_sample_s_ = now_s;
        // ...and it is also the settled cost the gain estimate was waiting for.
        if (pending_gain_ && cost_before_change_ms_ > 0.0) {
            const size_t step = pending_gain_step_;
            const double ratio =
                pending_gain_upward_ ? smoothed_ms_ / cost_before_change_ms_ : cost_before_change_ms_ / smoothed_ms_;
            gain_[step] = std::clamp(ratio, k_min_gain, k_max_gain);
            pending_gain_ = false;
        }
        over_since_s_ = -1.0;
        under_since_s_ = -1.0;
        return false;
    }

    const double dt = std::max(0.0, now_s - last_sample_s_);
    last_sample_s_ = now_s;
    // alpha = 1 - exp(-dt / tau), so ten samples of 10 ms move the average as far as one of 100 ms and the
    // time constant means the same thing on a preset drawing 150 frames a second and one drawing 2300.
    const double alpha = 1.0 - std::exp(-dt / std::max(1e-6, tuning_.smoothing_tau_s));
    smoothed_ms_ += alpha * (cost_ms - smoothed_ms_);

    const double budget = tuning_.budget_ms;

    // ---- drop ------------------------------------------------------------------------------------
    if (smoothed_ms_ > budget) {
        under_since_s_ = -1.0;
        if (over_since_s_ < 0.0) {
            over_since_s_ = now_s;
        }
        if (tier_ != quality_tier::low && now_s - over_since_s_ >= tuning_.drop_dwell_s) {
            change_to(tier_below(tier_), now_s);
            return true;
        }
        return false;
    }
    over_since_s_ = -1.0;

    // ---- raise -----------------------------------------------------------------------------------
    if (tier_ == quality_tier::high) {
        under_since_s_ = -1.0;
        return false;
    }
    const size_t step = step_above(tier_);
    // The rule with the teeth: not "is the cost I am seeing comfortable", which is a question about the tier
    // that is already drawing, but "would the cost at the tier above be comfortable", which is the question
    // whose answer decides whether the raise has to be undone. See quality.h.
    const double predicted = tuning_.use_gain_projection ? smoothed_ms_ * gain_[step] : smoothed_ms_;
    if (predicted > budget * tuning_.raise_margin) {
        under_since_s_ = -1.0;
        return false;
    }
    if (under_since_s_ < 0.0) {
        under_since_s_ = now_s;
    }
    const double dwell = tuning_.escalate_raise_dwell ? raise_dwell_[step] : tuning_.raise_dwell_s;
    if (now_s - under_since_s_ >= dwell) {
        change_to(tier_above(tier_), now_s);
        return true;
    }
    return false;
}

} // namespace mp::render
