// The extern "C" surface of mpcore.dll. Everything here is thin: validate, forward, convert errors.
#include "mpcore.h"

#include "abi/guard.h"
#include "abi/last_error.h"
#include "abi/struct_size.h"
#include "audio/bass_engine.h"
#include "common/log.h"
#include "common/version.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <memory>

namespace mp::abi::detail {

void record_seh(unsigned long code) noexcept {
    char text[64];
    std::snprintf(text, sizeof text, "structured exception 0x%08lX", code);
    set_last_error(text);
}

} // namespace mp::abi::detail

namespace {

using mp::audio::engine;
using mp::audio::track;

// The opaque handles are the C++ objects themselves.
engine* as_engine(mp_engine* e) {
    return reinterpret_cast<engine*>(e);
}
track* as_track(mp_track* t) {
    return reinterpret_cast<track*>(t);
}

using mp::abi::in_struct;
using mp::abi::out_array;
using mp::abi::out_struct;

mp_result invalid(const char* what) {
    mp::abi::set_last_error(what);
    return MP_E_INVALID_ARG;
}

} // namespace

extern "C" {

// ---- version and errors ----

MP_API uint32_t MP_CALL mpcore_abi_version(void) {
    return (MP_ABI_MAJOR << 16) | MP_ABI_MINOR;
}

MP_API const char* MP_CALL mp_version(void) {
    return mp::version_string();
}

MP_API mp_result MP_CALL mp_last_error(char* buf, size_t len) {
    if (buf == nullptr || len == 0) {
        return MP_E_INVALID_ARG;
    }
    // Deliberately not guarded: this is how the caller reads the error the guard recorded.
    const std::string_view msg = mp::abi::last_error();
    const size_t n = std::min(msg.size(), len - 1);
    std::memcpy(buf, msg.data(), n);
    buf[n] = '\0';
    return MP_OK;
}

// ---- logging ----

MP_API mp_result MP_CALL mp_log_set_sink(mp_log_cb sink, void* user, mp_log_level min_level) {
    return mp::abi::guard([&]() -> mp_result {
        mp::log_set_sink(sink, user, min_level);
        return MP_OK;
    });
}

// ---- engine ----

MP_API mp_result MP_CALL mp_engine_create(const mp_engine_config* config, mp_engine** out_engine) {
    return mp::abi::guard([&]() -> mp_result {
        if (out_engine == nullptr) {
            return invalid("mp_engine_create: NULL out_engine");
        }
        *out_engine = nullptr;
        return in_struct(config, "mp_engine_create", [&](const mp_engine_config& cfg) {
            std::unique_ptr<engine> e;
            const mp_result r = engine::create(cfg, e);
            if (r == MP_OK) {
                *out_engine = reinterpret_cast<mp_engine*>(e.release());
            }
            return r;
        });
    });
}

MP_API mp_result MP_CALL mp_engine_destroy(mp_engine* e) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr) {
            return invalid("mp_engine_destroy: NULL engine");
        }
        delete as_engine(e);
        return MP_OK;
    });
}

MP_API mp_result MP_CALL mp_engine_set_output(mp_engine* e, const mp_output_config* config) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr) {
            return invalid("mp_engine_set_output: NULL engine");
        }
        return in_struct(config, "mp_engine_set_output",
                         [&](const mp_output_config& cfg) { return as_engine(e)->set_output(cfg); });
    });
}

MP_API mp_result MP_CALL mp_engine_enum_devices(mp_engine* e, mp_device_info* out, uint32_t* count) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr || count == nullptr) {
            return invalid("mp_engine_enum_devices: NULL engine or count");
        }
        return out_array(out, count, "mp_engine_enum_devices",
                         [&](mp_device_info* buffer, uint32_t* n) { return as_engine(e)->enum_devices(buffer, n); });
    });
}

