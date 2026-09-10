// E1-S4 crossfade tests. The crossfade is a per-source equal-power envelope run by the mixer, started by a
// mix-time position sync on the outgoing track, and chosen per boundary by the caller (MP_JOIN_CROSSFADE against
// MP_JOIN_GAPLESS). The tests listen to the pull stage (MP_DEVICE_NONE + mp_engine_render) and measure: the RMS
// across a 5 s overlap of two uncorrelated sines stays flat (equal power), the events say where the overlap began,
// a gapless join ignores the crossfade, ReplayGain still applies under the fade, and the transport calls that cut
// a crossfade short leave nothing behind.
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

constexpr uint32_t k_frame_bytes = k_channels * sizeof(float);
constexpr double k_amplitude = 0.5;
const double k_level = k_amplitude / std::sqrt(2.0); // RMS of one sine at full gain

double db(double ratio) {
    return 20.0 * std::log10(ratio);
}

struct captured {
    mp_event_type type;
    int64_t a;
    int64_t b;
};

// Events arrive on the thread that renders (the test's own), so a plain vector is enough.
struct event_log {
    std::vector<captured> events;

    static void MP_CALL on_event(const mp_event* ev, void* user) {
        static_cast<event_log*>(user)->events.push_back({ev->type, ev->a, ev->b});
    }

    void attach(mp_engine* engine) { REQUIRE(mp_engine_set_event_callback(engine, &on_event, this) == MP_OK); }

    // Index of the first event of `type` for `track`, or -1.
    int find(mp_event_type type, const mp_track* track, size_t from = 0) const {
        for (size_t i = from; i < events.size(); ++i) {
            if (events[i].type == type && events[i].a == reinterpret_cast<int64_t>(track)) {
                return static_cast<int>(i);
            }
        }
        return -1;
    }
};

std::string sine(double seconds, double hz, const char* stem, double amplitude = k_amplitude) {
    mp::tests::wav_spec spec;
    spec.seconds = seconds;
    spec.frequency_hz = hz;
    spec.amplitude = amplitude;
    return mp::tests::write_sine_wav(spec, stem);
}

// The largest deviation, in dB, of the RMS over consecutive `window` frame windows of [from, to) from `level`.
double worst_window_db(const std::vector<float>& audio, size_t from, size_t to, size_t window, double level) {
    double worst = 0;
    for (size_t w = from; w + window <= to; w += window) {
        worst = std::max(worst, std::fabs(db(rms(audio, w, w + window) / level)));
    }
    return worst;
}

} // namespace

