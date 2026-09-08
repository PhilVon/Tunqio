// Export guard: no exception, C++ or structured, may cross the ABI (docs/solution-structure.md).
//
// Usage inside an extern "C" export:
//     return mp::abi::guard([&]() -> mp_result { ...; return MP_OK; });
//
// A C++ exception becomes MP_E_INTERNAL with its what() as the thread's last error; a structured
// exception (access violation, etc.) becomes MP_E_INTERNAL with the exception code. __try and
// C++ try cannot share a function, so the two layers are separate functions.
#pragma once

#include "mpcore.h"

#include "abi/last_error.h"

#include <exception>
#include <string_view>

#include <windows.h>

namespace mp::abi {

namespace detail {

template <typename F> mp_result invoke_cpp(F& f) noexcept {
    try {
        return f();
    } catch (const std::exception& e) {
        set_last_error(e.what());
        return MP_E_INTERNAL;
    } catch (...) {
        set_last_error("unknown C++ exception");
        return MP_E_INTERNAL;
    }
}

void record_seh(unsigned long code) noexcept;

template <typename F> mp_result invoke_seh(F& f) noexcept {
    // No objects with destructors may live in this frame.
    __try {
        return invoke_cpp(f);
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        record_seh(GetExceptionCode());
        return MP_E_INTERNAL;
    }
}

} // namespace detail

template <typename F> mp_result guard(F&& f) noexcept {
    clear_last_error();
    return detail::invoke_seh(f);
}

} // namespace mp::abi
