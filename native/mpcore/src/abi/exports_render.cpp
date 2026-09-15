// The mp_renderer_* exports (ABI 0.3). Thin: validate, forward, convert errors.
// The preset three (enum/set_preset/set_param) were declared and stubbed by E0-S5 and are implemented by E4-S3;
// set_theme is implemented by E4-S6 and set_quality by E4-S7, which is the last of the stubs. No export moved,
// so the ABI minor moves for the function that is new, not for a signature that changed.
#include "mpcore.h"

#include "abi/guard.h"
#include "abi/last_error.h"
#include "abi/struct_size.h"
#include "render/renderer.h"

#include <cstdio>
#include <memory>

namespace {

using mp::render::renderer;

renderer* as_renderer(mp_renderer* r) {
    return reinterpret_cast<renderer*>(r);
}

using mp::abi::in_struct;
using mp::abi::out_array;
using mp::abi::out_struct;

mp_result invalid(const char* what) {
    mp::abi::set_last_error(what);
    return MP_E_INVALID_ARG;
}

mp_result not_implemented(const char* export_name, const char* story) {
    char text[128];
    std::snprintf(text, sizeof text, "%s is not implemented yet (lands with %s)", export_name, story);
    mp::abi::set_last_error(text);
    return MP_E_STATE;
}

} // namespace