MP_API mp_result MP_CALL mp_engine_set_event_callback(mp_engine* e, mp_event_cb callback, void* user) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr) {
            return invalid("mp_engine_set_event_callback: NULL engine");
        }
        as_engine(e)->set_event_callback(callback, user);
        return MP_OK;
    });
}

// ---- tracks ----

MP_API mp_result MP_CALL mp_track_open(mp_engine* e, const char* utf8_path, mp_track** out_track) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr || utf8_path == nullptr || out_track == nullptr) {
            return invalid("mp_track_open: NULL argument");
        }
        *out_track = nullptr;
        track* t = nullptr;
        const mp_result r = as_engine(e)->open_track(utf8_path, t);
        if (r == MP_OK) {
            *out_track = reinterpret_cast<mp_track*>(t);
        }
        return r;
    });
}

MP_API mp_result MP_CALL mp_track_close(mp_track* t) {
    return mp::abi::guard([&]() -> mp_result {
        if (t == nullptr || as_track(t)->owner == nullptr) {
            return invalid("mp_track_close: NULL or orphaned track");
        }
        return as_track(t)->owner->close_track(as_track(t));
    });
}

MP_API mp_result MP_CALL mp_track_get_info(mp_track* t, mp_track_info* out_info) {
    return mp::abi::guard([&]() -> mp_result {
        if (t == nullptr) {
            return invalid("mp_track_get_info: NULL track");
        }
        return out_struct(out_info, "mp_track_get_info", [&](mp_track_info& info) {
            info = as_track(t)->info;
            return MP_OK;
        });
    });
}

// ---- transport ----

MP_API mp_result MP_CALL mp_engine_play(mp_engine* e, mp_track* t, int64_t start_ms) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr || t == nullptr) {
            return invalid("mp_engine_play: NULL engine or track");
        }
        if (!as_engine(e)->owns(as_track(t))) {
            return invalid("mp_engine_play: track does not belong to this engine");
        }
        return as_engine(e)->play(as_track(t), start_ms < 0 ? 0 : start_ms);
    });
}

MP_API mp_result MP_CALL mp_engine_preload_next(mp_engine* e, mp_track* next) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr) {
            return invalid("mp_engine_preload_next: NULL engine");
        }
        if (next != nullptr && !as_engine(e)->owns(as_track(next))) {
            return invalid("mp_engine_preload_next: track does not belong to this engine");
        }
        return as_engine(e)->preload_next(as_track(next), MP_JOIN_GAPLESS);
    });
}

MP_API mp_result MP_CALL mp_engine_preload_next_ex(mp_engine* e, mp_track* next, mp_join_mode mode) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr) {
            return invalid("mp_engine_preload_next_ex: NULL engine");
        }
        if (mode != MP_JOIN_GAPLESS && mode != MP_JOIN_CROSSFADE) {
            return invalid("mp_engine_preload_next_ex: unknown join mode");
        }
        if (next != nullptr && !as_engine(e)->owns(as_track(next))) {
            return invalid("mp_engine_preload_next_ex: track does not belong to this engine");
        }
        return as_engine(e)->preload_next(as_track(next), mode);
    });
}

MP_API mp_result MP_CALL mp_engine_pause(mp_engine* e) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr) {
            return invalid("mp_engine_pause: NULL engine");
        }
        return as_engine(e)->pause();
    });
}

MP_API mp_result MP_CALL mp_engine_resume(mp_engine* e) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr) {
            return invalid("mp_engine_resume: NULL engine");
        }
        return as_engine(e)->resume();
    });
}

MP_API mp_result MP_CALL mp_engine_stop(mp_engine* e, mp_fade_mode fade) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr) {
            return invalid("mp_engine_stop: NULL engine");
        }
        if (fade != MP_FADE_NONE && fade != MP_FADE_GUARD) {
            return invalid("mp_engine_stop: unknown fade mode");
        }
        return as_engine(e)->stop(fade);
    });
}

MP_API mp_result MP_CALL mp_engine_seek(mp_engine* e, int64_t position_ms) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr) {
            return invalid("mp_engine_seek: NULL engine");
        }
        return as_engine(e)->seek(position_ms < 0 ? 0 : position_ms);
    });
}

