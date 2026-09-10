// E1-S6 output tests: device selection, exclusive-mode init at the device's native rate, and the fallback to
// shared mode when a driver refuses exclusive. These need a real WASAPI output device, so each one skips (does
// not fail) when the machine has none - a CI runner usually has no audio device at all.
//
// The exclusive failure itself cannot be provoked on hardware that is willing, so it is injected through
// mp::audio::testing::force_exclusive_failure, a seam that exists only in the MP_STATIC build these tests link.
#include "mpcore.h"

#include "audio/bass_engine.h"

#include <catch2/catch_amalgamated.hpp>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

namespace {

struct engine_fixture {
    mp_engine* engine = nullptr;

    explicit engine_fixture(uint32_t sample_rate = 0) {
        mp_engine_config cfg{};
        cfg.struct_size = sizeof cfg;
        cfg.sample_rate = sample_rate;
        const mp_result r = mp_engine_create(&cfg, &engine);
        if (r != MP_OK) {
            char err[512];
            mp_last_error(err, sizeof err);
            FAIL("mp_engine_create failed: " << err);
        }
    }
    ~engine_fixture() {
        mp::audio::testing::force_exclusive_failure(false);
        if (engine != nullptr) {
            mp_engine_destroy(engine);
        }
    }
};

// Every enabled output device, or an empty list when the machine has none.
std::vector<mp_device_info> devices(mp_engine* engine) {
    uint32_t count = 0;
    if (mp_engine_enum_devices(engine, nullptr, &count) != MP_OK || count == 0) {
        return {};
    }
    std::vector<mp_device_info> list(count);
    uint32_t written = count;
    if (mp_engine_enum_devices(engine, list.data(), &written) != MP_OK) {
        return {};
    }
    list.resize(written);
    return list;
}

// The device MP_DEVICE_DEFAULT opens, which is what these tests ask for.
const mp_device_info* default_device(const std::vector<mp_device_info>& list) {
    for (const auto& d : list) {
        if (d.is_default != 0) {
            return &d;
        }
    }
    return list.empty() ? nullptr : &list.front();
}

// Engine events, collected off the calling thread the way a real consumer would.
struct event_log {
    std::mutex mutex;
    std::vector<std::pair<mp_event_type, std::string>> events;

    static void MP_CALL sink(const mp_event* ev, void* user) {
        auto* self = static_cast<event_log*>(user);
        std::lock_guard lock{self->mutex};
        self->events.emplace_back(ev->type, ev->message != nullptr ? ev->message : "");
    }

    std::string first_message(mp_event_type type) {
        std::lock_guard lock{mutex};
        for (const auto& [t, message] : events) {
            if (t == type) {
                return message;
            }
        }
        return {};
    }

    bool contains(mp_event_type type) {
        std::lock_guard lock{mutex};
        for (const auto& [t, message] : events) {
            if (t == type) {
                return true;
            }
        }
        return false;
    }
};

mp_output_config output_on(int32_t device_index, mp_output_mode mode) {
    mp_output_config out{};
    out.struct_size = sizeof out;
    out.device_index = device_index;
    out.mode = mode;
    return out;
}

mp_engine_stats stats_of(mp_engine* engine) {
    mp_engine_stats s{};
    s.struct_size = sizeof s;
    REQUIRE(mp_engine_get_stats(engine, &s) == MP_OK);
    return s;
}

std::string last_error() {
    char buf[512];
    mp_last_error(buf, sizeof buf);
    return buf;
}

} // namespace

TEST_CASE("a device can be chosen by index and by default", "[output][device]") {
    engine_fixture fx;
    const auto list = devices(fx.engine);
    if (list.empty()) {
        SKIP("no output device on this machine");
    }

    mp_output_config out = output_on(MP_DEVICE_DEFAULT, MP_OUTPUT_SHARED);
    REQUIRE(mp_engine_set_output(fx.engine, &out) == MP_OK);
    const mp_engine_stats on_default = stats_of(fx.engine);
    CHECK(on_default.output_sample_rate > 0);
    CHECK(on_default.output_channels > 0);

    const mp_device_info* chosen = default_device(list);
    REQUIRE(chosen != nullptr);
    out = output_on(chosen->index, MP_OUTPUT_SHARED);
    REQUIRE(mp_engine_set_output(fx.engine, &out) == MP_OK);
    const mp_engine_stats on_index = stats_of(fx.engine);
    // Naming the default device by its index is the same output as asking for the default.
    CHECK(on_index.output_sample_rate == on_default.output_sample_rate);
    CHECK(on_index.output_channels == on_default.output_channels);
}