extern "C" {

MP_API mp_result MP_CALL mp_renderer_create(mp_engine* engine, void* swap_chain_panel_native,
                                            const mp_renderer_config* config, mp_renderer** out_renderer) {
    return mp::abi::guard([&]() -> mp_result {
        if (out_renderer == nullptr) {
            return invalid("mp_renderer_create: NULL out_renderer");
        }
        *out_renderer = nullptr;
        return in_struct(config, "mp_renderer_create", [&](const mp_renderer_config& cfg) {
            std::unique_ptr<renderer> r;
            const mp_result result = renderer::create(engine, swap_chain_panel_native, cfg, r);
            if (result == MP_OK) {
                *out_renderer = reinterpret_cast<mp_renderer*>(r.release());
            }
            return result;
        });
    });
}

MP_API mp_result MP_CALL mp_renderer_destroy(mp_renderer* r) {
    return mp::abi::guard([&]() -> mp_result {
        if (r == nullptr) {
            return invalid("mp_renderer_destroy: NULL renderer");
        }
        delete as_renderer(r);
        return MP_OK;
    });
}

MP_API mp_result MP_CALL mp_renderer_resize(mp_renderer* r, uint32_t width, uint32_t height, float scale_x,
                                            float scale_y) {
    if (r == nullptr) {
        return invalid("mp_renderer_resize: NULL renderer");
    }
    as_renderer(r)->resize(width, height, scale_x, scale_y);
    return MP_OK;
}

MP_API mp_result MP_CALL mp_renderer_set_visible(mp_renderer* r, uint8_t visible) {
    if (r == nullptr) {
        return invalid("mp_renderer_set_visible: NULL renderer");
    }
    as_renderer(r)->set_visible(visible != 0);
    return MP_OK;
}

MP_API mp_result MP_CALL mp_renderer_get_stats(mp_renderer* r, mp_render_stats* out_stats) {
    if (r == nullptr) {
        return invalid("mp_renderer_get_stats: NULL renderer");
    }
    return out_struct(out_stats, "mp_renderer_get_stats", [&](mp_render_stats& stats) {
        as_renderer(r)->get_stats(stats);
        return MP_OK;
    });
}

MP_API mp_result MP_CALL mp_renderer_enum_presets(mp_renderer* r, mp_preset_info* out, uint32_t* count) {
    return mp::abi::guard([&]() -> mp_result {
        if (r == nullptr || count == nullptr) {
            return invalid("mp_renderer_enum_presets: NULL renderer or count");
        }
        return out_array(out, count, "mp_renderer_enum_presets",
                         [&](mp_preset_info* buffer, uint32_t* n) { return as_renderer(r)->enum_presets(buffer, n); });
    });
}

// T-142. Guarded because it allocates - the choice labels are packed into a std::string per parameter - and
// because a settings page calls it for every preset in the catalogue.
MP_API mp_result MP_CALL mp_renderer_enum_preset_params(mp_renderer* r, const char* utf8_preset_id,
                                                        mp_preset_param_info* out, uint32_t* count) {
    return mp::abi::guard([&]() -> mp_result {
        if (r == nullptr || utf8_preset_id == nullptr || count == nullptr) {
            return invalid("mp_renderer_enum_preset_params: NULL renderer, preset id or count");
        }
        return out_array(out, count, "mp_renderer_enum_preset_params", [&](mp_preset_param_info* buffer, uint32_t* n) {
            return as_renderer(r)->enum_preset_params(utf8_preset_id, buffer, n);
        });
    });
}

// Guarded: both of these rebuild the catalogue, which reads every manifest under both roots and allocates.
MP_API mp_result MP_CALL mp_renderer_set_user_preset_root(mp_renderer* r, const char* utf8_path) {
    return mp::abi::guard([&]() -> mp_result {
        if (r == nullptr) {
            return invalid("mp_renderer_set_user_preset_root: NULL renderer");
        }
        return as_renderer(r)->set_user_preset_root(utf8_path);
    });
}

MP_API mp_result MP_CALL mp_renderer_rescan_presets(mp_renderer* r, uint32_t* count) {
    return mp::abi::guard([&]() -> mp_result {
        if (r == nullptr) {
            return invalid("mp_renderer_rescan_presets: NULL renderer");
        }
        const uint32_t total = as_renderer(r)->rescan_presets();
        if (count != nullptr) {
            *count = total;
        }
        return MP_OK;
    });
}

// Guarded rather than forwarded bare: this one compiles HLSL on the calling thread, which allocates and can
// throw, and a preset that fails must leave the renderer exactly as it was.
MP_API mp_result MP_CALL mp_renderer_set_preset(mp_renderer* r, const char* utf8_id) {
    return mp::abi::guard([&]() -> mp_result {
        if (r == nullptr || utf8_id == nullptr) {
            return invalid("mp_renderer_set_preset: NULL renderer or id");
        }
        return as_renderer(r)->set_preset(utf8_id);
    });
}

// T-127 (ABI 0.21). Guarded like get_stats: the fill takes the preset mutex and copies two strings.
MP_API mp_result MP_CALL mp_renderer_get_preset(mp_renderer* r, mp_preset_info* out) {
    return mp::abi::guard([&]() -> mp_result {
        if (r == nullptr) {
            return invalid("mp_renderer_get_preset: NULL renderer");
        }
        return out_struct(out, "mp_renderer_get_preset",
                          [&](mp_preset_info& info) { return as_renderer(r)->get_preset(info); });
    });
}

MP_API mp_result MP_CALL mp_renderer_set_param(mp_renderer* r, const char* utf8_name, float value) {
    return mp::abi::guard([&]() -> mp_result {
        if (r == nullptr || utf8_name == nullptr) {
            return invalid("mp_renderer_set_param: NULL renderer or name");
        }
        return as_renderer(r)->set_param(utf8_name, value);
    });
}

// Guarded because set_theme takes a lock, and a caller built against ABI 0.12's mp_theme_colors - which had
// primary alone past struct_size - is served that prefix, the three colours its header did not have arriving as
// the zeros in_struct's whole struct gives them.
MP_API mp_result MP_CALL mp_renderer_set_theme(mp_renderer* r, const mp_theme_colors* colors) {
    return mp::abi::guard([&]() -> mp_result {
        if (r == nullptr) {
            return invalid("mp_renderer_set_theme: NULL renderer");
        }
        return in_struct(colors, "mp_renderer_set_theme",
                         [&](const mp_theme_colors& whole) { return as_renderer(r)->set_theme(whole); });
    });
}

// The last of E0-S5's stubs to start working (ABI 0.16). Nothing here takes a lock or allocates - the policy
// is one atomic store the render thread picks up on its next frame - so it is forwarded bare, like resize.
MP_API mp_result MP_CALL mp_renderer_set_quality(mp_renderer* r, mp_quality_policy policy) {
    if (r == nullptr) {
        return invalid("mp_renderer_set_quality: NULL renderer");
    }
    return as_renderer(r)->set_quality(policy);
}

// Guarded: it takes the probe's lock and may size a vector, so it can throw. A caller built against 0.17's
// mp_av_sync_config with a later field appended is served the prefix, and the fields its header did not have
// arrive as the zeros in_struct gives them - which for probe_capacity means "off", the documented default.
MP_API mp_result MP_CALL mp_renderer_set_av_sync(mp_renderer* r, const mp_av_sync_config* config) {
    return mp::abi::guard([&]() -> mp_result {
        if (r == nullptr) {
            return invalid("mp_renderer_set_av_sync: NULL renderer");
        }
        return in_struct(config, "mp_renderer_set_av_sync",
                         [&](const mp_av_sync_config& whole) { return as_renderer(r)->set_av_sync(whole); });
    });
}

// T-184, ABI 0.19. Two floats and no struct, deliberately: a struct would be a new row in D-33's minimum-size table
// for two numbers that will never grow a third, and a function that takes them is a minor with nothing to freeze.
// Neither call locks or allocates - the setting is two atomic stores the render thread reads each frame - so both
// are forwarded bare, as set_quality is.
MP_API mp_result MP_CALL mp_renderer_set_temporal_smoothing(mp_renderer* r, float attack_ms, float decay_ms) {
    if (r == nullptr) {
        return invalid("mp_renderer_set_temporal_smoothing: NULL renderer");
    }
    return as_renderer(r)->set_temporal_smoothing(attack_ms, decay_ms);
}

MP_API mp_result MP_CALL mp_renderer_get_temporal_smoothing(mp_renderer* r, float* out_attack_ms, float* out_decay_ms) {
    if (r == nullptr || out_attack_ms == nullptr || out_decay_ms == nullptr) {
        return invalid("mp_renderer_get_temporal_smoothing: NULL renderer or out pointer");
    }
    const mp::render::envelope_times times = as_renderer(r)->temporal_smoothing();
    *out_attack_ms = times.attack_ms;
    *out_decay_ms = times.decay_ms;
    return MP_OK;
}

// out_array, like the enumerations, because out[0].struct_size is the stride and a short caller has to be
// repacked. The drain is destructive, which out_array's short-caller path is safe against: the extra call it
// makes first is the (nullptr, &total) count query, and that takes nothing.
MP_API mp_result MP_CALL mp_renderer_drain_latency(mp_renderer* r, mp_latency_sample* out, uint32_t* count) {
    return mp::abi::guard([&]() -> mp_result {
        if (r == nullptr || count == nullptr) {
            return invalid("mp_renderer_drain_latency: NULL renderer or count");
        }
        return out_array(out, count, "mp_renderer_drain_latency", [&](mp_latency_sample* buffer, uint32_t* n) {
            return as_renderer(r)->drain_latency(buffer, n);
        });
    });
}

} // extern "C"
