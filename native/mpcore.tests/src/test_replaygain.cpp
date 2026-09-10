// E1-S5 ReplayGain tests. The gain is a property of the track, applied by the mixer per source, so the tests
// listen to the pull stage (MP_DEVICE_NONE + mp_engine_render) and measure levels: a -6 dB track is 6 dB
// quieter, clipping prevention holds the loudest sample at full scale, a preloaded track's gain lands on the
// exact frame of its gapless join, and changing the gain of the track being heard is a slide, not a step.
#include "mpcore.h"

#include "audio/bass_engine.h"
#include "offline_engine.h"
#include "wav_fixture.h"

#include <catch2/catch_amalgamated.hpp>
#include <cmath>
#include <string>
#include <vector>

using mp::tests::k_buffer_frames;
using mp::tests::k_channels;
using mp::tests::k_fade_frames;
using mp::tests::k_rate;
using mp::tests::max_step;
using mp::tests::offline_engine;
using mp::tests::rms;

namespace {

double db(double ratio) {
    return 20.0 * std::log10(ratio);
}

double peak_of(const std::vector<float>& samples, size_t from_frame) {
    double worst = 0;
    for (size_t i = from_frame * k_channels; i < samples.size(); ++i) {
        worst = std::max(worst, static_cast<double>(std::fabs(samples[i])));
    }
    return worst;
}

// Plays `path` from the start with the given ReplayGain and returns the second half of one second of output,
// after the fade-in has landed.
std::vector<float> settled_second(offline_engine& fx, const std::string& path, float gain_db, float peak) {
    mp_track* t = fx.open(path);
    REQUIRE(mp_track_set_replaygain(t, gain_db, peak) == MP_OK);
    REQUIRE(mp_engine_play(fx.engine, t, 0) == MP_OK);
    std::vector<float> audio = fx.render(k_rate);
    REQUIRE(mp_engine_stop(fx.engine, MP_FADE_NONE) == MP_OK);
    REQUIRE(mp_track_close(t) == MP_OK);
    return audio;
}

} // namespace

TEST_CASE("the resolved gain is 10^(dB/20), held to 1/peak when the peak would clip", "[replaygain]") {
    using mp::audio::engine;
    CHECK(engine::replaygain_linear(0.0f, 1.0f) == Catch::Approx(1.0f));
    CHECK(engine::replaygain_linear(-6.0f, 1.0f) == Catch::Approx(0.501187f).epsilon(1e-4));
    CHECK(engine::replaygain_linear(6.0f, 0.25f) == Catch::Approx(1.995262f).epsilon(1e-4)); // 0.5 after gain: fine
    CHECK(engine::replaygain_linear(12.0f, 0.5f) == Catch::Approx(2.0f));                    // would be 1.99: limited
    CHECK(engine::replaygain_linear(3.0f, 1.0f) == Catch::Approx(1.0f)); // full-scale source: no boost
    CHECK(engine::replaygain_linear(12.0f, 0.0f) == Catch::Approx(3.981072f).epsilon(1e-4)); // unknown peak: no limit
    CHECK(engine::replaygain_linear(12.0f, -1.0f) == Catch::Approx(3.981072f).epsilon(1e-4));
}

TEST_CASE("a track set to -6 dB plays 6 dB quieter than the same file untagged", "[replaygain]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 1.5;
    const std::string path = mp::tests::write_sine_wav(spec, "replaygain-6db");

    const std::vector<float> plain = settled_second(fx, path, 0.0f, 0.0f);
    const std::vector<float> quieter = settled_second(fx, path, -6.0f, static_cast<float>(spec.amplitude));
    const double plain_rms = rms(plain, k_rate / 2, k_rate);
    const double quieter_rms = rms(quieter, k_rate / 2, k_rate);
    CHECK(plain_rms == Catch::Approx(spec.amplitude / std::sqrt(2.0)).margin(0.002));
    CHECK(db(quieter_rms / plain_rms) == Catch::Approx(-6.0).margin(0.1)); // build-test-release.md: within 0.1 dB
    CHECK(max_step(quieter) < 0.02);                                       // still a clean sine
}

