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

// The last of E0-S5's stubs to start working (ABI 0.14). Nothing here takes a lock or allocates - the policy
// is one atomic store the render thread picks up on its next frame - so it is forwarded bare, like resize.
MP_API mp_result MP_CALL mp_renderer_set_quality(mp_renderer* r, mp_quality_policy policy) {
    if (r == nullptr) {
        return invalid("mp_renderer_set_quality: NULL renderer");
    }
    return as_renderer(r)->set_quality(policy);
}

} // extern "C"