MP_API mp_result MP_CALL mp_engine_set_volume(mp_engine* e, float linear) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr) {
            return invalid("mp_engine_set_volume: NULL engine");
        }
        as_engine(e)->set_volume(linear);
        return MP_OK;
    });
}

MP_API mp_result MP_CALL mp_track_set_replaygain(mp_track* t, float gain_db, float peak) {
    return mp::abi::guard([&]() -> mp_result {
        if (t == nullptr) {
            return invalid("mp_track_set_replaygain: NULL track");
        }
        if (!std::isfinite(gain_db) || !std::isfinite(peak)) {
            return invalid("mp_track_set_replaygain: gain_db and peak must be finite");
        }
        auto* track = as_track(t);
        if (track->owner == nullptr || !track->owner->owns(track)) {
            return invalid("mp_track_set_replaygain: unknown track handle");
        }
        return track->owner->set_replaygain(track, gain_db, peak);
    });
}

MP_API mp_result MP_CALL mp_engine_set_crossfade(mp_engine* e, uint32_t ms) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr) {
            return invalid("mp_engine_set_crossfade: NULL engine");
        }
        return as_engine(e)->set_crossfade(ms);
    });
}

MP_API mp_result MP_CALL mp_engine_get_clock(mp_engine* e, mp_clock* out_clock) {
    // Not guarded with SEH on purpose: this is polled at UI rate and must stay cheap. It only reads.
    if (e == nullptr) {
        return invalid("mp_engine_get_clock: NULL engine");
    }
    return out_struct(out_clock, "mp_engine_get_clock",
                      [&](mp_clock& clock) { return as_engine(e)->get_clock(clock); });
}

MP_API mp_result MP_CALL mp_engine_get_stats(mp_engine* e, mp_engine_stats* out_stats) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr) {
            return invalid("mp_engine_get_stats: NULL engine");
        }
        return out_struct(out_stats, "mp_engine_get_stats",
                          [&](mp_engine_stats& stats) { return as_engine(e)->get_stats(stats); });
    });
}

MP_API mp_result MP_CALL mp_engine_render(mp_engine* e, float* out_interleaved, uint32_t frames) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr || out_interleaved == nullptr) {
            return invalid("mp_engine_render: NULL engine or buffer");
        }
        return as_engine(e)->render(out_interleaved, frames);
    });
}

MP_API mp_result MP_CALL mp_preview_start(mp_engine* e, mp_track* t, float gain_db) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr || t == nullptr) {
            return invalid("mp_preview_start: NULL engine or track");
        }
        if (!as_engine(e)->owns(as_track(t))) {
            return invalid("mp_preview_start: track does not belong to this engine");
        }
        return as_engine(e)->preview_start(as_track(t), gain_db);
    });
}

MP_API mp_result MP_CALL mp_preview_stop(mp_engine* e) {
    return mp::abi::guard([&]() -> mp_result {
        if (e == nullptr) {
            return invalid("mp_preview_stop: NULL engine");
        }
        return as_engine(e)->preview_stop();
    });
}

// ---- analysis ----

MP_API mp_result MP_CALL mp_analysis_try_get_latest(mp_engine* e, mp_analysis_frame* out_frame) {
    // Unguarded for the same reason as mp_engine_get_clock: the theming poll runs at 30 Hz and the renderer will
    // ask per presented frame, and this only copies out of a published buffer.
    if (e == nullptr) {
        return invalid("mp_analysis_try_get_latest: NULL engine");
    }
    // A caller at the current size is filled in place, as it always was: the cost this rule adds to the path the
    // renderer polls per presented frame is one comparison, and the frame is never copied twice.
    return out_struct(out_frame, "mp_analysis_try_get_latest",
                      [&](mp_analysis_frame& frame) { return as_engine(e)->get_analysis_frame(frame); });
}

} // extern "C"
