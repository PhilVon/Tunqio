// The mp_renderer_* exports (ABI 0.3). Thin: validate, forward, convert errors.
#include "mpcore.h"

#include "abi/guard.h"
#include "abi/last_error.h"
#include "render/renderer.h"

#include <cstdio>
#include <memory>

namespace {

using mp::render::renderer;

renderer* as_renderer(mp_renderer* r) {
    return reinterpret_cast<renderer*>(r);
}

template <typename T> bool size_ok(const T* s) {
    return s != nullptr && s->struct_size == sizeof(T);
}

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

MP_API mp_result MP_CALL mp_renderer_create(mp_engine* /*engine: E4-S1 wires the analysis stream*/,
                                            void* swap_chain_panel_native, const mp_renderer_config* config,
                                            mp_renderer** out_renderer) {
    return mp::abi::guard([&]() -> mp_result {
        if (!size_ok(config) || out_renderer == nullptr) {
            return invalid("mp_renderer_create: bad config struct_size or NULL out_renderer");
        }
        *out_renderer = nullptr;
        std::unique_ptr<renderer> r;
        const mp_result result = renderer::create(swap_chain_panel_native, *config, r);
        if (result == MP_OK) {
            *out_renderer = reinterpret_cast<mp_renderer*>(r.release());
        }
        return result;
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
    if (r == nullptr || !size_ok(out_stats)) {
        return invalid("mp_renderer_get_stats: NULL renderer or bad struct_size");
    }
    as_renderer(r)->get_stats(*out_stats);
    return MP_OK;
}

MP_API mp_result MP_CALL mp_renderer_enum_presets(mp_renderer* r, mp_preset_info* /*out*/, uint32_t* count) {
    if (r == nullptr || count == nullptr) {
        return invalid("mp_renderer_enum_presets: NULL renderer or count");
    }
    return not_implemented("mp_renderer_enum_presets", "E4-S3");
}

MP_API mp_result MP_CALL mp_renderer_set_preset(mp_renderer* r, const char* utf8_id) {
    if (r == nullptr || utf8_id == nullptr) {
        return invalid("mp_renderer_set_preset: NULL renderer or id");
    }
    return not_implemented("mp_renderer_set_preset", "E4-S3");
}

MP_API mp_result MP_CALL mp_renderer_set_param(mp_renderer* r, const char* utf8_name, float /*value*/) {
    if (r == nullptr || utf8_name == nullptr) {
        return invalid("mp_renderer_set_param: NULL renderer or name");
    }
    return not_implemented("mp_renderer_set_param", "E4-S3");
}

MP_API mp_result MP_CALL mp_renderer_set_theme(mp_renderer* r, const mp_theme_colors* colors) {
    if (r == nullptr || !size_ok(colors)) {
        return invalid("mp_renderer_set_theme: NULL renderer or bad struct_size");
    }
    return not_implemented("mp_renderer_set_theme", "E4-S6");
}

MP_API mp_result MP_CALL mp_renderer_set_quality(mp_renderer* r, mp_quality_policy /*policy*/) {
    if (r == nullptr) {
        return invalid("mp_renderer_set_quality: NULL renderer");
    }
    return not_implemented("mp_renderer_set_quality", "E4-S7");
}

} // extern "C"
