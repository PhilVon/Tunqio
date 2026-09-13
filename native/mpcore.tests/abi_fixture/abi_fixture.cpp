// mpcore_abi_fixture_0_12: a caller built against the ABI 0.12 header, run against the current mpcore.dll (T-144).
//
// Every struct_size test in mpcore.tests simulates an older header by writing a smaller number into struct_size,
// but the caller and the core there are compiled from the same header, so the layouts they disagree about only
// disagree arithmetically. This binary includes nothing but frozen/0.12/mpcore.h - a byte-exact copy of the
// header at 0dd1b80, where T-140 made a smaller struct_size mean "an older header, served its prefix" - and links
// the DLL the product ships. The layout it reads is the one a real 0.12 caller was compiled with.
//
// What it asserts, and why each assertion can fail:
//   - mp_render_stats is the one 0.12 struct that has grown since (0.16 appended the adaptive-quality tail). The
//     core is handed the 0.12 size inside a buffer whose remaining bytes hold a sentinel and must leave every one
//     of them alone. The fields a 0.12 caller reads - width, height, warp, headless - must hold the values this
//     fixture created the renderer with. They sit after frames and the histogram, so a field inserted anywhere
//     before them in the current header moves them, and this goes red.
//   - the other 0.12 structs it exchanges (engine and output config, engine stats, clock, preset info) are served
//     at their own size with the sentinel intact, and the preset catalogue is read at the 0.12 stride - then a
//     preset is selected by an id read at that stride, which a wrong stride could not produce.
//   - a struct_size larger than any header ever declared is refused, so an MP_OK above is not a core that ignores
//     struct_size altogether.
//
// Usage: mpcore_abi_fixture_0_12 <preset root>. Exit 0 when every check passes.
#include "mpcore.h"

#include <cstdint>
#include <cstdio>
#include <cstring>

#include <windows.h>

namespace {

int g_failures = 0;

void check(bool ok, const char* what) {
    std::printf("  %s  %s\n", ok ? "ok  " : "FAIL", what);
    if (!ok) {
        ++g_failures;
    }
}

template <class T> uint32_t size_of() {
    return static_cast<uint32_t>(sizeof(T));
}

// Much larger than any version of any struct, filled with a sentinel. The struct under test is the first
// sizeof(T) bytes (or the first count * sizeof(T), for an array); everything after them must come back untouched.
constexpr unsigned char k_sentinel = 0xA5;
constexpr size_t k_buffer = 4096;

struct guarded {
    alignas(16) unsigned char bytes[k_buffer];

    explicit guarded(uint32_t struct_size) { reset(struct_size); }

    void reset(uint32_t struct_size) {
        std::memset(bytes, k_sentinel, sizeof bytes);
        std::memcpy(bytes, &struct_size, sizeof struct_size);
    }

    template <class T> T* as() { return reinterpret_cast<T*>(bytes); }

    template <class T> T read(size_t index = 0) const {
        T value;
        std::memcpy(&value, bytes + index * sizeof(T), sizeof(T));
        return value;
    }

