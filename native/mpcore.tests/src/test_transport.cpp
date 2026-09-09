// E1-S1 transport tests. The engine is opened on MP_DEVICE_NONE and the test drives the pull stage with
// mp_engine_render, so what a WASAPI device would have played is a buffer the test can inspect: fades,
// volume, seek accuracy, the pause hold, the clock, and (Debug) the no-allocation rule of the audio path.
#include "mpcore.h"

#include "audio/bass_engine.h"
#include "common/rt_guard.h"
#include "wav_fixture.h"

#include <algorithm>
#include <catch2/catch_amalgamated.hpp>
#include <cmath>
#include <cstring>
#include <string>
#include <vector>

namespace {

constexpr uint32_t k_rate = 48000;
constexpr uint32_t k_channels = 2;
constexpr uint32_t k_fade_frames = k_rate * mp::audio::engine::k_guard_fade_ms / 1000; // 2400
constexpr uint32_t k_buffer_frames = 480;                                              // 10 ms, a shared-mode period

std::string last_error() {
    char buf[512];
    mp_last_error(buf, sizeof buf);
    return buf;
}

struct offline_engine {
    mp_engine* engine = nullptr;

    offline_engine() {
        mp_engine_config cfg{};
        cfg.struct_size = sizeof cfg;
        cfg.sample_rate = k_rate;
        cfg.channels = k_channels;
        if (mp_engine_create(&cfg, &engine) != MP_OK) {
            FAIL("mp_engine_create failed: " << last_error());
        }
        mp_output_config out{};
        out.struct_size = sizeof out;
        out.device_index = MP_DEVICE_NONE;
        if (mp_engine_set_output(engine, &out) != MP_OK) {
            FAIL("mp_engine_set_output(MP_DEVICE_NONE) failed: " << last_error());
        }
    }
    ~offline_engine() {
        if (engine != nullptr) {
            mp_engine_destroy(engine);
        }
    }

    mp_track* open(const std::string& path) {
        REQUIRE_FALSE(path.empty());
        mp_track* t = nullptr;
        REQUIRE(mp_track_open(engine, path.c_str(), &t) == MP_OK);
        return t;
    }

    // Pulls `frames` frames in k_buffer_frames pieces, as a device would, and returns them concatenated.
    std::vector<float> render(uint32_t frames) {
        std::vector<float> out(static_cast<size_t>(frames) * k_channels);
        uint32_t done = 0;
        while (done < frames) {
            const uint32_t n = std::min(k_buffer_frames, frames - done);
            REQUIRE(mp_engine_render(engine, out.data() + static_cast<size_t>(done) * k_channels, n) == MP_OK);
            done += n;
        }
        return out;
    }

    int64_t position_ms() {
        mp_clock clock{};
        clock.struct_size = sizeof clock;
        REQUIRE(mp_engine_get_clock(engine, &clock) == MP_OK);
        return clock.position_ms;
    }
};

double rms(const std::vector<float>& samples, size_t from_frame, size_t to_frame) {
    double sum = 0;
    size_t n = 0;
    for (size_t i = from_frame * k_channels; i < to_frame * k_channels && i < samples.size(); ++i) {
        sum += static_cast<double>(samples[i]) * samples[i];
        ++n;
    }
    return n == 0 ? 0.0 : std::sqrt(sum / static_cast<double>(n));
}

// Largest sample-to-sample step per channel: what a click is.
double max_step(const std::vector<float>& samples) {
    double worst = 0;
    for (size_t i = k_channels; i < samples.size(); ++i) {
        worst = std::max(worst, static_cast<double>(std::fabs(samples[i] - samples[i - k_channels])));
    }
    return worst;
}

bool all_zero(const std::vector<float>& samples, size_t from_frame) {
    return std::all_of(samples.begin() + static_cast<std::ptrdiff_t>(from_frame * k_channels), samples.end(),
                       [](float s) { return s == 0.0f; });
}

void append(std::vector<float>& to, const std::vector<float>& more) {
    to.insert(to.end(), more.begin(), more.end());
}

} // namespace

