// The struct_size rule of the C ABI (mpcore.h, "Rules"), in one place.
//
// Every struct that crosses the boundary carries its own size first, and that number is which header the caller
// was built against. Three answers, and they are the whole rule:
//
//   - the size this build knows: served as it always was, straight into the caller's own struct;
//   - a smaller size: a caller built against an older header. It is served the prefix that size covers - only
//     those fields are read from an in struct, only those fields are written to an out struct - and nothing
//     beyond it is touched, because the bytes past the caller's struct_size are not the caller's struct;
//   - a larger size, or one too small to hold the struct's first meaningful field: MP_E_INVALID_ARG. A longer
//     struct is a caller asking for fields this DLL has never heard of, and a field it would have to invent is
//     worse than an error.
//
// That is what makes appending a field an ABI-minor: a binary built against the shorter struct keeps working,
// because it asks for - and is given - exactly the prefix it understands. It was not true until T-140, where
// the check was `struct_size == sizeof(T)` and refused the old caller along with the new one, and it stays true
// only while every export goes through the helpers below instead of comparing sizes for itself.
//
// A struct with no abi_struct specialisation will not compile through those helpers, which is how a struct
// added to the header is made to answer what its smallest useful prefix is. mp_event is the one struct in
// mpcore.h that is not listed: the core fills it for a callback, so no caller ever sizes it.
#pragma once

#include "mpcore.h"

#include "abi/last_error.h"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <type_traits>
#include <vector>