    bool tail_intact(size_t from) const {
        for (size_t i = from; i < sizeof bytes; ++i) {
            if (bytes[i] != k_sentinel) {
                return false;
            }
        }
        return true;
    }
};

bool terminated_and_nonempty(const char* field, size_t capacity) {
    return field[0] != '\0' && std::memchr(field, '\0', capacity) != nullptr;
}

void print_last_error(const char* call) {
    char err[512];
    mp_last_error(err, sizeof err);
    std::printf("  %s: %s\n", call, err);
}

void check_engine() {
    std::printf("engine\n");
    mp_engine_config config{};
    config.struct_size = size_of<mp_engine_config>();
    mp_engine* engine = nullptr;
    const mp_result created = mp_engine_create(&config, &engine);
    if (created != MP_OK) {
        print_last_error("mp_engine_create");
    }
    check(created == MP_OK && engine != nullptr, "mp_engine_create takes a 0.12 mp_engine_config");
    if (engine == nullptr) {
        return;
    }

    mp_output_config output{};
    output.struct_size = size_of<mp_output_config>();
    output.device_index = MP_DEVICE_NONE;
    output.mode = MP_OUTPUT_SHARED;
    check(mp_engine_set_output(engine, &output) == MP_OK,
          "mp_engine_set_output takes a 0.12 mp_output_config (no device)");

    guarded stats{size_of<mp_engine_stats>()};
    check(mp_engine_get_stats(engine, stats.as<mp_engine_stats>()) == MP_OK &&
              stats.tail_intact(sizeof(mp_engine_stats)),
          "mp_engine_get_stats serves a 0.12 mp_engine_stats and writes nothing past it");

    guarded clock{size_of<mp_clock>()};
    check(mp_engine_get_clock(engine, clock.as<mp_clock>()) == MP_OK && clock.tail_intact(sizeof(mp_clock)),
          "mp_engine_get_clock serves a 0.12 mp_clock and writes nothing past it");

    guarded too_big{static_cast<uint32_t>(k_buffer)};
    check(mp_engine_get_stats(engine, too_big.as<mp_engine_stats>()) == MP_E_INVALID_ARG,
          "a struct_size larger than any header declared is refused");

    check(mp_engine_destroy(engine) == MP_OK, "mp_engine_destroy");
}

void check_renderer() {
    std::printf("renderer\n");
    constexpr uint32_t k_width = 64;
    constexpr uint32_t k_height = 48; // not square, so a swapped pair of fields reads wrong
    mp_renderer_config config{};
    config.struct_size = size_of<mp_renderer_config>();
    config.width = k_width;
    config.height = k_height;
    config.scale_x = 1.0f;
    config.scale_y = 1.0f;
    config.force_warp = 1;
    config.vsync = 0;
    config.headless = 1;
    mp_renderer* renderer = nullptr;
    const mp_result created = mp_renderer_create(nullptr, nullptr, &config, &renderer);
    if (created != MP_OK) {
        print_last_error("mp_renderer_create");
    }
    check(created == MP_OK && renderer != nullptr, "mp_renderer_create takes a 0.12 mp_renderer_config");
    if (renderer == nullptr) {
        return;
    }

    // Wait for a drawn frame, so the stats are a live renderer's. The loop ends on a frame, on a failed call, or
    // after five seconds - never only on the outcome it hopes for.
    guarded stats{size_of<mp_render_stats>()};
    mp_result got = MP_OK;
    mp_render_stats read{};
    for (int attempt = 0; attempt < 100; ++attempt) {
        stats.reset(size_of<mp_render_stats>());
        got = mp_renderer_get_stats(renderer, stats.as<mp_render_stats>());
        if (got != MP_OK) {
            break;
        }
        read = stats.read<mp_render_stats>();
        if (read.frames > 0) {
            break;
        }
        Sleep(50);
    }
    std::printf("  0.12 mp_render_stats is %u bytes; frames %llu, %ux%u, warp %u, headless %u, adapter \"%.127s\"\n",
                size_of<mp_render_stats>(), static_cast<unsigned long long>(read.frames), read.width, read.height,
                read.warp, read.headless, read.adapter);
    check(got == MP_OK, "mp_renderer_get_stats accepts the 0.12 mp_render_stats, which 0.16 grew");
    check(stats.tail_intact(sizeof(mp_render_stats)),
          "and writes nothing past the 0.12 struct (the 0.16 tail stays out)");
    check(read.struct_size == size_of<mp_render_stats>(), "struct_size is left as the caller set it");
    check(read.frames > 0, "the renderer drew a frame within five seconds");
    check(read.width == k_width && read.height == k_height, "width and height read back at their 0.12 offsets");
    check(read.warp == 1 && read.headless == 1 && read.device_lost == 0,
          "warp, headless and device_lost read back at their 0.12 offsets");
    check(terminated_and_nonempty(read.adapter, sizeof read.adapter), "adapter is a terminated, non-empty name");

    uint32_t count = 0;
    check(mp_renderer_enum_presets(renderer, nullptr, &count) == MP_OK && count >= 2,
          "the catalogue holds the built-in preset and at least one from the preset root");
    const size_t capacity = k_buffer / sizeof(mp_preset_info) - 1;
    uint32_t written = count < capacity ? count : static_cast<uint32_t>(capacity);
    guarded presets{size_of<mp_preset_info>()};
    const mp_result listed = mp_renderer_enum_presets(renderer, presets.as<mp_preset_info>(), &written);
    check(listed == MP_OK && written >= 2, "mp_renderer_enum_presets writes at the 0.12 element size");
    check(presets.tail_intact(written * sizeof(mp_preset_info)), "and writes nothing past the last 0.12 element");
    bool names_ok = listed == MP_OK && written >= 2;
    for (uint32_t i = 0; names_ok && i < written; ++i) {
        const mp_preset_info p = presets.read<mp_preset_info>(i);
        names_ok = terminated_and_nonempty(p.id, sizeof p.id) && std::memchr(p.name, '\0', sizeof p.name) != nullptr;
    }
    check(names_ok, "every element has a terminated id and name where a 0.12 caller looks for them");
    if (names_ok) {
        const mp_preset_info second = presets.read<mp_preset_info>(1);
        const mp_result selected = mp_renderer_set_preset(renderer, second.id);
        if (selected != MP_OK) {
            print_last_error("mp_renderer_set_preset");
        }
        std::printf("  selecting \"%.63s\"\n", second.id);
        check(selected == MP_OK, "an id read at the 0.12 stride names a preset the core has");
    }

    check(mp_renderer_destroy(renderer) == MP_OK, "mp_renderer_destroy");
}

} // namespace

int main(int argc, char** argv) {
    if (argc < 2) {
        std::printf("usage: mpcore_abi_fixture_0_12 <preset root>\n");
        return 2;
    }
    if (!SetEnvironmentVariableA("MPCORE_PRESET_ROOT", argv[1])) {
        std::printf("could not set MPCORE_PRESET_ROOT\n");
        return 2;
    }

    const uint32_t version = mpcore_abi_version();
    std::printf("built against ABI %u.%u; loaded mpcore %s, ABI %u.%u\n", MP_ABI_MAJOR, MP_ABI_MINOR, mp_version(),
                version >> 16, version & 0xFFFFu);
    check((version >> 16) == MP_ABI_MAJOR && (version & 0xFFFFu) >= MP_ABI_MINOR,
          "the core has this header's major and at least its minor");

    check_engine();
    check_renderer();

    std::printf(g_failures == 0 ? "PASS: a caller built against ABI 0.12 is served what its header declares.\n"
                                : "FAIL: %d check(s) failed.\n",
                g_failures);
    return g_failures == 0 ? 0 : 1;
}