TEST_CASE("a device index that does not exist is refused", "[output][device]") {
    engine_fixture fx;
    mp_output_config out = output_on(9999, MP_OUTPUT_SHARED);
    CHECK(mp_engine_set_output(fx.engine, &out) != MP_OK);
    CHECK(last_error().find("BASS_WASAPI_Init") != std::string::npos);
    // Nothing is left half-open: the engine reports no output rather than a device it does not have.
    const mp_engine_stats s = stats_of(fx.engine);
    CHECK(s.output_started == 0);
    CHECK(std::strcmp(s.output_format, "none") == 0);
}

TEST_CASE("exclusive mode opens the device at its own rate, not the engine's", "[output][exclusive]") {
    // 96 kHz is deliberately not what a desktop device sits at: before E1-S6 the exclusive init asked for the
    // mixer's rate, so the device would have been driven at 96 kHz and every source resampled to it.
    engine_fixture fx{96000};
    const auto list = devices(fx.engine);
    const mp_device_info* device = default_device(list);
    if (device == nullptr) {
        SKIP("no output device on this machine");
    }
    if (device->mix_sample_rate == 0) {
        SKIP("the default device does not report a mix rate");
    }

    mp_output_config out = output_on(MP_DEVICE_DEFAULT, MP_OUTPUT_EXCLUSIVE);
    REQUIRE(mp_engine_set_output(fx.engine, &out) == MP_OK);
    const mp_engine_stats s = stats_of(fx.engine);
    INFO("exclusive=" << int{s.exclusive} << " output=" << s.output_sample_rate << " Hz, device mix rate "
                      << device->mix_sample_rate << " Hz");
    CHECK(s.output_sample_rate == device->mix_sample_rate);
    CHECK(s.output_started == 1);
}

TEST_CASE("an exclusive mode the driver refuses falls back to shared and says why", "[output][exclusive]") {
    engine_fixture fx;
    if (devices(fx.engine).empty()) {
        SKIP("no output device on this machine");
    }
    event_log log;
    REQUIRE(mp_engine_set_event_callback(fx.engine, &event_log::sink, &log) == MP_OK);

    mp::audio::testing::force_exclusive_failure(true);
    mp_output_config out = output_on(MP_DEVICE_DEFAULT, MP_OUTPUT_EXCLUSIVE);
    // The user asked for exclusive and cannot have it; they keep their music.
    REQUIRE(mp_engine_set_output(fx.engine, &out) == MP_OK);

    const mp_engine_stats s = stats_of(fx.engine);
    CHECK(s.exclusive == 0);
    CHECK(s.output_started == 1);
    CHECK(s.output_sample_rate > 0);

    const std::string message = log.first_message(MP_EVENT_ERROR);
    INFO("error message: " << message);
    CHECK(message.find("exclusive") != std::string::npos);
    CHECK(message.find("shared mode") != std::string::npos);
    // The reason the exclusive attempt gave is carried through, not swallowed.
    CHECK(message.find("BASS_WASAPI_Init(exclusive)") != std::string::npos);
    // A successful fallback is not an error the caller has to read: mp_last_error is clear.
    CHECK(last_error().empty());
}

TEST_CASE("a shared-mode failure is not dressed up as a fallback", "[output][exclusive]") {
    engine_fixture fx;
    event_log log;
    REQUIRE(mp_engine_set_event_callback(fx.engine, &event_log::sink, &log) == MP_OK);

    // Shared mode has nothing to fall back to, so the failure is the caller's to handle.
    mp_output_config out = output_on(9999, MP_OUTPUT_SHARED);
    CHECK(mp_engine_set_output(fx.engine, &out) != MP_OK);
    CHECK_FALSE(log.contains(MP_EVENT_ERROR));
}

TEST_CASE("the headless output is never exclusive", "[output][exclusive]") {
    engine_fixture fx;
    mp_output_config out = output_on(MP_DEVICE_NONE, MP_OUTPUT_EXCLUSIVE);
    REQUIRE(mp_engine_set_output(fx.engine, &out) == MP_OK);
    const mp_engine_stats s = stats_of(fx.engine);
    CHECK(s.exclusive == 0);
    CHECK(std::strcmp(s.output_format, "render") == 0);
}
