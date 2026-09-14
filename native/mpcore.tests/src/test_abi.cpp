// Contract tests for the E0-S1 ABI surface: version, last-error, the export guard, and (T-140) the struct_size
// rule that decides which callers an export will serve.
#include "mpcore.h"

#include "abi/guard.h"
#include "abi/last_error.h"
#include "abi/struct_size.h"
#include "common/version.h"
#include "offline_engine.h"

#include <algorithm>
#include <catch2/catch_amalgamated.hpp>
#include <chrono>
#include <cstddef>
#include <cstring>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

TEST_CASE("mpcore_abi_version packs major and minor", "[abi]") {
    const uint32_t v = mpcore_abi_version();
    CHECK((v >> 16) == MP_ABI_MAJOR);
    CHECK((v & 0xFFFFu) == MP_ABI_MINOR);
}

TEST_CASE("mp_version is the product version from the build", "[abi]") {
    const char* v = mp_version();
    REQUIRE(v != nullptr);
    CHECK(std::string{v} == std::string{mp::version_string()});
    // major.minor.patch, digits and dots only
    CHECK(std::string{v}.find_first_not_of("0123456789.") == std::string::npos);
    CHECK(std::count(v, v + std::strlen(v), '.') == 2);
}

TEST_CASE("mp_last_error rejects a null or empty buffer", "[abi]") {
    char buf[8];
    CHECK(mp_last_error(nullptr, sizeof buf) == MP_E_INVALID_ARG);
    CHECK(mp_last_error(buf, 0) == MP_E_INVALID_ARG);
}

TEST_CASE("mp_last_error is empty when nothing failed and truncates to fit", "[abi]") {
    mp::abi::clear_last_error();
    char buf[8] = "junk";
    REQUIRE(mp_last_error(buf, sizeof buf) == MP_OK);
    CHECK(std::string{buf}.empty());

    mp::abi::set_last_error("a fairly long message");
    REQUIRE(mp_last_error(buf, sizeof buf) == MP_OK);
    CHECK(std::string{buf} == "a fairl"); // 7 chars + NUL
    mp::abi::clear_last_error();
}

TEST_CASE("last error is per thread", "[abi]") {
    mp::abi::set_last_error("main");
    std::string seen_on_worker;
    std::thread worker{[&] {
        seen_on_worker = std::string{mp::abi::last_error()};
        mp::abi::set_last_error("worker");
    }};
    worker.join();
    CHECK(seen_on_worker.empty());
    CHECK(mp::abi::last_error() == "main");
    mp::abi::clear_last_error();
}

TEST_CASE("guard converts a C++ exception to MP_E_INTERNAL with the message", "[abi]") {
    const mp_result r = mp::abi::guard([]() -> mp_result { throw std::runtime_error("boom"); });
    CHECK(r == MP_E_INTERNAL);
    CHECK(mp::abi::last_error() == "boom");
}

TEST_CASE("guard passes a normal result through and clears a stale error", "[abi]") {
    mp::abi::set_last_error("stale");
    const mp_result r = mp::abi::guard([]() -> mp_result { return MP_E_STATE; });
    CHECK(r == MP_E_STATE);
    CHECK(mp::abi::last_error().empty());
}

TEST_CASE("guard converts a structured exception to MP_E_INTERNAL", "[abi]") {
    const mp_result r = mp::abi::guard([]() -> mp_result {
        volatile int* p = nullptr;
        *p = 1; // access violation
        return MP_OK;
    });
    CHECK(r == MP_E_INTERNAL);
    CHECK(mp::abi::last_error().starts_with("structured exception 0xC0000005"));
}

// ---- the struct_size rule (T-140, ABI 0.12) ---------------------------------------------------------------
//
// The rule itself first, over the helpers every export goes through, because that is where "the bytes past the
// caller's struct_size are not the caller's struct" can be asserted byte for byte: the buffer is filled with a
// sentinel, an older header's size is put in it, and what survives the call is the test. The exports that use
// the helpers are then checked separately, which is the wiring rather than the rule.

