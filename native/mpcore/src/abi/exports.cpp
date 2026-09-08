// The extern "C" surface of mpcore.dll. Everything here is thin: validate, forward, convert errors.
#include "mpcore.h"

#include "abi/guard.h"
#include "abi/last_error.h"
#include "common/version.h"

#include <algorithm>
#include <cstdio>
#include <cstring>

namespace mp::abi::detail {

void record_seh(unsigned long code) noexcept {
    char text[64];
    std::snprintf(text, sizeof text, "structured exception 0x%08lX", code);
    set_last_error(text);
}

} // namespace mp::abi::detail

extern "C" {

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

} // extern "C"