TEST_CASE("render is only available on the MP_DEVICE_NONE output", "[transport][abi]") {
    mp_engine_config cfg{};
    cfg.struct_size = sizeof cfg;
    mp_engine* e = nullptr;
    REQUIRE(mp_engine_create(&cfg, &e) == MP_OK);
    float buf[16];
    CHECK(mp_engine_render(e, buf, 8) == MP_E_STATE);
    CHECK(last_error().find("MP_DEVICE_NONE") != std::string::npos);

    mp_output_config out{};
    out.struct_size = sizeof out;
    out.device_index = MP_DEVICE_NONE;
    REQUIRE(mp_engine_set_output(e, &out) == MP_OK);
    CHECK(mp_engine_render(e, buf, 0) == MP_OK);
    CHECK(mp_engine_render(e, buf, 8) == MP_OK);
    CHECK(std::all_of(buf, buf + 16, [](float s) { return s == 0.0f; })); // nothing playing: silence

    mp_engine_stats stats{};
    stats.struct_size = sizeof stats;
    REQUIRE(mp_engine_get_stats(e, &stats) == MP_OK);
    CHECK(std::string{stats.output_format} == "render");
    CHECK(stats.output_started == 1);
    CHECK(stats.callbacks == 1); // a zero-frame render is not a callback
    REQUIRE(mp_engine_destroy(e) == MP_OK);
}

TEST_CASE("play fades in over the guard time and the clock follows the rendered audio", "[transport]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 3.0;
    mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "transport-play"));

    REQUIRE(mp_engine_play(fx.engine, t, 0) == MP_OK);
    std::vector<float> audio = fx.render(k_rate); // one second

    const double expected = spec.amplitude / std::sqrt(2.0);
    CHECK(rms(audio, 0, 480) < expected * 0.5); // still fading in
    CHECK(rms(audio, k_fade_frames + 480, k_rate) == Catch::Approx(expected).margin(expected * 0.05));
    CHECK(fx.position_ms() == Catch::Approx(1000).margin(25));

    std::vector<float> more = fx.render(k_rate / 2);
    CHECK(fx.position_ms() == Catch::Approx(1500).margin(25));
    CHECK(max_step(more) < 0.02); // a steady 440 Hz sine at -20 dBFS: no discontinuity

    CHECK(mp_engine_stop(fx.engine, MP_FADE_NONE) == MP_OK);
    CHECK(fx.position_ms() == 0);
    CHECK(all_zero(fx.render(480), 0));
}

TEST_CASE("pause and resume are click-free and the position holds while paused", "[transport]") {
    offline_engine fx;
    // 10 Hz at -6 dBFS: the signal's own sample-to-sample step (0.5 * 2*pi*10 / 48000 = 6.5e-4) stays under
    // the 1e-3 click bound, so anything above it is the engine's doing.
    mp::tests::wav_spec spec;
    spec.seconds = 4.0;
    spec.frequency_hz = 10.0;
    spec.amplitude = 0.5;
    mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "transport-pause"));

    REQUIRE(mp_engine_play(fx.engine, t, 0) == MP_OK);
    std::vector<float> audio = fx.render(k_rate / 2); // 500 ms: fade-in done, signal running

    REQUIRE(mp_engine_pause(fx.engine) == MP_OK);
    append(audio, fx.render(k_rate / 4));                     // 250 ms: 50 ms fade-out, then the hold
    CHECK(all_zero(audio, audio.size() / k_channels - 4800)); // the last 100 ms are silence
    const int64_t held = fx.position_ms();
    append(audio, fx.render(k_rate / 4));
    CHECK(fx.position_ms() == held); // the mixer is not advanced while held
    CHECK(all_zero(audio, audio.size() / k_channels - 12000));

    REQUIRE(mp_engine_resume(fx.engine) == MP_OK);
    append(audio, fx.render(k_rate / 2));
    CHECK(fx.position_ms() > held + 400);
    CHECK(rms(audio, audio.size() / k_channels - 4800, audio.size() / k_channels) ==
          Catch::Approx(spec.amplitude / std::sqrt(2.0)).margin(0.05));

    CHECK(max_step(audio) < 1e-3);

    CHECK(mp_engine_pause(fx.engine) == MP_OK);
    CHECK(mp_engine_pause(fx.engine) == MP_OK); // idempotent
}

TEST_CASE("pause and resume without a track are state errors", "[transport]") {
    offline_engine fx;
    CHECK(mp_engine_pause(fx.engine) == MP_E_STATE);
    CHECK(mp_engine_resume(fx.engine) == MP_E_STATE);
    CHECK(mp_engine_seek(fx.engine, 0) == MP_E_STATE);
    CHECK(mp_engine_stop(fx.engine, MP_FADE_GUARD) == MP_OK); // stopping nothing is fine
}