TEST_CASE("clipping prevention holds the loudest sample at full scale", "[replaygain]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 1.5;
    spec.amplitude = 0.5; // -6 dBFS peak
    const std::string path = mp::tests::write_sine_wav(spec, "replaygain-peak");

    SECTION("gain plus preamp that would exceed 0 dBFS is reduced to 1/peak") {
        const std::vector<float> audio =
            settled_second(fx, path, 12.0f, static_cast<float>(spec.amplitude)); // +12 dB would peak at 1.99
        CHECK(peak_of(audio, k_fade_frames + k_buffer_frames) <= 1.0001);
        CHECK(peak_of(audio, k_fade_frames + k_buffer_frames) > 0.99);
        CHECK(rms(audio, k_rate / 2, k_rate) == Catch::Approx(1.0 / std::sqrt(2.0)).margin(0.01));
        CHECK(max_step(audio) < 0.15); // 440 Hz at full scale steps by 0.058 per sample: no clip flattening or click
    }
    SECTION("a gain that stays under full scale is applied in full") {
        const std::vector<float> audio =
            settled_second(fx, path, 3.0f, static_cast<float>(spec.amplitude)); // peaks at 0.707
        CHECK(db(rms(audio, k_rate / 2, k_rate) / (spec.amplitude / std::sqrt(2.0))) == Catch::Approx(3.0).margin(0.1));
    }
    SECTION("an unknown peak means no limiting") {
        const std::vector<float> audio = settled_second(fx, path, 12.0f, 0.0f);
        CHECK(peak_of(audio, k_fade_frames + k_buffer_frames) > 1.5); // float output carries it; the DAC would clip
    }
}

TEST_CASE("a preloaded track's gain takes effect on the frame of the gapless join", "[replaygain][gapless]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 1.0;
    spec.frequency_hz = 10.0; // slow enough that a step at the join would stand out against the signal's own slope
    spec.amplitude = 0.5;
    mp_track* a = fx.open(mp::tests::write_sine_wav(spec, "replaygain-join-a"));
    mp_track* b = fx.open(mp::tests::write_sine_wav(spec, "replaygain-join-b"));
    REQUIRE(mp_track_set_replaygain(b, -12.0f, static_cast<float>(spec.amplitude)) == MP_OK);

    REQUIRE(mp_engine_play(fx.engine, a, 0) == MP_OK);
    REQUIRE(mp_engine_preload_next(fx.engine, b) == MP_OK);
    const std::vector<float> audio = fx.render(2 * k_rate);

    const size_t join = k_rate; // a is exactly 1.0 s
    const double before = rms(audio, join - k_rate / 4, join);
    const double after = rms(audio, join, join + k_rate / 4);
    CHECK(before == Catch::Approx(spec.amplitude / std::sqrt(2.0)).margin(0.01));
    CHECK(db(after / before) == Catch::Approx(-12.0).margin(0.15));

    // The join itself: b starts at phase 0 (sample 0) and a ended a whole number of 10 Hz cycles in, so the two
    // sides meet at zero crossing; with the gain applied from b's first frame the step across the seam is the
    // signal's own slope, not a level jump.
    const float last_a = audio[(join - 1) * k_channels];
    const float first_b = audio[join * k_channels];
    CHECK(std::fabs(first_b - last_a) < 0.002);
    // And b's first frames are already quiet: the gain did not arrive a buffer late.
    CHECK(rms(audio, join, join + k_buffer_frames) < rms(audio, join - k_buffer_frames, join) * 0.5);
}

TEST_CASE("changing the gain of the track being heard is ramped, not stepped", "[replaygain]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 2.0;
    spec.frequency_hz = 10.0;
    spec.amplitude = 0.5;
    mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "replaygain-slide"));
    REQUIRE(mp_engine_play(fx.engine, t, 0) == MP_OK);
    fx.render(k_rate / 2);

    REQUIRE(mp_track_set_replaygain(t, -6.0f, static_cast<float>(spec.amplitude)) == MP_OK);
    std::vector<float> audio = fx.render(k_rate / 2);
    // 10 Hz at 0.5 moves at most 6.5e-4 per sample; a step of -6 dB at a peak would be 0.25. The RMS window is
    // the last 0.2 s: two whole cycles, so the phase does not matter.
    CHECK(max_step(audio) < 0.005);
    CHECK(rms(audio, k_rate / 2 - k_rate / 5, k_rate / 2) ==
          Catch::Approx(0.5 * 0.501187 / std::sqrt(2.0)).margin(0.005));

    // Back to unity while playing: also ramped.
    REQUIRE(mp_track_set_replaygain(t, 0.0f, static_cast<float>(spec.amplitude)) == MP_OK);
    audio = fx.render(k_rate / 2);
    CHECK(max_step(audio) < 0.005);
    CHECK(rms(audio, k_rate / 2 - k_rate / 5, k_rate / 2) == Catch::Approx(0.5 / std::sqrt(2.0)).margin(0.005));
}

TEST_CASE("the gain survives a seek", "[replaygain]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 3.0;
    mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "replaygain-seek"));
    REQUIRE(mp_track_set_replaygain(t, -6.0f, static_cast<float>(spec.amplitude)) == MP_OK);
    REQUIRE(mp_engine_play(fx.engine, t, 0) == MP_OK);
    fx.render(k_rate / 2);
    REQUIRE(mp_engine_seek(fx.engine, 2000) == MP_OK);
    const std::vector<float> audio = fx.render(k_rate / 2);
    CHECK(db(rms(audio, k_rate / 4, k_rate / 2) / (spec.amplitude / std::sqrt(2.0))) ==
          Catch::Approx(-6.0).margin(0.1));
    REQUIRE(mp_track_close(t) == MP_OK);
}
