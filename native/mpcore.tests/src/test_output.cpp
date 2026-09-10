// E1-S6 output tests: device selection, exclusive-mode init at the device's native rate, and the fallback to
// shared mode when a driver refuses exclusive. These need a real WASAPI output device, so each one skips (does
// not fail) when the machine has none - a CI runner usually has no audio device at all.
//
// E1-S7 device changes are here too: the same seam posts a BASSWASAPI device notification as if a driver had
// raised it, so unplugging, re-plugging and a default-device change are all testable without touching hardware.
//
// The exclusive failure itself cannot be provoked on hardware that is willing, so it is injected through
// mp::audio::testing::force_exclusive_failure, a seam that exists only in the MP_STATIC build these tests link.
#include "mpcore.h"

#include "audio/bass_engine.h"
#include "wav_fixture.h"

#include <catch2/catch_amalgamated.hpp>
#include <chrono>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

#include <windows.h>

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
        self->payloads.emplace_back(ev->a, ev->b);
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

    size_t count(mp_event_type type) {
        std::lock_guard lock{mutex};
        size_t n = 0;
        for (const auto& [t, message] : events) {
            n += t == type ? 1 : 0;
        }
        return n;
    }

    void clear() {
        std::lock_guard lock{mutex};
        events.clear();
        payloads.clear();
    }

    // a and b of the first event of a type, for the ones that carry a device index and whether it was moved to.
    std::pair<int64_t, int64_t> first_payload(mp_event_type type) {
        std::lock_guard lock{mutex};
        for (size_t i = 0; i < events.size(); ++i) {
            if (events[i].first == type) {
                return payloads[i];
            }
        }
        return {-1, -1};
    }

    std::vector<std::pair<int64_t, int64_t>> payloads;
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

// ---- E1-S7 device changes ----------------------------------------------------------------------

namespace {

using mp::audio::testing::k_notify_default_output;
using mp::audio::testing::k_notify_disabled;
using mp::audio::testing::k_notify_enabled;
using mp::audio::testing::k_notify_fail;
using mp::audio::testing::simulate_device_notification;

// Opens the default device in shared mode and starts a two-second sine on it. Returns the track, or nullptr when
// the machine has no output device (every caller then skips).
mp_track* play_on_the_default_device(mp_engine* engine, const char* stem) {
    mp_output_config out = output_on(MP_DEVICE_DEFAULT, MP_OUTPUT_SHARED);
    if (mp_engine_set_output(engine, &out) != MP_OK) {
        return nullptr;
    }
    const std::string path = mp::tests::write_sine_wav({}, stem);
    mp_track* track = nullptr;
    REQUIRE(mp_track_open(engine, path.c_str(), &track) == MP_OK);
    REQUIRE(mp_engine_play(engine, track, 0) == MP_OK);
    return track;
}

int64_t position_ms(mp_engine* engine) {
    mp_clock clock{};
    clock.struct_size = sizeof clock;
    REQUIRE(mp_engine_get_clock(engine, &clock) == MP_OK);
    return clock.position_ms;
}

DWORD process_handles() {
    DWORD count = 0;
    return GetProcessHandleCount(GetCurrentProcess(), &count) ? count : 0;
}

// The BASS index of the open device, read back the way a caller would: the one the enumeration marks default.
int32_t default_device_index(mp_engine* engine) {
    const auto list = devices(engine);
    const mp_device_info* d = default_device(list);
    return d != nullptr ? d->index : -1;
}

} // namespace