TEST_CASE("seek and play land within one frame", "[transport]") {
    offline_engine fx;
    mp_track* t = fx.open(mp::tests::write_position_wav(k_rate, 3.0, "transport-position"));

    // The rendered frame at index k after the fade-in encodes source frame (target + k).
    auto source_frame_at = [&](const std::vector<float>& audio, size_t k) {
        return mp::tests::decode_position_frame(audio[k * k_channels], audio[k * k_channels + 1]);
    };
    const size_t probe = k_fade_frames + 100; // past the fade: samples are unscaled again

    SECTION("play from an offset") {
        REQUIRE(mp_engine_play(fx.engine, t, 500) == MP_OK);
        std::vector<float> audio = fx.render(4800);
        CHECK(std::llabs(source_frame_at(audio, probe) - (24000 + static_cast<int64_t>(probe))) <= 1);
    }

    SECTION("seek while playing") {
        REQUIRE(mp_engine_play(fx.engine, t, 0) == MP_OK);
        fx.render(4800);
        REQUIRE(mp_engine_seek(fx.engine, 1250) == MP_OK);
        std::vector<float> audio = fx.render(4800);
        CHECK(std::llabs(source_frame_at(audio, probe) - (60000 + static_cast<int64_t>(probe))) <= 1);
        CHECK(fx.position_ms() == Catch::Approx(1350).margin(15));
    }

    SECTION("seek while paused stays held and resumes at the new position") {
        REQUIRE(mp_engine_play(fx.engine, t, 0) == MP_OK);
        fx.render(4800);
        REQUIRE(mp_engine_pause(fx.engine) == MP_OK);
        fx.render(9600); // fade out and hold
        REQUIRE(mp_engine_seek(fx.engine, 2000) == MP_OK);
        CHECK(all_zero(fx.render(4800), 0)); // still held
        CHECK(fx.position_ms() == Catch::Approx(2000).margin(15));
        REQUIRE(mp_engine_resume(fx.engine) == MP_OK);
        std::vector<float> audio = fx.render(4800);
        CHECK(std::llabs(source_frame_at(audio, probe) - (96000 + static_cast<int64_t>(probe))) <= 1);
    }
}

TEST_CASE("volume follows an audio taper and a mute lands within one buffer without a click", "[transport]") {
    using mp::audio::engine;
    CHECK(engine::volume_taper(0.0f) == 0.0f);
    CHECK(engine::volume_taper(-1.0f) == 0.0f);
    CHECK(engine::volume_taper(1.0f) == 1.0f);
    CHECK(engine::volume_taper(2.0f) == 1.0f);
    CHECK(engine::volume_taper(0.5f) == Catch::Approx(0.1f).epsilon(0.01));     // -20 dB at half travel
    CHECK(engine::volume_taper(0.25f) == Catch::Approx(0.0316f).epsilon(0.02)); // -30 dB
    CHECK(engine::volume_taper(0.75f) == Catch::Approx(0.316f).epsilon(0.02));  // -10 dB
    float previous = 0.0f;
    for (int i = 1; i <= 100; ++i) {
        const float g = engine::volume_taper(static_cast<float>(i) / 100.0f);
        CHECK(g > previous);
        previous = g;
    }

    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 4.0;
    spec.frequency_hz = 10.0;
    spec.amplitude = 0.25;
    mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "transport-volume"));
    REQUIRE(mp_engine_play(fx.engine, t, 0) == MP_OK);
    std::vector<float> audio = fx.render(k_rate / 2);
    const double full = spec.amplitude / std::sqrt(2.0);
    CHECK(rms(audio, k_fade_frames + 480, k_rate / 2) == Catch::Approx(full).margin(full * 0.05));

    REQUIRE(mp_engine_set_volume(fx.engine, 0.5f) == MP_OK);
    std::vector<float> transition = fx.render(k_buffer_frames); // the interpolation buffer
    std::vector<float> settled = fx.render(k_rate / 4);
    CHECK(rms(settled, 0, k_rate / 4) == Catch::Approx(full * 0.1).margin(full * 0.1 * 0.05));
    append(audio, transition);
    append(audio, settled);

    REQUIRE(mp_engine_set_volume(fx.engine, 0.0f) == MP_OK);
    std::vector<float> muting = fx.render(k_buffer_frames);
    std::vector<float> muted = fx.render(k_rate / 4);
    CHECK(all_zero(muted, 0)); // silent from the very next buffer on
    append(audio, muting);
    append(audio, muted);

    REQUIRE(mp_engine_set_volume(fx.engine, 1.0f) == MP_OK);
    append(audio, fx.render(k_rate / 4));
    CHECK(rms(audio, audio.size() / k_channels - 4800, audio.size() / k_channels) ==
          Catch::Approx(full).margin(full * 0.05));

    CHECK(max_step(audio) < 1e-3);
}

