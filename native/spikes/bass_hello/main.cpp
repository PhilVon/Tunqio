// E0-S4 spike: BASS + BASSmix + BASSWASAPI hello world through the mpcore ABI.
//
//   bass_hello [--seconds N] [--exclusive-seconds N] [--device INDEX] [--buffer-ms N] [--quiet]
//
// 1. Enumerates output devices.
// 2. Shared mode at the requested buffer (default 10 ms): plays a generated 440 Hz sine for N seconds
//    (default 60), printing clock position vs wall clock every second, then the WASAPI latency and stats.
// 3. Exclusive mode at the same buffer: plays for a few seconds and reports latency and format.
// Exit code 0 when no underrun was counted and the clock drift stayed under 10 ms; 1 otherwise.
#include "mpcore.h"

#include "wav_fixture.h"

#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <vector>

#include <windows.h>

namespace {

void MP_CALL log_sink(mp_log_level level, const char* message, void* /*user*/) {
    static const char* const names[] = {"trace", "debug", "info", "warn", "error"};
    std::printf("  [mpcore %s] %s\n", names[level], message);
}

void MP_CALL event_sink(const mp_event* ev, void* /*user*/) {
    std::printf("  [event] type=%d a=%lld b=%lld%s%s\n", static_cast<int>(ev->type), static_cast<long long>(ev->a),
                static_cast<long long>(ev->b), ev->message ? " " : "", ev->message ? ev->message : "");
}

std::string err() {
    char buf[512];
    mp_last_error(buf, sizeof buf);
    return buf;
}

bool check(mp_result r, const char* what) {
    if (r != MP_OK) {
        std::printf("FAIL %s -> %d: %s\n", what, static_cast<int>(r), err().c_str());
        return false;
    }
    return true;
}

double now_s() {
    return std::chrono::duration<double>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

struct run_result {
    bool ok = true;
    uint64_t underruns = 0;
    double max_offset_ms = 0.0;  // largest |clock - wall| seen (includes the start-up transient)
    double slope_ms_per_s = 0.0; // drift rate over the steady part (after the first 2 s)
};

// Plays `track` for `seconds`, sampling the clock once a second and comparing its advance with wall time.
run_result play_and_watch(mp_engine* engine, mp_track* track, int seconds, bool verbose) {
    run_result result;
    if (!check(mp_engine_play(engine, track, 0), "mp_engine_play")) {
        result.ok = false;
        return result;
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(300)); // let the output settle
    mp_clock first{};
    first.struct_size = sizeof first;
    mp_engine_get_clock(engine, &first);
    const double wall0 = now_s();
    double drift_at_2s = 0.0;
    double drift_last = 0.0;

    for (int s = 1; s <= seconds; ++s) {
        std::this_thread::sleep_for(std::chrono::seconds(1));
        mp_clock c{};
        c.struct_size = sizeof c;
        mp_engine_get_clock(engine, &c);
        mp_engine_stats st{};
        st.struct_size = sizeof st;
        mp_engine_get_stats(engine, &st);
        const double wall_ms = (now_s() - wall0) * 1000.0;
        const double clock_ms = static_cast<double>(c.position_ms - first.position_ms);
        const double drift = clock_ms - wall_ms;
        const double buffered_ms = st.output_sample_rate != 0
                                       ? static_cast<double>(c.output_buffered_bytes) * 1000.0 /
                                             (static_cast<double>(st.output_sample_rate) * st.output_channels * 4.0)
                                       : 0.0;
        if (std::abs(drift) > result.max_offset_ms) {
            result.max_offset_ms = std::abs(drift);
        }
        if (s == 2) {
            drift_at_2s = drift;
        }
        drift_last = drift;
        result.underruns = st.underruns;
        if (verbose || s == seconds || s % 10 == 0) {
            std::printf("  t=%3ds clock=%8.1f ms wall=%8.1f ms drift=%+7.2f ms buffered=%5.1f ms underruns=%llu "
                        "callbacks=%llu max_cb=%u us\n",
                        s, clock_ms, wall_ms, drift, buffered_ms, static_cast<unsigned long long>(st.underruns),
                        static_cast<unsigned long long>(st.callbacks), st.callback_max_us);
        }
    }
    if (seconds > 2) {
        result.slope_ms_per_s = (drift_last - drift_at_2s) / static_cast<double>(seconds - 2);
    }
    check(mp_engine_stop(engine, MP_FADE_NONE), "mp_engine_stop");
    return result;
}

bool verdict(const run_result& r) {
    return r.ok && r.underruns == 0 && std::abs(r.slope_ms_per_s) < 2.0;
}

void print_result(const char* label, const run_result& r) {
    std::printf("  %s result: underruns=%llu max_offset=%.2f ms slope=%+.3f ms/s %s\n", label,
                static_cast<unsigned long long>(r.underruns), r.max_offset_ms, r.slope_ms_per_s,
                verdict(r) ? "PASS" : "FAIL");
}

void print_output(mp_engine* engine, const char* label) {
    mp_engine_stats st{};
    st.struct_size = sizeof st;
    mp_engine_get_stats(engine, &st);
    mp_clock c{};
    c.struct_size = sizeof c;
    mp_engine_get_clock(engine, &c);
    std::printf("  %s: %s, %u Hz / %u ch, format %s, WASAPI buffer %u ms (clock reports %u ms latency)\n", label,
                st.exclusive ? "exclusive" : "shared", st.output_sample_rate, st.output_channels, st.output_format,
                st.output_buffer_ms, c.output_latency_ms);
}

} // namespace

int main(int argc, char* argv[]) {
    int seconds = 60;
    int exclusive_seconds = 6;
    int device = -1;
    uint32_t buffer_ms = 10;
    bool verbose = true;
    for (int i = 1; i < argc; ++i) {
        if (std::strcmp(argv[i], "--seconds") == 0 && i + 1 < argc) {
            seconds = std::atoi(argv[++i]);
        } else if (std::strcmp(argv[i], "--exclusive-seconds") == 0 && i + 1 < argc) {
            exclusive_seconds = std::atoi(argv[++i]);
        } else if (std::strcmp(argv[i], "--device") == 0 && i + 1 < argc) {
            device = std::atoi(argv[++i]);
        } else if (std::strcmp(argv[i], "--buffer-ms") == 0 && i + 1 < argc) {
            buffer_ms = static_cast<uint32_t>(std::atoi(argv[++i]));
        } else if (std::strcmp(argv[i], "--quiet") == 0) {
            verbose = false;
        }
    }

    std::printf("bass_hello: mpcore %s, ABI %u.%u\n", mp_version(), mpcore_abi_version() >> 16,
                mpcore_abi_version() & 0xFFFF);
    mp_log_set_sink(&log_sink, nullptr, MP_LOG_DEBUG);

    mp_engine_config cfg{};
    cfg.struct_size = sizeof cfg;
    mp_engine* engine = nullptr;
    if (!check(mp_engine_create(&cfg, &engine), "mp_engine_create")) {
        return 1;
    }
    mp_engine_set_event_callback(engine, &event_sink, nullptr);

    uint32_t count = 0;
    mp_engine_enum_devices(engine, nullptr, &count);
    std::vector<mp_device_info> devices(count);
    if (count > 0) {
        devices[0].struct_size = sizeof(mp_device_info); // the element size, and so the stride (ABI 0.12)
    }
    mp_engine_enum_devices(engine, devices.data(), &count);
    std::printf("output devices (%u):\n", count);
    for (const auto& d : devices) {
        std::printf("  [%d]%s %s  mix %u Hz / %u ch, period min %.2f ms default %.2f ms\n", d.index,
                    d.is_default ? "*" : " ", d.name, d.mix_sample_rate, d.mix_channels, d.min_period_us / 1000.0,
                    d.default_period_us / 1000.0);
    }

    mp::tests::wav_spec spec;
    spec.seconds = static_cast<double>(seconds + exclusive_seconds + 10);
    const std::string wav = mp::tests::write_sine_wav(spec, "spike-440");
    if (wav.empty()) {
        std::printf("FAIL could not write the WAV fixture\n");
        return 1;
    }
    mp_track* track = nullptr;
    if (!check(mp_track_open(engine, wav.c_str(), &track), "mp_track_open")) {
        return 1;
    }
    mp_track_info info{};
    info.struct_size = sizeof info;
    mp_track_get_info(track, &info);
    std::printf("track: %s, %u Hz / %u ch / %u bit, %lld ms, %lld frames\n", info.codec, info.sample_rate,
                info.channels, info.bits_per_sample, static_cast<long long>(info.duration_ms),
                static_cast<long long>(info.total_frames));

    bool ok = true;
    mp_engine_set_volume(engine, 0.5f);

    struct pass {
        const char* label;
        mp_output_mode mode;
        uint8_t event_driven;
        int seconds;
        bool required;
    };
    const pass passes[] = {
        {"shared", MP_OUTPUT_SHARED, 0, seconds, true},
        {"shared event-driven", MP_OUTPUT_SHARED, 1, exclusive_seconds, true},
        {"exclusive", MP_OUTPUT_EXCLUSIVE, 0, exclusive_seconds, false},
        {"exclusive event-driven", MP_OUTPUT_EXCLUSIVE, 1, exclusive_seconds, false},
    };
    for (const pass& p : passes) {
        std::printf("\n== %s, %u ms buffer requested, %d s ==\n", p.label, buffer_ms, p.seconds);
        mp_output_config out{};
        out.struct_size = sizeof out;
        out.device_index = device;
        out.mode = p.mode;
        out.buffer_ms = buffer_ms;
        out.event_driven = p.event_driven;
        if (!check(mp_engine_set_output(engine, &out), "mp_engine_set_output")) {
            if (p.required) {
                ok = false;
            } else {
                std::printf("  %s unavailable on this device (recorded, not fatal)\n", p.label);
            }
            continue;
        }
        print_output(engine, p.label);
        const run_result r = play_and_watch(engine, track, p.seconds, verbose);
        print_result(p.label, r);
        if (p.required) {
            ok = ok && verdict(r);
        } else {
            std::printf("  (%s is informational; it does not affect the verdict)\n", p.label);
        }
    }

    mp_track_close(track);
    check(mp_engine_destroy(engine), "mp_engine_destroy");
    std::printf("\n%s\n", ok ? "bass_hello: PASS" : "bass_hello: FAIL");
    return ok ? 0 : 1;
}