TEST_CASE("losing the open device parks playback and says which device went", "[output][device-change]") {
    engine_fixture fx;
    event_log log;
    REQUIRE(mp_engine_set_event_callback(fx.engine, &event_log::sink, &log) == MP_OK);
    mp_track* track = play_on_the_default_device(fx.engine, "device-lost");
    if (track == nullptr) {
        SKIP("no output device on this machine");
    }
    const int32_t device = default_device_index(fx.engine);
    log.clear();

    const auto t0 = std::chrono::steady_clock::now();
    simulate_device_notification(k_notify_fail, static_cast<uint32_t>(device));
    const auto elapsed = std::chrono::steady_clock::now() - t0;

    // AC-52: within 500 ms, and in practice as fast as the watch thread can be scheduled.
    INFO("parked in " << std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count() << " ms");
    CHECK(elapsed < std::chrono::milliseconds(500));
    CHECK(log.count(MP_EVENT_DEVICE_LOST) == 1);
    CHECK(log.first_payload(MP_EVENT_DEVICE_LOST).first == device);
    CHECK_FALSE(log.first_message(MP_EVENT_DEVICE_LOST).empty()); // the endpoint id, for the shell to name it

    // The output is gone and the music is parked, not stopped: the track is still loaded at its position.
    const mp_engine_stats s = stats_of(fx.engine);
    CHECK(s.output_started == 0);
    CHECK(std::strcmp(s.output_format, "none") == 0);
    CHECK(mp_engine_resume(fx.engine) == MP_OK); // still loaded; nothing was thrown away
}

TEST_CASE("a device that comes back is offered, not taken", "[output][device-change]") {
    engine_fixture fx;
    event_log log;
    REQUIRE(mp_engine_set_event_callback(fx.engine, &event_log::sink, &log) == MP_OK);
    mp_track* track = play_on_the_default_device(fx.engine, "device-back");
    if (track == nullptr) {
        SKIP("no output device on this machine");
    }
    const int32_t device = default_device_index(fx.engine);
    simulate_device_notification(k_notify_fail, static_cast<uint32_t>(device));
    REQUIRE(log.count(MP_EVENT_DEVICE_LOST) == 1);
    log.clear();

    simulate_device_notification(k_notify_enabled, static_cast<uint32_t>(device));

    // AC-53: the shell is told the device is back, with its id, and offers "switch back". The engine does not
    // move on its own - ui-screens-and-flows.md wants the user to choose, not to find the music somewhere else.
    CHECK(log.count(MP_EVENT_DEVICE_CHANGED) == 1);
    CHECK(log.first_payload(MP_EVENT_DEVICE_CHANGED).first == device);
    CHECK(log.first_payload(MP_EVENT_DEVICE_CHANGED).second == 0); // 0 = offered, 1 = moved to
    CHECK_FALSE(log.first_message(MP_EVENT_DEVICE_CHANGED).empty());
    CHECK(stats_of(fx.engine).output_started == 0); // still parked until the shell asks

    // And taking the offer is an ordinary set_output, which resumes where the music stopped.
    const int64_t parked_at = position_ms(fx.engine);
    mp_output_config out = output_on(device, MP_OUTPUT_SHARED);
    REQUIRE(mp_engine_set_output(fx.engine, &out) == MP_OK);
    REQUIRE(mp_engine_resume(fx.engine) == MP_OK);
    CHECK(stats_of(fx.engine).output_started == 1);
    CHECK(position_ms(fx.engine) >= parked_at - 100);
}

TEST_CASE("a device nobody is listening to coming and going is ignored", "[output][device-change]") {
    engine_fixture fx;
    event_log log;
    REQUIRE(mp_engine_set_event_callback(fx.engine, &event_log::sink, &log) == MP_OK);
    mp_track* track = play_on_the_default_device(fx.engine, "device-other");
    if (track == nullptr) {
        SKIP("no output device on this machine");
    }
    const int32_t device = default_device_index(fx.engine);
    log.clear();

    // Some other device on the machine, named by an index that is not the one being played through.
    const auto other = static_cast<uint32_t>(device + 7);
    simulate_device_notification(k_notify_disabled, other);
    simulate_device_notification(k_notify_enabled, other);
    simulate_device_notification(k_notify_fail, other);

    CHECK(log.count(MP_EVENT_DEVICE_LOST) == 0);
    CHECK(log.count(MP_EVENT_DEVICE_CHANGED) == 0);
    CHECK(stats_of(fx.engine).output_started == 1); // the music never noticed
}

