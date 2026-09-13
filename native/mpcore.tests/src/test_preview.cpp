// E5-S5 hover preview. Listened to at the pull stage (MP_DEVICE_NONE + mp_engine_render), as the ReplayGain and
// crossfade tests are: a preview is heard at its gain once its 200 ms ramp has landed, the main track ducks by 6 dB
// under it and comes back after it, a stop fades it out in 200 ms, it stops by itself after 15 s, a long track is
// sampled from 30% in, and a second preview is refused while the first can still be heard.
#include "mpcore.h"

#include "audio/bass_engine.h"
#include "offline_engine.h"
#include "wav_fixture.h"

#include <catch2/catch_amalgamated.hpp>
#include <cmath>
#include <string>
#include <vector>

using mp::tests::all_zero;
using mp::tests::k_buffer_frames;
using mp::tests::k_channels;
using mp::tests::k_rate;
using mp::tests::last_error;
using mp::tests::max_step;
using mp::tests::offline_engine;
using mp::tests::rms;

namespace {

constexpr uint32_t k_ramp = k_rate * mp::audio::engine::k_preview_ramp_ms / 1000; // 9600

double db(double ratio) {
    return 20.0 * std::log10(ratio);
}

double sine_rms(double amplitude) {
    return amplitude / std::sqrt(2.0);
}

// Silence, for a preview whose only effect should be the duck it causes.
std::string write_silence(double seconds, const char* stem) {
    return mp::tests::write_wav(k_rate, 2, static_cast<uint32_t>(seconds * k_rate), stem,
                                [](uint32_t, uint16_t) { return static_cast<int16_t>(0); });
}

// Silent for the first 30% of `seconds`, a tone after it: whether a preview is heard in its first second says where
// in the file it started. Mono at 8 kHz so a 70 s file stays small; the preview mixer resamples it.
std::string write_late_tone(double seconds, const char* stem) {
    constexpr uint32_t rate = 8000;
    const auto frames = static_cast<uint32_t>(seconds * rate);
    const auto quiet = static_cast<uint32_t>(frames * 0.3) - rate / 2; // the tone starts half a second before 30%
    return mp::tests::write_wav(rate, 1, frames, stem, [quiet](uint32_t i, uint16_t) {
        return i < quiet
                   ? static_cast<int16_t>(0)
                   : static_cast<int16_t>(std::lround(0.5 * 32767.0 * std::sin(6.283185307179586 * 440.0 * i / rate)));
    });
}

} // namespace

TEST_CASE("a preview is heard at its gain once its ramp has landed, and ramps rather than steps in", "[preview]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 3.0;
    spec.amplitude = 0.5;
    mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "preview-level"));

    REQUIRE(mp_preview_start(fx.engine, t, -12.0f) == MP_OK);
    const std::vector<float> audio = fx.render(k_rate);

    const double settled = rms(audio, k_ramp + k_buffer_frames, k_rate);
    CHECK(db(settled / sine_rms(spec.amplitude)) == Catch::Approx(-12.0).margin(0.2));
    CHECK(rms(audio, 0, k_ramp / 4) < settled * 0.5); // the first 50 ms are well under the settled level
    CHECK(max_step(audio) < 0.05);                    // no click at the start

    REQUIRE(mp_preview_stop(fx.engine) == MP_OK);
    REQUIRE(mp_track_close(t) == MP_OK);
}

TEST_CASE("the main track ducks by 6 dB under a preview and comes back after it", "[preview]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 6.0;
    spec.amplitude = 0.2;
    mp_track* main = fx.open(mp::tests::write_sine_wav(spec, "preview-duck-main"));
    mp_track* quiet = fx.open(write_silence(3.0, "preview-duck-silence"));

    REQUIRE(mp_engine_play(fx.engine, main, 0) == MP_OK);
    const std::vector<float> before = fx.render(k_rate / 2);
    const double level = rms(before, k_rate / 4, k_rate / 2);
    REQUIRE(level == Catch::Approx(sine_rms(spec.amplitude)).margin(0.005));

    REQUIRE(mp_preview_start(fx.engine, quiet, -12.0f) == MP_OK);
    const std::vector<float> during = fx.render(k_rate);
    CHECK(db(rms(during, k_ramp + k_buffer_frames, k_rate) / level) == Catch::Approx(-6.0).margin(0.2));
    CHECK(max_step(during) < 0.05); // a duck, not a step

    REQUIRE(mp_preview_stop(fx.engine) == MP_OK);
    const std::vector<float> after = fx.render(k_rate);
    CHECK(db(rms(after, k_ramp + k_buffer_frames, k_rate) / level) == Catch::Approx(0.0).margin(0.2));

    REQUIRE(mp_engine_stop(fx.engine, MP_FADE_NONE) == MP_OK);
    REQUIRE(mp_track_close(quiet) == MP_OK);
    REQUIRE(mp_track_close(main) == MP_OK);
}

