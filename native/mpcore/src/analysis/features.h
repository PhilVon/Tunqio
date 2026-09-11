// Feature extraction (E4-S2): what the analysis thread reads out of the spectrum E4-S1 produces.
//
// Four numbers per hop, all of them over the same 1024-bin magnitude spectrum and none of them owning any state
// bigger than the previous hop's spectrum: the spectral centroid, ten octave band levels, a harmonic ratio and
// an onset flag. They fill the four fields mp_analysis_frame has carried since ABI 0.8 and has been publishing
// as zero, and they are what E4-S3's preset constant buffer already reads - `level.zw` and `counts.x` and
// `bands[3]` are in the shipped contract (docs/visualization-engine.md), which is why this is a filling-in and
// not a widening.
//
// **Ten bands, not six.** docs/roadmap-and-backlog.md says six; MP_ANALYSIS_OCTAVE_BANDS says ten, the managed
// binding says ten, and the preset constant buffer ships ten. Ten is also the physically right answer: 20 Hz to
// 20 kHz is ten octaves, and six would either cover six of them or stop being octaves. The header is the
// contract and the roadmap is the description, so the roadmap line is the one that was wrong.
//
// Real-time: every buffer here is a member or a fixed local, the band edges are computed once per sample rate
// rather than per hop, and nothing allocates or locks. This runs inside analyzer::analyze's rt::scope, which
// asserts exactly that in Debug (AC-112).
#pragma once

#include "mpcore.h"

#include <cstdint>

namespace mp::analysis {

class features {
public:
    static constexpr uint32_t k_fft_size = 2048;
    static constexpr uint32_t k_bins = MP_ANALYSIS_SPECTRUM_BINS; // 1024: bins 0..1023, Nyquist dropped
    static constexpr uint32_t k_bands = MP_ANALYSIS_OCTAVE_BANDS; // 10 octaves, 31.5 Hz centre to 16 kHz centre
    static_assert(k_bins * 2 == k_fft_size, "the spectrum is the real FFT's bins below Nyquist");

    // The lowest octave's nominal centre (ISO R.40): the ten bands are 31.5 Hz to 16 kHz doubling, and each band
    // runs from centre/sqrt(2) to centre*sqrt(2). The top band is closed at the last bin rather than at
    // 22.8 kHz, so nothing in the spectrum falls outside every band.
    static constexpr float k_lowest_band_centre_hz = 31.5f;

    // A Hann window's equivalent noise bandwidth is 1.5 bins, which is the factor that makes a band level mean
    // the same thing for a tone and for noise: sum(m^2)/1.5 is the mean square of what is in the band either
    // way. With E4-S1's spectrum scale (full-scale sine = 1.0 in its own bin) the square root of that is the
    // amplitude - a full-scale sine anywhere inside a band puts 1.0 in it, wherever between two bins it sits.
    static constexpr float k_hann_enbw_bins = 1.5f;

    // Magnitudes below this are treated as this when the flatness is taken, so log(0) is not a number the
    // harmonic ratio has to survive. 1e-9 is 180 dB below a full-scale sine: under anything real, over
    // denormals, and low enough that a pure tone's float noise floor still reads as the near-zero it is.
    static constexpr float k_flatness_floor = 1e-9f;

    // Onset detection: half-wave-rectified spectral flux against an adaptive threshold.
    //   threshold(n) = k_onset_delta * spectral_sum(n) + k_onset_lambda * median(last k_flux_history fluxes)
    // The first term is what makes the threshold scale-free - it is a fraction of the spectrum the frame
    // actually has, so the same detector works at -40 dBFS as at -6 - and it is what stops a stationary signal
    // firing, because a mean-and-standard-deviation threshold over a stationary flux crosses itself every few
    // dozen frames by construction. The second term is the adaptation: the median of the recent past is the
    // quiet between the onsets and not the onsets, which a mean is not, so a train of loud events does not
    // raise the bar until the later ones miss it.
    static constexpr uint32_t k_flux_history = 43;    // ~0.46 s at 93.75 hops/s
    static constexpr uint32_t k_onset_refractory = 3; // ~32 ms, so one event is one flag and not three
    static constexpr float k_onset_delta = 0.05f;
    static constexpr float k_onset_lambda = 2.0f;
    // Below this much total magnitude there is nothing to have an onset in, and flux is float noise.
    static constexpr float k_level_gate = 1e-4f;

    explicit features(uint32_t sample_rate = 48000) noexcept;

    // Control thread. Forgets the previous hop and the flux history and recomputes the band edges: a new stream
    // must not produce an onset because its first spectrum differs from the last one of the stream before it.
    void reset(uint32_t sample_rate) noexcept;

    // Analysis thread, inside rt::scope. Reads k_bins magnitudes and fills `out.bands`,
    // `out.spectral_centroid_hz`, `out.harmonic_ratio` and `out.onset`. Touches no other field.
    void extract(const float* spectrum, mp_analysis_frame& out) noexcept;

    // ---- what the tests measure ----

    // The most recent hop's flux and the threshold it was held against.
    float flux() const noexcept { return flux_; }
    float onset_threshold() const noexcept { return threshold_; }
    // Half-open bin range of a band: [first, last). Equal when the band is narrower than one bin, which the
    // lowest octave is at any sample rate this plays at.
    uint32_t band_first_bin(uint32_t band) const noexcept { return edges_[band < k_bands ? band : k_bands]; }
    uint32_t band_last_bin(uint32_t band) const noexcept { return edges_[band < k_bands ? band + 1 : k_bands]; }
    float bin_hz() const noexcept { return bin_hz_; }

private:
    float median_of_history() const noexcept;

    uint32_t sample_rate_ = 48000;
    float bin_hz_ = 48000.0f / static_cast<float>(k_fft_size);
    uint32_t edges_[k_bands + 1]{}; // band i is bins [edges_[i], edges_[i + 1])

    float previous_[k_bins]{}; // the hop before this one, which is what flux is a difference against
    bool have_previous_ = false;

    float flux_history_[k_flux_history]{};
    uint32_t history_count_ = 0;
    uint32_t history_write_ = 0;
    uint32_t refractory_ = 0;

    float flux_ = 0.0f;
    float threshold_ = 0.0f;
};

} // namespace mp::analysis
