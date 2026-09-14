// Temporal smoothing of the analysis frame before it reaches a preset (T-184, ABI 0.19).
//
// A preset keeps nothing between frames - b0 and two SRVs are its whole surface - so an attack and decay envelope
// cannot live in a shader (T-141, Q-90). It lives here instead, on the render thread, between the frame the renderer
// chose to draw and the upload of that frame to the GPU: one envelope for every preset, set renderer-wide.
//
// What is smoothed, and why only that:
//   - spectrum, bands, rms and peak. These are magnitudes, and "rises quickly, falls slowly" means something for a
//     magnitude.
//   - NOT waveform. It is the newest 512 signed samples of a hop, and the hop before it is a different stretch of
//     audio at an unrelated phase. An envelope per sample index across hops would average unrelated signed values
//     toward zero: a waveform that flattens as the setting grows, rather than one that moves smoothly.
//   - NOT spectral_centroid_hz or harmonic_ratio. They are positions on a scale, not levels, and T-181 measured that
//     a centroid between the ends of the map paints a BRIGHTER field than either end. An envelope on the centroid
//     would sweep the colour through that middle on every change, which is a flash search's worst case produced on
//     purpose. Left as the analysis measured them, they move exactly as they do with smoothing off.
//   - NOT onset, sequence or anything else. An onset is an event, and an eased event is not one.
//
// The envelope is one-pole and exponential in TIME, not in frames:
//
//     y <- x + (y - x) * exp(-dt / tau),   tau = attack when x > y, decay otherwise
//
// For an input held between two frames that composition is exact - exp(-a) * exp(-b) == exp(-(a + b)) - so a step
// drawn at 144 Hz passes through the same values at the same instants as the same step drawn at 60 Hz, and the
// setting means the same thing on every display. What still depends on the frame rate is only which instants get
// drawn, which is what a frame rate is.
//
// Off is exact. A time constant of zero makes the factor exactly 0, and x + (y - x) * 0 is x bit for bit, so
// attack == decay == 0 draws the analysis frame unchanged - but the renderer does not even rely on that: with both
// at zero it never calls this and uploads the analysis frame itself, which is what keeps every golden image valid.
#pragma once

#include "mpcore.h"

#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>

namespace mp::render {

// The largest time constants mp_renderer_set_temporal_smoothing accepts; larger values are clamped to these. Ten
// times what a person would choose, so the clamp exists to keep a nonsense value bounded rather than to limit a
// setting (Settings > Visualization offers 0-250 ms of rise and 0-2000 ms of fall).
inline constexpr float k_max_attack_ms = 1000.0f;
inline constexpr float k_max_decay_ms = 5000.0f;

// Within this of its input a value simply takes the input. Without it a decay toward digital silence never arrives
// and walks down through the subnormal floats on the way, which cost a hundred times a normal multiply on x86 and
// are exactly the "denormals on silence" an audio path is not allowed to have. 1e-7 is -140 dB against the
// spectrum's full scale of 1.0, below anything a bar can show, and it is hundreds of orders of magnitude above the
// smallest normal float, so nothing in here is ever subnormal.
inline constexpr float k_envelope_snap = 1e-7f;

struct envelope_times {
    float attack_ms = 0.0f; // rising: 0 follows a rise at once
    float decay_ms = 0.0f;  // falling: 0 follows a fall at once

    bool off() const noexcept { return attack_ms <= 0.0f && decay_ms <= 0.0f; }
};

class temporal_envelope {
public:
    // Forgets everything, so the next apply starts from its input rather than easing toward it. Called when smoothing
    // is switched off, so that switching it on again does not ease in from a picture that is no longer on screen.
    void reset() noexcept { primed_ = false; }

    bool primed() const noexcept { return primed_; }

    // The factor one value keeps of its distance from the input over `dt_s` seconds: exp(-dt / tau), and exactly 0
    // for a time constant of zero (follow at once) or a step too long to leave anything. dt below zero is treated as
    // zero, which keeps a value where it is.
    static float keep(float tau_ms, double dt_s) noexcept {
        if (!(tau_ms > 0.0f)) {
            return 0.0f;
        }
        const double exponent = (dt_s > 0.0 ? dt_s : 0.0) * 1000.0 / static_cast<double>(tau_ms);
        return exponent > 700.0 ? 0.0f : static_cast<float>(std::exp(-exponent));
    }

    // One value one step. A non-finite state or input takes the input, so a NaN can never become a state that
    // outlives the frame it arrived on: at worst the envelope is off for that value for that frame.
    static float follow(float y, float x, float keep_rising, float keep_falling) noexcept {
        const float keep = x > y ? keep_rising : keep_falling;
        const float next = x + (y - x) * keep;
        const float distance = next - x;
        // The comparison is false for NaN as well as for a distance inside the snap, and both take the input.
        return distance < k_envelope_snap && distance > -k_envelope_snap ? x : (std::isfinite(next) ? next : x);
    }

    // Eases the smoothed fields of `in` over `dt_s` seconds and writes the whole frame to `out`, the fields it does
    // not smooth copied from `in` unchanged. The first call after a reset, and the first after the analysis reports
    // a discontinuity (mpcore.h: anything carried across frames starts again when that count moves), take the input
    // as it is. Returns true when anything `out` carries in a smoothed field differs from the previous call's, which
    // is how the renderer knows the spectrum on the GPU is already this frame's.
    bool apply(const mp_analysis_frame& in, envelope_times times, double dt_s, mp_analysis_frame& out) noexcept {
        out = in;
        if (!primed_ || in.discontinuities != discontinuities_) {
            for (size_t i = 0; i < spectrum_.size(); ++i) {
                spectrum_[i] = std::isfinite(in.spectrum[i]) ? in.spectrum[i] : 0.0f;
            }
            for (size_t i = 0; i < bands_.size(); ++i) {
                bands_[i] = std::isfinite(in.bands[i]) ? in.bands[i] : 0.0f;
            }
            rms_ = std::isfinite(in.rms) ? in.rms : 0.0f;
            peak_ = std::isfinite(in.peak) ? in.peak : 0.0f;
            discontinuities_ = in.discontinuities;
            primed_ = true;
            return true; // a new state is a change by definition
        }
        const float rising = keep(times.attack_ms, dt_s);
        const float falling = keep(times.decay_ms, dt_s);
        bool moved = false;
        const auto step = [&](float& state, float input) {
            const float next = follow(state, input, rising, falling);
            moved = moved || next != state;
            state = next;
            return next;
        };
        for (size_t i = 0; i < spectrum_.size(); ++i) {
            out.spectrum[i] = step(spectrum_[i], in.spectrum[i]);
        }
        for (size_t i = 0; i < bands_.size(); ++i) {
            out.bands[i] = step(bands_[i], in.bands[i]);
        }
        out.rms = step(rms_, in.rms);
        out.peak = step(peak_, in.peak);
        return moved;
    }

private:
    std::array<float, MP_ANALYSIS_SPECTRUM_BINS> spectrum_{};
    std::array<float, MP_ANALYSIS_OCTAVE_BANDS> bands_{};
    float rms_ = 0.0f;
    float peak_ = 0.0f;
    uint8_t discontinuities_ = 0;
    bool primed_ = false;
};

} // namespace mp::render