namespace {

// Storage for a struct a short caller owns: aligned for T and filled with a sentinel byte, so anything the
// callee writes past the size it was given is visible as a byte that is no longer 0xCD.
template <typename T> struct caller_buffer {
    alignas(T) unsigned char bytes[sizeof(T)];

    explicit caller_buffer(uint32_t struct_size) {
        std::memset(bytes, 0xCD, sizeof bytes);
        as_struct()->struct_size = struct_size;
    }
    T* as_struct() { return reinterpret_cast<T*>(bytes); }
    // Whether every byte from `from` to the end of the struct is still the sentinel.
    bool tail_untouched(size_t from) const {
        for (size_t i = from; i < sizeof bytes; ++i) {
            if (bytes[i] != 0xCD) {
                return false;
            }
        }
        return true;
    }
};

constexpr uint32_t k_stats_prefix =
    static_cast<uint32_t>(offsetof(mp_engine_stats, output_channels) + sizeof(mp_engine_stats::output_channels));
constexpr uint32_t k_frame_prefix =
    static_cast<uint32_t>(offsetof(mp_analysis_frame, waveform)); // spectrum, but no waveform and no features

} // namespace

TEST_CASE("an out struct from an older header is filled to its own size and no further", "[abi][struct_size]") {
    // The fill writes the whole struct, stamping its own size as every fill in the core does; what the caller
    // sees is the prefix its struct_size covers, and the size it is told is the one it was served.
    const auto fill = [](mp_engine_stats& stats) {
        std::memset(&stats, 0xAB, sizeof stats);
        stats.struct_size = sizeof stats;
        return MP_OK;
    };

    caller_buffer<mp_engine_stats> older{k_stats_prefix};
    REQUIRE(mp::abi::out_struct(older.as_struct(), "test", fill) == MP_OK);
    // The size it is told is its own, not what this build knows: it is the number of bytes that were filled.
    CHECK(older.as_struct()->struct_size == k_stats_prefix);
    for (size_t i = sizeof(uint32_t); i < k_stats_prefix; ++i) {
        INFO("byte " << i << " of the prefix");
        REQUIRE(older.bytes[i] == 0xAB);
    }
    CHECK(older.tail_untouched(k_stats_prefix));

    // The same for the largest struct on the surface, whose tail is 6 KB of spectrum, waveform and features.
    caller_buffer<mp_analysis_frame> old_frame{k_frame_prefix};
    REQUIRE(mp::abi::out_struct(old_frame.as_struct(), "test", [](mp_analysis_frame& f) {
                std::memset(&f, 0xAB, sizeof f);
                f.struct_size = sizeof f;
                return MP_OK;
            }) == MP_OK);
    CHECK(old_frame.as_struct()->struct_size == k_frame_prefix);
    CHECK(old_frame.bytes[k_frame_prefix - 1] == 0xAB);
    CHECK(old_frame.tail_untouched(k_frame_prefix));

    // A caller at the current size is served exactly as it was: the whole struct, filled in place.
    caller_buffer<mp_engine_stats> current{sizeof(mp_engine_stats)};
    REQUIRE(mp::abi::out_struct(current.as_struct(), "test", fill) == MP_OK);
    CHECK(current.as_struct()->struct_size == sizeof(mp_engine_stats));
    CHECK(current.bytes[sizeof(mp_engine_stats) - 1] == 0xAB);
}

TEST_CASE("an in struct from an older header is read to its own size and no further", "[abi][struct_size]") {
    // Everything past device_index is sentinel: a callee that read it would see 0xCDCDCDCD, not the zero the
    // fields the caller's header did not have are documented to take.
    caller_buffer<mp_output_config> older{
        static_cast<uint32_t>(offsetof(mp_output_config, device_index) + sizeof(mp_output_config::device_index))};
    older.as_struct()->device_index = MP_DEVICE_NONE;

    mp_output_config seen{};
    REQUIRE(mp::abi::in_struct(older.as_struct(), "test", [&](const mp_output_config& cfg) {
                seen = cfg;
                return MP_OK;
            }) == MP_OK);
    CHECK(seen.device_index == MP_DEVICE_NONE);
    CHECK(seen.mode == MP_OUTPUT_SHARED);
    CHECK(seen.buffer_ms == 0);
    CHECK(seen.event_driven == 0);
}