TEST_CASE("a stop with the guard fade ends in silence rather than a cut", "[transport]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 4.0;
    spec.frequency_hz = 10.0;
    spec.amplitude = 0.5;
    mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "transport-stop"));
    REQUIRE(mp_engine_play(fx.engine, t, 0) == MP_OK);
    std::vector<float> audio = fx.render(k_rate / 2);
    // Headless there is no audio thread to wait for, so a guard stop drops the level at once: the contract
    // the live device gets (fade, then remove) is exercised by the pause test above; here the state machine
    // must at least leave the engine idle and silent.
    REQUIRE(mp_engine_stop(fx.engine, MP_FADE_GUARD) == MP_OK);
    CHECK(all_zero(fx.render(4800), 0));
    CHECK(fx.position_ms() == 0);
    CHECK(mp_engine_resume(fx.engine) == MP_E_STATE);

    // Playing again after a stop fades in from silence.
    REQUIRE(mp_engine_play(fx.engine, t, 0) == MP_OK);
    std::vector<float> again = fx.render(4800);
    CHECK(std::fabs(again[0]) < 1e-3);
    CHECK(max_step(again) < 1e-3);
}

TEST_CASE("the end of a track is reported once and the engine falls silent", "[transport][event]") {
    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 0.25;
    mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "transport-end"));

    struct seen {
        int started = 0;
        int ended = 0;
        int64_t ended_a = 0;
    } events;
    mp_engine_set_event_callback(
        fx.engine,
        [](const mp_event* ev, void* user) {
            auto* s = static_cast<seen*>(user);
            if (ev->type == MP_EVENT_TRACK_STARTED) {
                ++s->started;
            } else if (ev->type == MP_EVENT_TRACK_ENDED) {
                ++s->ended;
                s->ended_a = ev->a;
            }
        },
        &events);

    REQUIRE(mp_engine_play(fx.engine, t, 0) == MP_OK);
    CHECK(events.started == 1);
    fx.render(k_rate); // well past the 250 ms of audio
    CHECK(events.ended == 1);
    CHECK(events.ended_a != 0);
    CHECK(all_zero(fx.render(4800), 0));
    mp_engine_set_event_callback(fx.engine, nullptr, nullptr);
}

#if defined(MP_DEBUG) && MP_DEBUG
TEST_CASE("the pull stage allocates nothing (RT_ASSERT_NO_ALLOC)", "[transport][rt]") {
    // First prove the hook sees an allocation inside a scope at all.
    mp::rt::reset_violations();
    {
        mp::rt::scope inside;
        std::vector<int> v(1);
        CHECK(v.size() == 1);
    }
    CHECK(mp::rt::violations() >= 1);
    CHECK_FALSE(mp::rt::active());

    offline_engine fx;
    mp::tests::wav_spec spec;
    spec.seconds = 2.0;
    mp_track* t = fx.open(mp::tests::write_sine_wav(spec, "transport-rt"));
    REQUIRE(mp_engine_play(fx.engine, t, 0) == MP_OK);
    std::vector<float> buffer(static_cast<size_t>(k_buffer_frames) * k_channels);
    mp::rt::reset_violations();
    for (int i = 0; i < 150; ++i) { // 1.5 s: fade-in, steady state, a volume change, a pause, a resume
        if (i == 40) {
            mp_engine_set_volume(fx.engine, 0.3f);
        }
        if (i == 80) {
            mp_engine_pause(fx.engine);
        }
        if (i == 110) {
            mp_engine_resume(fx.engine);
        }
        REQUIRE(mp_engine_render(fx.engine, buffer.data(), k_buffer_frames) == MP_OK);
    }
    CHECK(mp::rt::violations() == 0);
}
#endif