TEST_CASE("a stop fades the preview out within 200 ms, rather than cutting it", "[preview]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 3.0;
    spec.amplitude = 0.5;
    mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "preview-stop"));

    REQUIRE(mp_preview_start(fx.engine, t, -12.0f) == MP_OK);
    fx.render(k_rate / 2);
    REQUIRE(mp_preview_stop(fx.engine) == MP_OK);
    const std::vector<float> tail = fx.render(k_rate / 2);

    CHECK(rms(tail, 0, k_ramp / 4) > 0.0); // still sounding at the start of the fade
    CHECK(all_zero(tail, k_ramp + 1));     // and silent once 200 ms have passed
    CHECK(max_step(tail) < 0.05);
    REQUIRE(mp_track_close(t) == MP_OK);
}

TEST_CASE("a preview stops by itself after 15 s", "[preview]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 20.0;
    spec.amplitude = 0.5;
    mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "preview-limit"));

    REQUIRE(mp_preview_start(fx.engine, t, -12.0f) == MP_OK);
    const std::vector<float> body = fx.render(k_rate * 15 - k_buffer_frames);
    // Still at its gain in the last second before the limit, not already fading.
    CHECK(db(rms(body, static_cast<size_t>(k_rate) * 14, static_cast<size_t>(k_rate) * 15 - k_buffer_frames) /
             sine_rms(spec.amplitude)) == Catch::Approx(-12.0).margin(0.2));
    const std::vector<float> tail = fx.render(k_rate / 2);
    CHECK(all_zero(tail, k_ramp + 2 * k_buffer_frames));
    REQUIRE(mp_track_close(t) == MP_OK);
}

TEST_CASE("a track of a minute or more is sampled from 30% in, a shorter one from the start", "[preview]") {
    offline_engine fx;

    SECTION("70 s: the tone after 30% is heard at once") {
        mp_track* t = fx.open(write_late_tone(70.0, "preview-long"));
        REQUIRE(mp_preview_start(fx.engine, t, -12.0f) == MP_OK);
        const std::vector<float> audio = fx.render(k_rate);
        CHECK(rms(audio, k_ramp + k_buffer_frames, k_rate) > 0.05);
        REQUIRE(mp_track_close(t) == MP_OK);
    }

    SECTION("50 s: it starts at 0, which is silent") {
        mp_track* t = fx.open(write_late_tone(50.0, "preview-short"));
        REQUIRE(mp_preview_start(fx.engine, t, -12.0f) == MP_OK);
        const std::vector<float> audio = fx.render(k_rate);
        CHECK(rms(audio, 0, k_rate) == 0.0);
        REQUIRE(mp_track_close(t) == MP_OK);
    }
}

TEST_CASE("one preview at a time: a second start waits until the first has faded out", "[preview]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 3.0;
    mp_track* a = fx.open(mp::tests::write_sine_wav(spec, "preview-single-a"));
    mp_track* b = fx.open(mp::tests::write_sine_wav(spec, "preview-single-b"));

    REQUIRE(mp_preview_start(fx.engine, a, -12.0f) == MP_OK);
    CHECK(mp_preview_start(fx.engine, b, -12.0f) == MP_E_STATE);
    CHECK(last_error().find("one preview at a time") != std::string::npos);

    fx.render(k_rate / 4);
    REQUIRE(mp_preview_stop(fx.engine) == MP_OK);
    CHECK(mp_preview_start(fx.engine, b, -12.0f) == MP_E_STATE); // still fading out
    fx.render(k_ramp + k_buffer_frames);
    CHECK(mp_preview_start(fx.engine, b, -12.0f) == MP_OK);

    REQUIRE(mp_preview_stop(fx.engine) == MP_OK);
    REQUIRE(mp_track_close(b) == MP_OK);
    REQUIRE(mp_track_close(a) == MP_OK);
}