TEST_CASE("a struct_size no header ever had is refused and says why", "[abi][struct_size]") {
    const auto fill = [](mp_engine_stats&) { return MP_OK; };

    // Larger than this build: the caller wants fields this DLL cannot fill.
    caller_buffer<mp_engine_stats> newer{sizeof(mp_engine_stats) + 8};
    CHECK(mp::abi::out_struct(newer.as_struct(), "mp_engine_get_stats", fill) == MP_E_INVALID_ARG);
    CHECK(std::string{mp::abi::last_error()}.find("newer mpcore.h") != std::string::npos);
    CHECK(newer.tail_untouched(sizeof(uint32_t)));

    // Smaller than the first meaningful field: no header ever looked like this.
    caller_buffer<mp_engine_stats> stunted{sizeof(uint32_t)};
    CHECK(mp::abi::out_struct(stunted.as_struct(), "mp_engine_get_stats", fill) == MP_E_INVALID_ARG);
    CHECK(std::string{mp::abi::last_error()}.find("below") != std::string::npos);

    CHECK(mp::abi::out_struct<mp_engine_stats>(nullptr, "mp_engine_get_stats", fill) == MP_E_INVALID_ARG);
    CHECK(std::string{mp::abi::last_error()}.find("mp_engine_stats") != std::string::npos);
}

TEST_CASE("an enumeration writes at the caller's element size", "[abi][struct_size]") {
    // mp_preset_info without its name: the smallest prefix an older header could have had.
    constexpr uint32_t k_element =
        static_cast<uint32_t>(offsetof(mp_preset_info, id) + sizeof(mp_preset_info::id)); // no `name`
    static_assert(k_element < sizeof(mp_preset_info), "the point is an element smaller than this build's");
    constexpr uint32_t k_items = 3;

    // Three of the caller's elements, which is fewer bytes than three of this build's: writing at the wrong
    // stride runs off the end, and under ASan that is the failure rather than a wrong value.
    std::vector<uint32_t> storage(k_items * k_element / sizeof(uint32_t) + 2, 0xCDCDCDCDu);
    auto* out = reinterpret_cast<mp_preset_info*>(storage.data());
    out->struct_size = k_element;

    // The two-call protocol an enumeration implements, with ids the assertions can name.
    const auto fill = [k_items](mp_preset_info* buffer, uint32_t* count) {
        if (buffer == nullptr) {
            *count = k_items;
            return MP_OK;
        }
        const uint32_t n = std::min(*count, k_items);
        for (uint32_t i = 0; i < n; ++i) {
            std::memset(&buffer[i], 0, sizeof(mp_preset_info));
            buffer[i].struct_size = sizeof(mp_preset_info);
            buffer[i].id[0] = static_cast<char>('a' + i);
            std::memcpy(buffer[i].name, "a name this caller has no room for", 35);
        }
        *count = n;
        return MP_OK;
    };

    uint32_t count = k_items;
    REQUIRE(mp::abi::out_array(out, &count, "mp_renderer_enum_presets", fill) == MP_OK);
    CHECK(count == k_items);
    const auto* bytes = reinterpret_cast<const unsigned char*>(storage.data());
    for (uint32_t i = 0; i < k_items; ++i) {
        const auto* element = reinterpret_cast<const mp_preset_info*>(bytes + static_cast<size_t>(i) * k_element);
        INFO("element " << i);
        CHECK(element->struct_size == k_element); // what it carries, not what this build knows
        CHECK(element->id[0] == static_cast<char>('a' + i));
    }
    for (size_t i = static_cast<size_t>(k_items) * k_element; i < storage.size() * sizeof(uint32_t); ++i) {
        INFO("byte " << i << ", past the last element");
        REQUIRE(bytes[i] == 0xCD);
    }
}

TEST_CASE("the exports serve an older caller and refuse a newer one", "[abi][struct_size]") {
    mp::tests::offline_engine fx;

    // Out: mp_engine_get_stats through the export, against a full-size read of the same engine.
    mp_engine_stats full{};
    full.struct_size = sizeof full;
    REQUIRE(mp_engine_get_stats(fx.engine, &full) == MP_OK);

    caller_buffer<mp_engine_stats> older{k_stats_prefix};
    REQUIRE(mp_engine_get_stats(fx.engine, older.as_struct()) == MP_OK);
    CHECK(older.as_struct()->struct_size == k_stats_prefix);
    CHECK(older.as_struct()->output_sample_rate == full.output_sample_rate);
    CHECK(older.as_struct()->output_channels == full.output_channels);
    CHECK(older.as_struct()->output_channels == mp::tests::k_channels); // filled, not left as sentinel
    CHECK(older.tail_untouched(k_stats_prefix));

    caller_buffer<mp_engine_stats> newer{sizeof(mp_engine_stats) + 4};
    CHECK(mp_engine_get_stats(fx.engine, newer.as_struct()) == MP_E_INVALID_ARG);
    CHECK(mp::tests::last_error().find("mp_engine_get_stats") != std::string::npos);
    CHECK(newer.tail_untouched(sizeof(uint32_t)));

    // In: an mp_output_config that stops after device_index still opens MP_DEVICE_NONE, and the sentinel bytes
    // past it are not read as an exclusive mode or a buffer length.
    caller_buffer<mp_output_config> old_config{
        static_cast<uint32_t>(offsetof(mp_output_config, device_index) + sizeof(mp_output_config::device_index))};
    old_config.as_struct()->device_index = MP_DEVICE_NONE;
    REQUIRE(mp_engine_set_output(fx.engine, old_config.as_struct()) == MP_OK);
    mp_engine_stats after{};
    after.struct_size = sizeof after;
    REQUIRE(mp_engine_get_stats(fx.engine, &after) == MP_OK);
    CHECK(after.exclusive == 0);
    CHECK(fx.render(mp::tests::k_buffer_frames).size() == mp::tests::k_buffer_frames * mp::tests::k_channels);

    caller_buffer<mp_output_config> new_config{sizeof(mp_output_config) + 4};
    CHECK(mp_engine_set_output(fx.engine, new_config.as_struct()) == MP_E_INVALID_ARG);
    CHECK(mp::tests::last_error().find("mp_engine_set_output") != std::string::npos);
}