TEST_CASE("a 5 s crossfade overlaps two tracks on an equal-power curve", "[crossfade]") {
    offline_engine fx;
    event_log log;
    log.attach(fx.engine);
    REQUIRE(mp_engine_set_crossfade(fx.engine, 5000) == MP_OK);
    // Two 6 s sines at different frequencies: uncorrelated over a 250 ms window, so the RMS of their mix is the
    // root of the sum of their powers, which equal-power gains hold constant.
    mp_track* a = fx.open(sine(6.0, 440.0, "crossfade-a"));
    mp_track* b = fx.open(sine(6.0, 660.0, "crossfade-b"));

    REQUIRE(mp_engine_play(fx.engine, a, 0) == MP_OK);
    REQUIRE(mp_engine_preload_next_ex(fx.engine, b, MP_JOIN_CROSSFADE) == MP_OK);
    const std::vector<float> audio = fx.render(static_cast<uint32_t>(7.5 * k_rate));

    // The overlap: a runs 0-6 s, so the fade point is 1 s; b then plays 1-7 s.
    const size_t fade_start = k_rate;
    const size_t fade_end = 6 * k_rate;

    SECTION("events: b starts at the fade point (within one buffer), a ends when its last frame is mixed") {
        const int started_b = log.find(MP_EVENT_TRACK_STARTED, b);
        const int ended_a = log.find(MP_EVENT_TRACK_ENDED, a);
        const int ended_b = log.find(MP_EVENT_TRACK_ENDED, b);
        REQUIRE(started_b >= 0);
        REQUIRE(ended_a >= 0);
        REQUIRE(ended_b >= 0);
        CHECK(started_b < ended_a); // the overlap: b is playing before a has ended
        CHECK(ended_a < ended_b);
        const int64_t at = log.events[static_cast<size_t>(started_b)].b;
        const auto nominal = static_cast<int64_t>(fade_start * k_frame_bytes);
        CHECK(at <= nominal);
        CHECK(at >= nominal - static_cast<int64_t>(k_buffer_frames * k_frame_bytes));
        CHECK(log.events[static_cast<size_t>(ended_a)].b == 0); // no successor took over at that position
        CHECK(log.find(MP_EVENT_TRACK_STARTED, b, static_cast<size_t>(started_b) + 1) == -1); // once
    }

    SECTION("the RMS across the overlap stays within 3 dB of one track's level (it stays within 0.5 dB)") {
        const double worst = worst_window_db(audio, fade_start, fade_end, k_rate / 4, k_level);
        INFO("largest RMS deviation across the overlap: " << worst << " dB");
        CHECK(worst < 3.0); // the criterion
        CHECK(worst < 0.5); // what equal power actually gives
        // Before and after: one track alone at its own level.
        CHECK(rms(audio, k_rate / 2, fade_start) == Catch::Approx(k_level).margin(0.005));
        CHECK(rms(audio, fade_end + k_rate / 4, 7 * k_rate) == Catch::Approx(k_level).margin(0.005));
        // Half way: both at -3 dB.
        const size_t mid = (fade_start + fade_end) / 2;
        CHECK(rms(audio, mid - k_rate / 8, mid + k_rate / 8) == Catch::Approx(k_level).margin(0.01));
    }

    SECTION("no click anywhere: the largest step is the sines' own") {
        // 660 Hz at 0.5 steps by at most 0.043 per sample; the two together, at most 0.072.
        CHECK(max_step(audio) < 0.08);
        // And the output is silent once b has ended.
        CHECK(all_zero(audio, 7 * k_rate + k_buffer_frames));
    }

    REQUIRE(mp_track_close(a) == MP_OK);
    REQUIRE(mp_track_close(b) == MP_OK);
}

TEST_CASE("a gapless join ignores the crossfade, and a crossfade of 0 ms is a gapless join", "[crossfade][gapless]") {
    offline_engine fx;
    event_log log;
    log.attach(fx.engine);
    // 10 Hz: a ends a whole number of cycles in, b starts at phase 0, so a continuous join meets at a zero crossing.
    mp_track* a = fx.open(sine(1.0, 10.0, "crossfade-gapless-a"));
    mp_track* b = fx.open(sine(1.0, 10.0, "crossfade-gapless-b"));

    SECTION("MP_JOIN_GAPLESS with a 5 s crossfade set") {
        REQUIRE(mp_engine_set_crossfade(fx.engine, 5000) == MP_OK);
        REQUIRE(mp_engine_play(fx.engine, a, 0) == MP_OK);
        REQUIRE(mp_engine_preload_next(fx.engine, b) == MP_OK);
    }
    SECTION("MP_JOIN_CROSSFADE with the crossfade off") {
        REQUIRE(mp_engine_set_crossfade(fx.engine, 0) == MP_OK);
        REQUIRE(mp_engine_play(fx.engine, a, 0) == MP_OK);
        REQUIRE(mp_engine_preload_next_ex(fx.engine, b, MP_JOIN_CROSSFADE) == MP_OK);
    }
    const std::vector<float> audio = fx.render(2 * k_rate);

    const size_t join = k_rate;
    CHECK(rms(audio, join - k_rate / 4, join) == Catch::Approx(k_level).margin(0.005)); // no fade-out
    CHECK(rms(audio, join, join + k_rate / 4) == Catch::Approx(k_level).margin(0.005)); // no fade-in
    CHECK(std::fabs(audio[join * k_channels] - audio[(join - 1) * k_channels]) < 0.002);
    const int ended_a = log.find(MP_EVENT_TRACK_ENDED, a);
    const int started_b = log.find(MP_EVENT_TRACK_STARTED, b);
    REQUIRE(ended_a >= 0);
    REQUIRE(started_b >= 0);
    CHECK(ended_a < started_b); // the gapless order: ended, then started, at the same position
    CHECK(log.events[static_cast<size_t>(ended_a)].b == static_cast<int64_t>(join * k_frame_bytes));
    CHECK(log.events[static_cast<size_t>(started_b)].b == static_cast<int64_t>(join * k_frame_bytes));
}