namespace mp::abi {

// What this build knows about one ABI struct: its name, for the diagnostic, and the smallest struct_size it
// will serve - the size field plus the struct's first meaningful field. A prefix shorter than that carries no
// answer at all, and a caller that sends one has a bug rather than an old header. The number is declared per
// struct rather than derived, because which field is the first meaningful one is knowledge about the struct.
template <typename T> struct abi_struct;

#define MP_ABI_STRUCT(type, first_field)                                                                               \
    template <> struct abi_struct<type> {                                                                              \
        static constexpr const char* name = #type;                                                                     \
        static constexpr uint32_t min_size =                                                                           \
            static_cast<uint32_t>(offsetof(type, first_field) + sizeof(type::first_field));                            \
        static_assert(offsetof(type, struct_size) == 0, #type " must carry struct_size first");                        \
        static_assert(sizeof(type::struct_size) == sizeof(uint32_t), #type "::struct_size must be a uint32_t");        \
        static_assert(min_size > sizeof(uint32_t) && min_size <= sizeof(type),                                         \
                      #type "'s minimum must cover struct_size plus " #first_field " and fit the struct");             \
        static_assert(std::is_trivially_copyable_v<type>, #type " is copied by prefix with memcpy");                   \
    }

MP_ABI_STRUCT(mp_engine_config, sample_rate);
MP_ABI_STRUCT(mp_output_config, device_index);
MP_ABI_STRUCT(mp_device_info, index);
MP_ABI_STRUCT(mp_track_info, duration_ms);
MP_ABI_STRUCT(mp_clock, position_ms);
MP_ABI_STRUCT(mp_engine_stats, callbacks);
MP_ABI_STRUCT(mp_analysis_frame, sequence);
MP_ABI_STRUCT(mp_renderer_config, width);
MP_ABI_STRUCT(mp_render_stats, frames);
MP_ABI_STRUCT(mp_preset_info, id);
MP_ABI_STRUCT(mp_theme_colors, primary);

// Whether a caller's struct_size names a prefix of T this build can serve.
template <typename T> constexpr bool size_served(uint32_t size) noexcept {
    return size >= abi_struct<T>::min_size && size <= sizeof(T);
}

namespace detail {

// Why the struct was refused, in words the caller can act on: one says "you were built against a newer mpcore
// than the one you loaded", the other "this is not a struct_size any header ever had". Not a template, so the
// text exists once however many structs there are.
inline mp_result reject(const char* export_name, const char* type_name, const uint32_t* size, uint32_t min_size,
                        uint32_t max_size) noexcept {
    char text[256];
    if (size == nullptr) {
        std::snprintf(text, sizeof text, "%s: NULL %s", export_name, type_name);
    } else if (*size > max_size) {
        std::snprintf(text, sizeof text,
                      "%s: struct_size %u is larger than this build's %s (%u bytes), so the caller was built "
                      "against a newer mpcore.h than this mpcore.dll",
                      export_name, *size, type_name, max_size);
    } else {
        std::snprintf(text, sizeof text, "%s: struct_size %u is below the %u bytes of %s a caller must carry",
                      export_name, *size, min_size, type_name);
    }
    set_last_error(text);
    return MP_E_INVALID_ARG;
}

template <typename T> mp_result reject(const char* export_name, const T* caller) noexcept {
    return reject(export_name, abi_struct<T>::name, caller == nullptr ? nullptr : &caller->struct_size,
                  abi_struct<T>::min_size, static_cast<uint32_t>(sizeof(T)));
}

} // namespace detail

// Serves a caller's in struct: `use` is called with a T this build can read every field of. A short caller's
// bytes are copied into a zeroed one, so the fields its header did not have read as zero - which is what every
// one of them documents as its default - and the bytes past its struct_size are never read.
template <typename T, typename Use> mp_result in_struct(const T* caller, const char* export_name, Use&& use) {
    if (caller == nullptr || !size_served<T>(caller->struct_size)) {
        return detail::reject(export_name, caller);
    }
    if (caller->struct_size == sizeof(T)) {
        return use(*caller);
    }
    T whole{};
    std::memcpy(&whole, caller, caller->struct_size);
    return use(whole);
}

// Serves a caller's out struct: `fill` is called with a T it may write in full. A short caller gets back the
// bytes its struct_size covers and not one byte more, and is told the size that was filled - its own - rather
// than what this build knows. A failed fill copies nothing back, so a short caller's struct is left as it was.
template <typename T, typename Fill> mp_result out_struct(T* caller, const char* export_name, Fill&& fill) {
    if (caller == nullptr || !size_served<T>(caller->struct_size)) {
        return detail::reject(export_name, caller);
    }
    const uint32_t size = caller->struct_size;
    if (size == sizeof(T)) {
        return fill(*caller); // the current size: filled in place, exactly as it was before the rule existed
    }
    T whole{};
    whole.struct_size = sizeof(T);
    const mp_result result = fill(whole);
    if (result == MP_OK) {
        whole.struct_size = size;
        std::memcpy(caller, &whole, size);
    }
    return result;
}

// Serves a caller's array of out structs (mp_engine_enum_devices, mp_renderer_enum_presets). out[0].struct_size
// is the size of every element, because they are one array and the size is also the stride: out[i] in this
// build's terms is not where a short caller's i-th element begins. `fill` is the two-call protocol itself -
// (nullptr, &total) counts, (buffer, &n) writes at most n - and a short caller is served by running it into a
// buffer of this build's elements and repacking. That buffer is bounded by how many there are to enumerate and
// not by the count the caller claims, and only an old caller ever allocates it.
template <typename T, typename Fill>
mp_result out_array(T* out, uint32_t* count, const char* export_name, Fill&& fill) {
    if (out == nullptr || *count == 0) {
        return fill(out, count); // the count query, and a buffer with no room: no element to take a size from
    }
    if (!size_served<T>(out->struct_size)) {
        return detail::reject(export_name, out);
    }
    const uint32_t size = out->struct_size;
    if (size == sizeof(T)) {
        return fill(out, count);
    }
    uint32_t total = 0;
    if (const mp_result counted = fill(nullptr, &total); counted != MP_OK) {
        return counted;
    }
    const uint32_t wanted = std::min(*count, total);
    if (wanted == 0) {
        *count = 0;
        return MP_OK;
    }
    std::vector<T> whole(wanted);
    uint32_t written = wanted;
    if (const mp_result filled = fill(whole.data(), &written); filled != MP_OK) {
        return filled;
    }
    written = std::min(written, wanted);
    for (uint32_t i = 0; i < written; ++i) {
        whole[i].struct_size = size; // each element says what it carries, not what this build knows
        std::memcpy(reinterpret_cast<std::byte*>(out) + static_cast<size_t>(i) * size, &whole[i], size);
    }
    *count = written;
    return MP_OK;
}

} // namespace mp::abi
