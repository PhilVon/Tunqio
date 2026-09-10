// Headless engine tests: everything that works on the BASS "no sound" device without a WASAPI output.
// Output, playback and latency are exercised by native/spikes/bass_hello (needs a real device).
#include "mpcore.h"

#include "wav_fixture.h"

#include <catch2/catch_amalgamated.hpp>
#include <cstring>
#include <string>

namespace {

struct engine_fixture {
    mp_engine* engine = nullptr;

    engine_fixture() {
        mp_engine_config cfg{};
        cfg.struct_size = sizeof cfg;
        const mp_result r = mp_engine_create(&cfg, &engine);
        if (r != MP_OK) {
            char err[512];
            mp_last_error(err, sizeof err);
            FAIL("mp_engine_create failed: " << err);
        }
    }
    ~engine_fixture() {
        if (engine != nullptr) {
            mp_engine_destroy(engine);
        }
    }
};

std::string last_error() {
    char buf[512];
    mp_last_error(buf, sizeof buf);
    return buf;
}

} // namespace

TEST_CASE("engine rejects a bad config struct_size", "[engine][abi]") {
    mp_engine_config cfg{};
    cfg.struct_size = sizeof cfg - 4;
    mp_engine* e = nullptr;
    CHECK(mp_engine_create(&cfg, &e) == MP_E_INVALID_ARG);
    CHECK(e == nullptr);
    CHECK(mp_engine_create(nullptr, &e) == MP_E_INVALID_ARG);
}

TEST_CASE("only one engine per process", "[engine]") {
    engine_fixture fx;
    mp_engine_config cfg{};
    cfg.struct_size = sizeof cfg;
    mp_engine* second = nullptr;
    CHECK(mp_engine_create(&cfg, &second) == MP_E_STATE);
    CHECK(second == nullptr);
    CHECK(last_error().find("already exists") != std::string::npos);
}

TEST_CASE("engine create/destroy cycles are stable", "[engine][stress]") {
    for (int i = 0; i < 50; ++i) {
        mp_engine_config cfg{};
        cfg.struct_size = sizeof cfg;
        mp_engine* e = nullptr;
        REQUIRE(mp_engine_create(&cfg, &e) == MP_OK);
        REQUIRE(mp_engine_destroy(e) == MP_OK);
    }
}

TEST_CASE("a generated WAV opens with the right info", "[engine][track]") {
    engine_fixture fx;
    mp::tests::wav_spec spec;
    spec.seconds = 2.0;
    const std::string path = mp::tests::write_sine_wav(spec, "engine-info");
    REQUIRE_FALSE(path.empty());

    mp_track* t = nullptr;
    REQUIRE(mp_track_open(fx.engine, path.c_str(), &t) == MP_OK);
    REQUIRE(t != nullptr);

    mp_track_info info{};
    info.struct_size = sizeof info;
    REQUIRE(mp_track_get_info(t, &info) == MP_OK);
    CHECK(info.sample_rate == 48000);
    CHECK(info.channels == 2);
    CHECK(info.bits_per_sample == 16);
    CHECK(std::string{info.codec} == "wav");
    CHECK(info.duration_ms >= 1995);
    CHECK(info.duration_ms <= 2005);
    CHECK(info.total_frames == 96000);

    CHECK(mp_track_close(t) == MP_OK);
}

TEST_CASE("opening a missing file reports a BASS error with a message", "[engine][track]") {
    engine_fixture fx;
    mp_track* t = nullptr;
    CHECK(mp_track_open(fx.engine, "Z:\\does\\not\\exist.wav", &t) == MP_E_BASS);
    CHECK(t == nullptr);
    CHECK(last_error().find("FILEOPEN") != std::string::npos);
}

TEST_CASE("play without an output is a state error", "[engine]") {
    engine_fixture fx;
    const std::string path = mp::tests::write_sine_wav({}, "engine-play");
    REQUIRE_FALSE(path.empty());
    mp_track* t = nullptr;
    REQUIRE(mp_track_open(fx.engine, path.c_str(), &t) == MP_OK);
    CHECK(mp_engine_play(fx.engine, t, 0) == MP_E_STATE);
    CHECK(last_error().find("no output") != std::string::npos);
}

TEST_CASE("clock and stats read cleanly on an idle engine", "[engine]") {
    engine_fixture fx;
    mp_clock clock{};
    clock.struct_size = sizeof clock;
    REQUIRE(mp_engine_get_clock(fx.engine, &clock) == MP_OK);
    CHECK(clock.position_ms == 0);
    CHECK(clock.qpc_ticks > 0);

    mp_engine_stats stats{};
    stats.struct_size = sizeof stats;
    REQUIRE(mp_engine_get_stats(fx.engine, &stats) == MP_OK);
    CHECK(stats.callbacks == 0);
    CHECK(stats.underruns == 0);
    CHECK(stats.output_started == 0);

    mp_clock bad{};
    bad.struct_size = 1;
    CHECK(mp_engine_get_clock(fx.engine, &bad) == MP_E_INVALID_ARG);
}

TEST_CASE("device enumeration honours the count protocol", "[engine][device]") {
    engine_fixture fx;
    uint32_t count = 0;
    REQUIRE(mp_engine_enum_devices(fx.engine, nullptr, &count) == MP_OK);
    // A CI runner may have no output device at all; the protocol must still hold.
    if (count > 0) {
        std::vector<mp_device_info> devices(count);
        uint32_t written = count;
        REQUIRE(mp_engine_enum_devices(fx.engine, devices.data(), &written) == MP_OK);
        CHECK(written == count);
        CHECK(devices[0].struct_size == sizeof(mp_device_info));
        CHECK(std::strlen(devices[0].name) > 0);
        uint32_t one = 1;
        REQUIRE(mp_engine_enum_devices(fx.engine, devices.data(), &one) == MP_OK);
        CHECK(one == 1);
    }
}

TEST_CASE("unimplemented exports say which story implements them", "[engine][abi]") {
    engine_fixture fx;
    CHECK(mp_engine_set_crossfade(fx.engine, 100) == MP_E_STATE);
    CHECK(last_error().find("E1-S4") != std::string::npos);
    mp_analysis_frame frame{};
    frame.struct_size = sizeof frame;
    CHECK(mp_analysis_try_get_latest(fx.engine, &frame) == MP_E_STATE);
    CHECK(last_error().find("E1-S8") != std::string::npos);
}

TEST_CASE("tracks die with their engine and a destroyed engine frees the process slot", "[engine]") {
    mp_engine_config cfg{};
    cfg.struct_size = sizeof cfg;
    mp_engine* e = nullptr;
    REQUIRE(mp_engine_create(&cfg, &e) == MP_OK);
    const std::string path = mp::tests::write_sine_wav({}, "engine-lifetime");
    mp_track* t = nullptr;
    REQUIRE(mp_track_open(e, path.c_str(), &t) == MP_OK);
    REQUIRE(mp_engine_destroy(e) == MP_OK); // frees the track too; t is now invalid by contract

    mp_engine* again = nullptr;
    REQUIRE(mp_engine_create(&cfg, &again) == MP_OK);
    REQUIRE(mp_engine_destroy(again) == MP_OK);
}
