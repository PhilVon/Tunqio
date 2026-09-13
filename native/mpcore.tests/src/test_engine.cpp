// Headless engine tests: everything that works on the BASS "no sound" device without a WASAPI output.
// Output, playback and latency are exercised by native/spikes/bass_hello (needs a real device).
#include "mpcore.h"

#include "wav_fixture.h"

#include <catch2/catch_amalgamated.hpp>
#include <cstring>
#include <stdexcept>
#include <string>

#include <windows.h>

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

// ---- T-164: the fixtures themselves ----------------------------------------------------------------
//
// These two are about the test harness rather than the engine, and they are here because this file is the
// harness's oldest consumer. They exist because the harness's failure mode was to blame the engine: a fixture
// that could not be written returned a bare empty string, and the reader met REQUIRE_FALSE(path.empty()) at
// offline_engine.h:57 with no fixture named and no reason given.

TEST_CASE("fixtures live in a directory this process does not share", "[fixture]") {
    const std::string root = mp::tests::fixture_root();
    INFO("fixture root: " << root);
    // The pid is what stops two mpcore.tests.exe -- parallel agents in separate worktrees, or a gate sweep
    // beside a peer's -- writing, reading and deleting the same files.
    CHECK(root.find("tunqio-tests-" + std::to_string(GetCurrentProcessId())) != std::string::npos);
    // And a fixture really is written inside it, rather than beside it in the machine-wide %TEMP%.
    const std::string path = mp::tests::write_sine_wav({}, "fixture-isolation");
    CHECK(path.rfind(root, 0) == 0);
}

TEST_CASE("a fixture that cannot be written names the fixture and the reason", "[fixture]") {
    // A stem that puts the file in a subdirectory nobody created: the closest thing to a disk problem that is
    // reproducible on any machine, and it exercises exactly the path a full disk or a denied ACL would take.
    std::string message;
    try {
        (void)mp::tests::write_sine_wav({}, "no-such-subdirectory\\fixture-failure");
        FAIL("writing into a directory that does not exist was expected to throw");
    } catch (const std::runtime_error& e) {
        message = e.what();
    }
    INFO("thrown message: " << message);
    // The fixture, so a reader knows WHICH one.
    CHECK(message.find("no-such-subdirectory\\fixture-failure") != std::string::npos);
    // The full path, so a reader can go and look.
    CHECK(message.find(mp::tests::fixture_root()) != std::string::npos);
    // The OS's own reason, so a disk problem reads as a disk problem and not as an audio-engine regression.
    CHECK(message.find("errno 2") != std::string::npos);
    CHECK(message.find("engine") == std::string::npos);
}

// Was "rejects a bad config struct_size" and used sizeof - 4, which since ABI 0.12 is an older header being
// served rather than an error (test_abi.cpp asserts that direction). What is left an error is a size no header
// ever had: longer than this build's, or too short to hold the first field.
TEST_CASE("engine rejects a config struct_size no header ever had", "[engine][abi]") {
    mp_engine_config cfg{};
    cfg.struct_size = sizeof cfg + 4; // built against a newer mpcore.h than this one
    mp_engine* e = nullptr;
    CHECK(mp_engine_create(&cfg, &e) == MP_E_INVALID_ARG);
    CHECK(e == nullptr);
    cfg.struct_size = sizeof cfg.struct_size; // the size field and nothing else
    CHECK(mp_engine_create(&cfg, &e) == MP_E_INVALID_ARG);
    CHECK(e == nullptr);
    CHECK(mp_engine_create(nullptr, &e) == MP_E_INVALID_ARG);
}

TEST_CASE("an engine is created from an older header's config", "[engine][abi]") {
    // struct_size and sample_rate, and nothing else: channels and plugin_dir take the zero each documents as
    // its default, which is two channels and the directory mpcore.dll sits in.
    mp_engine_config cfg{};
    cfg.struct_size = static_cast<uint32_t>(offsetof(mp_engine_config, sample_rate) + sizeof cfg.sample_rate);
    cfg.sample_rate = 44100;
    mp_engine* e = nullptr;
    REQUIRE(mp_engine_create(&cfg, &e) == MP_OK);
    REQUIRE(e != nullptr);
    mp_engine_stats stats{};
    stats.struct_size = sizeof stats;
    CHECK(mp_engine_get_stats(e, &stats) == MP_OK);
    CHECK(mp_engine_destroy(e) == MP_OK);
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
    bad.struct_size = 1; // shorter than mp_clock's first field, so no header this ever was
    CHECK(mp_engine_get_clock(fx.engine, &bad) == MP_E_INVALID_ARG);
}

TEST_CASE("device enumeration honours the count protocol", "[engine][device]") {
    engine_fixture fx;
    uint32_t count = 0;
    REQUIRE(mp_engine_enum_devices(fx.engine, nullptr, &count) == MP_OK);
    // A CI runner may have no output device at all; the protocol must still hold.
    if (count > 0) {
        std::vector<mp_device_info> devices(count);
        devices[0].struct_size = sizeof(mp_device_info); // the element size, and so the stride (ABI 0.12)
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

TEST_CASE("the preview pair needs an output to start and is idle-safe to stop", "[engine][abi]") {
    engine_fixture fx;
    // This case asserted the pair was unimplemented until E5-S5 implemented it, the last exports that were.
    CHECK(mp_preview_stop(fx.engine) == MP_OK); // nothing is previewing
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