TEST_CASE("ReplayGain still applies under the crossfade", "[crossfade][replaygain]") {
    offline_engine fx;
    REQUIRE(mp_engine_set_crossfade(fx.engine, 2000) == MP_OK);
    mp_track* a = fx.open(sine(3.0, 440.0, "crossfade-rg-a"));
    mp_track* b = fx.open(sine(3.0, 660.0, "crossfade-rg-b"));
    REQUIRE(mp_track_set_replaygain(b, -6.0f, static_cast<float>(k_amplitude)) == MP_OK);

    REQUIRE(mp_engine_play(fx.engine, a, 0) == MP_OK);
    REQUIRE(mp_engine_preload_next_ex(fx.engine, b, MP_JOIN_CROSSFADE) == MP_OK);
    const std::vector<float> audio = fx.render(4 * k_rate);

    // b alone from 3 s: at its ReplayGain level, the fade long finished.
    CHECK(db(rms(audio, 3 * k_rate + k_rate / 4, 4 * k_rate) / k_level) == Catch::Approx(-6.0).margin(0.1));
    // Half way through the overlap (2 s): a at -3 dB plus b at -9 dB, uncorrelated.
    const double expected_mid = std::sqrt(0.5 * k_level * k_level + 0.5 * 0.501187 * 0.501187 * k_level * k_level);
    CHECK(rms(audio, 2 * k_rate - k_rate / 8, 2 * k_rate + k_rate / 8) == Catch::Approx(expected_mid).margin(0.01));
}

TEST_CASE("cutting a crossfade short leaves nothing behind", "[crossfade][transport]") {
    offline_engine fx;
    event_log log;
    log.attach(fx.engine);
    REQUIRE(mp_engine_set_crossfade(fx.engine, 5000) == MP_OK);
    mp_track* a = fx.open(sine(6.0, 440.0, "crossfade-cut-a"));
    mp_track* b = fx.open(sine(6.0, 660.0, "crossfade-cut-b"));
    REQUIRE(mp_engine_play(fx.engine, a, 0) == MP_OK);
    REQUIRE(mp_engine_preload_next_ex(fx.engine, b, MP_JOIN_CROSSFADE) == MP_OK);
    fx.render(3 * k_rate); // two seconds into the overlap
    REQUIRE(log.find(MP_EVENT_TRACK_STARTED, b) >= 0);
    REQUIRE(log.find(MP_EVENT_TRACK_ENDED, a) == -1);

    SECTION("stop takes both sources out") {
        REQUIRE(mp_engine_stop(fx.engine, MP_FADE_GUARD) == MP_OK);
        CHECK(log.find(MP_EVENT_TRACK_ENDED, a) >= 0); // the outgoing tail was cut: it has ended
        CHECK(all_zero(fx.render(k_rate / 2), 0));
        CHECK(fx.position_ms() == 0);
    }
    SECTION("playing a third track by hand replaces both") {
        mp_track* c = fx.open(sine(2.0, 220.0, "crossfade-cut-c", 0.25));
        REQUIRE(mp_engine_play(fx.engine, c, 0) == MP_OK);
        CHECK(log.find(MP_EVENT_TRACK_ENDED, a) >= 0);
        CHECK(log.find(MP_EVENT_TRACK_STARTED, c) >= 0);
        const std::vector<float> audio = fx.render(k_rate);
        CHECK(rms(audio, k_rate / 2, k_rate) == Catch::Approx(0.25 / std::sqrt(2.0)).margin(0.005)); // c alone
        CHECK(max_step(audio) < 0.02);
        REQUIRE(mp_track_close(c) == MP_OK);
    }
    SECTION("closing the outgoing track mid-fade leaves the incoming playing") {
        REQUIRE(mp_track_close(a) == MP_OK);
        CHECK(log.find(MP_EVENT_TRACK_ENDED, a) >= 0);
        const std::vector<float> audio = fx.render(k_rate);
        // b alone, 2-3 s into its 5 s fade-in (it started at 1 s): sin(pi/2 * 2.5/5) = 0.707 at the window's middle.
        CHECK(rms(audio, k_rate / 4, 3 * k_rate / 4) == Catch::Approx(k_level * 0.707).margin(0.02));
        CHECK(max_step(audio) < 0.05);
        a = nullptr;
    }
    SECTION("preloading the track that is still fading out is refused") {
        CHECK(mp_engine_preload_next_ex(fx.engine, a, MP_JOIN_CROSSFADE) == MP_E_INVALID_ARG);
        CHECK(last_error().find("fading out") != std::string::npos);
    }
    if (a != nullptr) {
        REQUIRE(mp_track_close(a) == MP_OK);
    }
    REQUIRE(mp_track_close(b) == MP_OK);
}