TEST_CASE("following the default device follows it when it moves", "[output][device-change]") {
    engine_fixture fx;
    event_log log;
    REQUIRE(mp_engine_set_event_callback(fx.engine, &event_log::sink, &log) == MP_OK);
    // Opened as MP_DEVICE_DEFAULT: the caller asked to follow the default, not for a particular device.
    mp_track* track = play_on_the_default_device(fx.engine, "device-default");
    if (track == nullptr) {
        SKIP("no output device on this machine");
    }
    const int32_t device = default_device_index(fx.engine);
    log.clear();

    // Windows says the default output is now some other device. With one device on the machine the reopen lands
    // back on the same one, which still exercises the whole migration: free, reopen, carry the source over.
    const auto t0 = std::chrono::steady_clock::now();
    simulate_device_notification(k_notify_default_output, static_cast<uint32_t>(device + 7));
    const auto elapsed = std::chrono::steady_clock::now() - t0;

    // AC-54: within 1 s, playing, and the shell is told where the music went.
    INFO("migrated in " << std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count() << " ms");
    CHECK(elapsed < std::chrono::seconds(1));
    CHECK(stats_of(fx.engine).output_started == 1);
    CHECK(log.count(MP_EVENT_DEVICE_LOST) == 0); // a migration is not a loss
    CHECK(log.count(MP_EVENT_DEVICE_CHANGED) == 1);
    CHECK(log.first_payload(MP_EVENT_DEVICE_CHANGED).second == 1); // 1 = the music moved there
}

TEST_CASE("a caller that named a device is left alone when the default moves", "[output][device-change]") {
    engine_fixture fx;
    event_log log;
    REQUIRE(mp_engine_set_event_callback(fx.engine, &event_log::sink, &log) == MP_OK);
    const auto list = devices(fx.engine);
    const mp_device_info* device = default_device(list);
    if (device == nullptr) {
        SKIP("no output device on this machine");
    }
    // Named by index: the user chose this device, so Windows changing its mind is not their instruction.
    mp_output_config out = output_on(device->index, MP_OUTPUT_SHARED);
    REQUIRE(mp_engine_set_output(fx.engine, &out) == MP_OK);
    log.clear();

    simulate_device_notification(k_notify_default_output, static_cast<uint32_t>(device->index + 7));

    CHECK(log.count(MP_EVENT_DEVICE_CHANGED) == 0);
    CHECK(stats_of(fx.engine).output_started == 1);
}

TEST_CASE("200 simulated device changes leave the handle count where it started", "[output][device-change][soak]") {
    engine_fixture fx;
    event_log log;
    REQUIRE(mp_engine_set_event_callback(fx.engine, &event_log::sink, &log) == MP_OK);
    mp_track* track = play_on_the_default_device(fx.engine, "device-soak");
    const bool live = track != nullptr;
    const int32_t device = live ? default_device_index(fx.engine) : 0;

    // Settle first: the first notifications can still be growing thread-pool and driver handles.
    for (int i = 0; i < 20; ++i) {
        simulate_device_notification(k_notify_enabled, static_cast<uint32_t>(device + 7));
    }
    const DWORD before = process_handles();
    REQUIRE(before > 0);

    constexpr int k_changes = 200;
    for (int i = 0; i < k_changes; ++i) {
        if (live && i % 4 == 0) {
            // A real loss and recovery: the output is freed and opened again, which is where a handle would leak.
            simulate_device_notification(k_notify_fail, static_cast<uint32_t>(device));
            mp_output_config out = output_on(MP_DEVICE_DEFAULT, MP_OUTPUT_SHARED);
            REQUIRE(mp_engine_set_output(fx.engine, &out) == MP_OK);
        } else {
            simulate_device_notification(i % 2 == 0 ? k_notify_enabled : k_notify_disabled,
                                         static_cast<uint32_t>(device + 7));
        }
        log.clear();
    }

    const DWORD after = process_handles();
    INFO("handles " << before << " -> " << after << (live ? " (with a live device)" : " (no output device)"));
    // AC-55: stable, not identical - the driver and the thread pool move a few handles about under any load.
    CHECK(after <= before + 16);
    CHECK(stats_of(fx.engine).output_started == (live ? 1 : 0));
}
