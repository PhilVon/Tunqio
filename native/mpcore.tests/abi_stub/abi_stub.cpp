// mpcore_abi_stub.dll: reports ABI major MP_ABI_MAJOR + 1 so the managed loader's refusal can be tested.
#include "mpcore.h"

extern "C" {

MP_API uint32_t MP_CALL mpcore_abi_version(void) {
    return ((MP_ABI_MAJOR + 1u) << 16) | 0u;
}

MP_API const char* MP_CALL mp_version(void) {
    return "0.0.0-abi-stub";
}

MP_API mp_result MP_CALL mp_last_error(char* buf, size_t len) {
    if (buf == nullptr || len == 0) {
        return MP_E_INVALID_ARG;
    }
    buf[0] = '\0';
    return MP_OK;
}

} // extern "C"