TEST_CASE("a seek decides the join again from the new position", "[crossfade][transport]") {
    offline_engine fx;
    event_log log;
    log.attach(fx.engine);
    REQUIRE(mp_engine_set_crossfade(fx.engine, 5000) == MP_OK);
    // 10 Hz so a gapless seam is a zero crossing: a seek to a whole 0.1 s lands on one too.
    mp_track* a = fx.open(sine(6.0, 10.0, "crossfade-seek-a"));
    mp_track* b = fx.open(sine(2.0, 10.0, "crossfade-seek-b"));
    REQUIRE(mp_engine_play(fx.engine, a, 0) == MP_OK);
    REQUIRE(mp_engine_preload_next_ex(fx.engine, b, MP_JOIN_CROSSFADE) == MP_OK);
    fx.render(k_rate / 2);

    SECTION("into the fade window: the join becomes gapless at full level") {
        REQUIRE(mp_engine_seek(fx.engine, 5000) == MP_OK); // the window is 1-6 s
        const std::vector<float> audio = fx.render(2 * k_rate);
        CHECK(rms(audio, k_rate / 4, 3 * k_rate / 4) == Catch::Approx(k_level).margin(0.005)); // a, not fading
        CHECK(rms(audio, k_rate + k_rate / 4, k_rate + 3 * k_rate / 4) == Catch::Approx(k_level).margin(0.005)); // b
        const int ended_a = log.find(MP_EVENT_TRACK_ENDED, a);
        const int started_b = log.find(MP_EVENT_TRACK_STARTED, b);
        REQUIRE(ended_a >= 0);
        REQUIRE(started_b >= 0);
        CHECK(ended_a < started_b);
        CHECK(log.events[static_cast<size_t>(ended_a)].b == log.events[static_cast<size_t>(started_b)].b);
        CHECK(log.events[static_cast<size_t>(ended_a)].b != 0);
        CHECK(max_step(audio) < 0.05);
    }
    SECTION("back before the fade point: the crossfade still happens there") {
        REQUIRE(mp_engine_seek(fx.engine, 500) == MP_OK);
        fx.render(k_rate); // a reaches 1.5 s; the fade point (1 s) was passed with the mixer at 1 s
        const int started_b = log.find(MP_EVENT_TRACK_STARTED, b);
        REQUIRE(started_b >= 0);
        REQUIRE(log.find(MP_EVENT_TRACK_ENDED, a) == -1);
        const int64_t at = log.events[static_cast<size_t>(started_b)].b;
        const auto nominal = static_cast<int64_t>(k_rate * k_frame_bytes);
        CHECK(at <= nominal);
        CHECK(at >= nominal - static_cast<int64_t>(k_buffer_frames * k_frame_bytes));
    }
    REQUIRE(mp_track_close(a) == MP_OK);
    REQUIRE(mp_track_close(b) == MP_OK);
}

TEST_CASE("the crossfade exports check their arguments", "[crossfade][abi]") {
    offline_engine fx;
    mp_track* t = fx.open(sine(1.0, 440.0, "crossfade-abi"));
    CHECK(mp_engine_set_crossfade(nullptr, 100) == MP_E_INVALID_ARG);
    CHECK(mp_engine_set_crossfade(fx.engine, 13000) == MP_OK); // clamped to 12 s, not refused
    CHECK(mp_engine_preload_next_ex(nullptr, t, MP_JOIN_CROSSFADE) == MP_E_INVALID_ARG);
    CHECK(mp_engine_preload_next_ex(fx.engine, t, static_cast<mp_join_mode>(7)) == MP_E_INVALID_ARG);
    CHECK(last_error().find("join mode") != std::string::npos);
    CHECK(mp_engine_preload_next_ex(fx.engine, nullptr, MP_JOIN_CROSSFADE) == MP_OK); // clears the queue
    REQUIRE(mp_track_close(t) == MP_OK);
}