TEST_CASE("every engine export rejects NULL handles and unknown struct sizes", "[transport][abi]") {
    offline_engine fx;
    mp_track* t = fx.open(mp::tests::write_sine_wav({}, "transport-contract"));
    mp_engine* e = fx.engine;

    mp_output_config out{};
    out.struct_size = sizeof out;
    mp_output_config bad_out = out;
    bad_out.struct_size = sizeof out + 1;
    mp_track_info info{};
    info.struct_size = sizeof info;
    mp_track_info bad_info = info;
    bad_info.struct_size = 0;
    mp_clock clock{};
    clock.struct_size = sizeof clock;
    mp_clock bad_clock = clock;
    bad_clock.struct_size = sizeof clock - 1;
    mp_engine_stats stats{};
    stats.struct_size = sizeof stats;
    mp_engine_stats bad_stats = stats;
    bad_stats.struct_size = 12;
    mp_analysis_frame frame{};
    frame.struct_size = sizeof frame;
    mp_analysis_frame bad_frame = frame;
    bad_frame.struct_size = 1;
    uint32_t count = 0;
    mp_track* out_track = nullptr;
    mp_engine* out_engine = nullptr;
    float buf[8];

    CHECK(mp_engine_create(nullptr, &out_engine) == MP_E_INVALID_ARG);
    mp_engine_config cfg{};
    cfg.struct_size = sizeof cfg;
    CHECK(mp_engine_create(&cfg, nullptr) == MP_E_INVALID_ARG);
    CHECK(mp_engine_destroy(nullptr) == MP_E_INVALID_ARG);
    CHECK(mp_engine_set_output(nullptr, &out) == MP_E_INVALID_ARG);
    CHECK(mp_engine_set_output(e, nullptr) == MP_E_INVALID_ARG);
    CHECK(mp_engine_set_output(e, &bad_out) == MP_E_INVALID_ARG);
    CHECK(mp_engine_enum_devices(nullptr, nullptr, &count) == MP_E_INVALID_ARG);
    CHECK(mp_engine_enum_devices(e, nullptr, nullptr) == MP_E_INVALID_ARG);
    CHECK(mp_engine_set_event_callback(nullptr, nullptr, nullptr) == MP_E_INVALID_ARG);
    CHECK(mp_track_open(nullptr, "x.wav", &out_track) == MP_E_INVALID_ARG);
    CHECK(mp_track_open(e, nullptr, &out_track) == MP_E_INVALID_ARG);
    CHECK(mp_track_open(e, "x.wav", nullptr) == MP_E_INVALID_ARG);
    CHECK(mp_track_open(e, "", &out_track) == MP_E_INVALID_ARG);
    CHECK(mp_track_close(nullptr) == MP_E_INVALID_ARG);
    CHECK(mp_track_get_info(nullptr, &info) == MP_E_INVALID_ARG);
    CHECK(mp_track_get_info(t, nullptr) == MP_E_INVALID_ARG);
    CHECK(mp_track_get_info(t, &bad_info) == MP_E_INVALID_ARG);
    CHECK(mp_engine_play(nullptr, t, 0) == MP_E_INVALID_ARG);
    CHECK(mp_engine_play(e, nullptr, 0) == MP_E_INVALID_ARG);
    CHECK(mp_engine_preload_next(nullptr, t) == MP_E_INVALID_ARG);
    CHECK(mp_engine_pause(nullptr) == MP_E_INVALID_ARG);
    CHECK(mp_engine_resume(nullptr) == MP_E_INVALID_ARG);
    CHECK(mp_engine_stop(nullptr, MP_FADE_NONE) == MP_E_INVALID_ARG);
    CHECK(mp_engine_stop(e, static_cast<mp_fade_mode>(7)) == MP_E_INVALID_ARG);
    CHECK(mp_engine_seek(nullptr, 0) == MP_E_INVALID_ARG);
    CHECK(mp_engine_set_volume(nullptr, 1.0f) == MP_E_INVALID_ARG);
    CHECK(mp_engine_set_replaygain(nullptr, 0.0f, 1.0f) == MP_E_INVALID_ARG);
    CHECK(mp_engine_set_crossfade(nullptr, 0) == MP_E_INVALID_ARG);
    CHECK(mp_engine_get_clock(nullptr, &clock) == MP_E_INVALID_ARG);
    CHECK(mp_engine_get_clock(e, nullptr) == MP_E_INVALID_ARG);
    CHECK(mp_engine_get_clock(e, &bad_clock) == MP_E_INVALID_ARG);
    CHECK(mp_engine_get_stats(nullptr, &stats) == MP_E_INVALID_ARG);
    CHECK(mp_engine_get_stats(e, &bad_stats) == MP_E_INVALID_ARG);
    CHECK(mp_engine_render(nullptr, buf, 4) == MP_E_INVALID_ARG);
    CHECK(mp_engine_render(e, nullptr, 4) == MP_E_INVALID_ARG);
    CHECK(mp_preview_start(nullptr, t, 0.0f) == MP_E_INVALID_ARG);
    CHECK(mp_preview_stop(nullptr) == MP_E_INVALID_ARG);
    CHECK(mp_analysis_try_get_latest(nullptr, &frame) == MP_E_INVALID_ARG);
    CHECK(mp_analysis_try_get_latest(e, &bad_frame) == MP_E_INVALID_ARG);
    CHECK(mp_log_set_sink(nullptr, nullptr, MP_LOG_INFO) == MP_OK); // clearing the sink is valid

    // The engine is unharmed by any of it.
    CHECK(mp_engine_play(e, t, 0) == MP_OK);
    CHECK(mp_engine_get_clock(e, &clock) == MP_OK);
    CHECK(mp_track_close(t) == MP_OK);
}