TEST_CASE("the track already playing can be previewed, and the main track keeps its place", "[preview]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 4.0;
    mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "preview-same-track"));

    REQUIRE(mp_engine_play(fx.engine, t, 0) == MP_OK);
    fx.render(k_rate / 2);
    const int64_t before = fx.position_ms();
    REQUIRE(mp_preview_start(fx.engine, t, -12.0f) == MP_OK); // its own stream, not the one the mixer is reading
    fx.render(k_rate / 2);
    CHECK(fx.position_ms() - before == Catch::Approx(500).margin(30));

    REQUIRE(mp_preview_stop(fx.engine) == MP_OK);
    REQUIRE(mp_engine_stop(fx.engine, MP_FADE_NONE) == MP_OK);
    REQUIRE(mp_track_close(t) == MP_OK);
}

TEST_CASE("a preview plays over paused music", "[preview]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 4.0;
    spec.amplitude = 0.5;
    mp_track* main = fx.open(mp::tests::write_sine_wav(spec, "preview-paused-main"));
    mp_track* sample = fx.open(mp::tests::write_sine_wav(spec, "preview-paused-sample"));

    REQUIRE(mp_engine_play(fx.engine, main, 0) == MP_OK);
    fx.render(k_rate / 4);
    REQUIRE(mp_engine_pause(fx.engine) == MP_OK);
    fx.render(k_rate / 4);
    REQUIRE(mp_preview_start(fx.engine, sample, -12.0f) == MP_OK);
    const std::vector<float> audio = fx.render(k_rate);
    CHECK(db(rms(audio, k_ramp + k_buffer_frames, k_rate) / sine_rms(spec.amplitude)) ==
          Catch::Approx(-12.0).margin(0.3));

    REQUIRE(mp_preview_stop(fx.engine) == MP_OK);
    REQUIRE(mp_engine_stop(fx.engine, MP_FADE_NONE) == MP_OK);
    REQUIRE(mp_track_close(sample) == MP_OK);
    REQUIRE(mp_track_close(main) == MP_OK);
}

// AC-141's last clause, at its source: the visualizer reads the analysis tap, so a preview the tap never hears is a
// preview the visualizer never follows.
TEST_CASE("the analysis tap never hears the preview", "[preview][analysis]") {
    offline_engine fx;
    auto* core = reinterpret_cast<mp::audio::engine*>(fx.engine);
    // As in test_analysis_tap.cpp: stop the analysis thread so this test is the ring's one consumer.
    core->analysis_thread().stop();
    mp::analysis::tap& engine_tap = core->analysis_tap();

    mp::tests::wav_spec spec;
    spec.seconds = 3.0;
    spec.amplitude = 0.5;
    mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "preview-tap"));
    REQUIRE(mp_preview_start(fx.engine, t, -12.0f) == MP_OK);
    const std::vector<float> audio = fx.render(k_rate / 2);
    // The listener hears the preview at its gain (0.5 amplitude at -12 dB is an RMS of 0.0888).
    CHECK(db(rms(audio, k_ramp + k_buffer_frames, k_rate / 2) / sine_rms(spec.amplitude)) ==
          Catch::Approx(-12.0).margin(0.2));

    mp::analysis::tap_block block;
    int blocks = 0;
    float loudest = 0.0f;
    while (engine_tap.try_read(block)) {
        ++blocks;
        for (float s : block.samples) {
            loudest = std::max(loudest, std::fabs(s));
        }
    }
    CHECK(blocks > 0);      // the tap was running
    CHECK(loudest == 0.0f); // and heard only the main mix, which is silence

    REQUIRE(mp_preview_stop(fx.engine) == MP_OK);
    REQUIRE(mp_track_close(t) == MP_OK);
}

TEST_CASE("mp_preview_start refuses a gain that is not a number, and needs an output", "[preview][abi]") {
    SECTION("NaN gain") {
        offline_engine fx;
        mp::tests::wav_spec spec;
        mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "preview-nan"));
        CHECK(mp_preview_start(fx.engine, t, std::nanf("")) == MP_E_INVALID_ARG);
        REQUIRE(mp_track_close(t) == MP_OK);
    }
}