// ---- reserved bytes (T-145, Q-136) ------------------------------------------------------------------------
//
// The reserved bytes are a store for future single-byte flags, and that store works only while a core that has
// not given a byte a meaning writes zero into it. Every out struct starts as sentinel, so a byte the core skipped
// reads 0xCD rather than a zero the test put there itself. mp_latency_sample's is in test_latency.cpp, beside the
// renderer that fills it. The layout itself is not asserted here: struct_size.h's static_asserts and the ABI 0.12
// fixture own that, and T-145 moved nothing.

TEST_CASE("every reserved byte the core writes out is zero", "[abi][reserved]") {
    mp::tests::offline_engine fx;

    caller_buffer<mp_engine_stats> stats{sizeof(mp_engine_stats)};
    REQUIRE(mp_engine_get_stats(fx.engine, stats.as_struct()) == MP_OK);
    CHECK(stats.as_struct()->reserved[0] == 0);
    CHECK(stats.as_struct()->reserved[1] == 0);

    uint32_t count = 0;
    REQUIRE(mp_engine_enum_devices(fx.engine, nullptr, &count) == MP_OK);
    if (count > 0) { // a CI runner may have no output device; the stats and the frame still prove the rule
        std::vector<mp_device_info> devices(count);
        std::memset(devices.data(), 0xCD, devices.size() * sizeof(mp_device_info));
        devices[0].struct_size = sizeof(mp_device_info); // the stride
        uint32_t written = count;
        REQUIRE(mp_engine_enum_devices(fx.engine, devices.data(), &written) == MP_OK);
        for (uint32_t i = 0; i < written; ++i) {
            INFO("device " << i << " of " << written);
            CHECK(devices[i].reserved[0] == 0);
            CHECK(devices[i].reserved[1] == 0);
        }
    }

    // One hop is 512 frames, so 200 ms of the offline mixer publishes a frame (silence is still a frame).
    std::vector<float> buffer(static_cast<size_t>(mp::tests::k_buffer_frames) * mp::tests::k_channels);
    for (int i = 0; i < 20; ++i) {
        REQUIRE(mp_engine_render(fx.engine, buffer.data(), mp::tests::k_buffer_frames) == MP_OK);
    }
    caller_buffer<mp_analysis_frame> frame{sizeof(mp_analysis_frame)};
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds{2};
    while (mp_analysis_try_get_latest(fx.engine, frame.as_struct()) != MP_OK) {
        REQUIRE(std::chrono::steady_clock::now() < deadline);
        std::this_thread::sleep_for(std::chrono::milliseconds{1});
    }
    CHECK(frame.as_struct()->sequence > 0);
    CHECK(frame.as_struct()->reserved[0] == 0);
    CHECK(frame.as_struct()->reserved[1] == 0);

    // The other half of the rule, for an in struct: a reader ignores a value it does not know. A caller's nonzero
    // reserved bytes are not a reason to refuse the call.
    mp_output_config config{};
    config.struct_size = sizeof config;
    config.device_index = MP_DEVICE_NONE;
    std::memset(config.reserved, 0xFF, sizeof config.reserved);
    CHECK(mp_engine_set_output(fx.engine, &config) == MP_OK);
}
