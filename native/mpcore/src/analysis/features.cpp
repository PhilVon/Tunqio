#include "analysis/features.h"

#include <algorithm>
#include <cmath>
#include <cstring>

namespace mp::analysis {

features::features(uint32_t sample_rate) noexcept {
    reset(sample_rate);
}

void features::reset(uint32_t sample_rate) noexcept {
    sample_rate_ = sample_rate != 0 ? sample_rate : 48000;
    bin_hz_ = static_cast<float>(sample_rate_) / static_cast<float>(k_fft_size);

    // Band i is the octave centred on 31.5 * 2^i Hz, so its edges are that centre divided and multiplied by
    // sqrt(2). Boundaries are taken once and shared - edges_[i + 1] is both the top of band i and the bottom of
    // band i + 1 - which is what makes the ten bands a partition of the spectrum rather than ten independent
    // rounding decisions with gaps and overlaps between them.
    constexpr float k_root_two = 1.41421356f;
    float edge_hz = k_lowest_band_centre_hz / k_root_two;
    for (uint32_t i = 0; i < k_bands; ++i) {
        // Bin 0 is DC and belongs to no octave; the lowest band starts at bin 1 however low its edge falls.
        const auto bin = static_cast<int64_t>(std::lround(edge_hz / bin_hz_));
        edges_[i] = static_cast<uint32_t>(std::clamp<int64_t>(bin, 1, k_bins));
        if (i > 0) {
            edges_[i] = std::max(edges_[i], edges_[i - 1]);
        }
        edge_hz *= 2.0f;
    }
    // The top band is closed at the last bin rather than at its own 22.8 kHz edge: at 48 kHz that keeps
    // 22.8-24 kHz inside a band instead of in no band at all, and at a rate where the octaves run past Nyquist
    // the clamp above has already folded the upper bands onto k_bins, so they read zero as they should.
    edges_[k_bands] = k_bins;
    for (uint32_t i = k_bands; i > 0; --i) {
        edges_[i - 1] = std::min(edges_[i - 1], edges_[i]);
    }

    std::memset(previous_, 0, sizeof previous_);
    have_previous_ = false;
    std::memset(flux_history_, 0, sizeof flux_history_);
    history_count_ = 0;
    history_write_ = 0;
    refractory_ = 0;
    flux_ = 0.0f;
    threshold_ = 0.0f;
}

float features::median_of_history() const noexcept {
    if (history_count_ == 0) {
        return 0.0f;
    }
    // nth_element on a fixed local: introselect sorts in place and allocates nothing, and 43 floats is 172 bytes
    // of stack. The median rather than the mean because the history of a click track is mostly silence with a
    // few spikes in it, and the mean of that is a bar the next spike has to clear.
    float scratch[k_flux_history];
    std::memcpy(scratch, flux_history_, history_count_ * sizeof(float));
    const uint32_t middle = history_count_ / 2;
    std::nth_element(scratch, scratch + middle, scratch + history_count_);
    return scratch[middle];
}

void features::extract(const float* spectrum, mp_analysis_frame& out) noexcept {
    // One pass for the three sums every feature is built out of: the total magnitude (the denominator of the
    // centroid and the scale the onset threshold is a fraction of), the frequency-weighted magnitude, and the
    // sum of logs the flatness needs. Bin 0 is left out of all of them - DC is not a frequency anything is heard
    // at, and a DC offset weighted at 0 Hz would drag the centroid down towards a pitch nothing is playing.
    double magnitude_sum = 0.0;
    double weighted_sum = 0.0;
    double log_sum = 0.0;
    for (uint32_t k = 1; k < k_bins; ++k) {
        const float m = spectrum[k];
        magnitude_sum += m;
        weighted_sum += static_cast<double>(k) * m;
        log_sum += std::log(static_cast<double>(std::max(m, k_flatness_floor)));
    }
    const auto counted = static_cast<double>(k_bins - 1);

    out.spectral_centroid_hz = magnitude_sum > 0.0 ? static_cast<float>(weighted_sum / magnitude_sum * bin_hz_) : 0.0f;

    // The harmonic ratio is one minus the spectral flatness (the Wiener entropy): the geometric mean of the bin
    // magnitudes over their arithmetic mean. The name does not define itself, so this is the definition - how
    // much of the spectrum is tone rather than noise, on a scale where a single sine reads ~1.0 and white noise
    // reads ~0.15, because a flat spectrum has its two means equal and a spectrum that is three loud bins and
    // 1021 quiet ones does not. It is not a count of harmonics: an inharmonic bell and a perfect fifth both read
    // high, and that is the intent - what a visualizer wants from this field is "is there a note here".
    const double geometric = std::exp(log_sum / counted);
    const double arithmetic = magnitude_sum / counted;
    const double flatness = arithmetic > 0.0 ? std::clamp(geometric / arithmetic, 0.0, 1.0) : 1.0;
    out.harmonic_ratio = static_cast<float>(1.0 - flatness);

    // Octave bands, in quadrature and divided by the Hann window's 1.5-bin noise bandwidth: a full-scale sine
    // inside a band reads 1.0 there whatever fraction of a bin it sits at, and a band of noise reads the
    // amplitude of a sine with the same power. Both follow from the same constant, which is why one correction
    // serves a tone and a hiss alike.
    for (uint32_t band = 0; band < k_bands; ++band) {
        double energy = 0.0;
        for (uint32_t k = edges_[band]; k < edges_[band + 1]; ++k) {
            energy += static_cast<double>(spectrum[k]) * spectrum[k];
        }
        out.bands[band] = static_cast<float>(std::sqrt(energy / k_hann_enbw_bins));
    }

    // Spectral flux: how much magnitude appeared since the previous hop, rectified so that a decay is not an
    // onset. The first hop after a reset has nothing to be a difference from, and calling that an onset would
    // flag one every time a track starts loading rather than every time one starts sounding.
    double flux = 0.0;
    if (have_previous_) {
        for (uint32_t k = 1; k < k_bins; ++k) {
            const float rise = spectrum[k] - previous_[k];
            if (rise > 0.0f) {
                flux += rise;
            }
        }
    }
    flux_ = static_cast<float>(flux);
    threshold_ = static_cast<float>(k_onset_delta * magnitude_sum) + k_onset_lambda * median_of_history();

    uint8_t onset = 0;
    if (refractory_ > 0) {
        --refractory_;
    } else if (have_previous_ && magnitude_sum > k_level_gate && flux_ > threshold_) {
        onset = 1;
        refractory_ = k_onset_refractory;
    }
    out.onset = onset;

    flux_history_[history_write_] = flux_;
    history_write_ = (history_write_ + 1) % k_flux_history;
    history_count_ = std::min(history_count_ + 1, k_flux_history);

    std::memcpy(previous_, spectrum, k_bins * sizeof(float));
    have_previous_ = true;
}

} // namespace mp::analysis
