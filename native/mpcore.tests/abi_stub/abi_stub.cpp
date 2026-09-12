// mpcore_abi_stub.dll: a library that reports an ABI the managed loader must refuse, so the refusal itself can
// be tested end to end rather than only as arithmetic.
//
// By default it reports MP_ABI_MAJOR + 1, which is AC-31's case. Since T-161 (Q-36: refuse always) the loader
// also refuses a core whose MINOR is below the binding's, and that case needs a stub too - so the reported
// version can be overridden with the environment variable TUNQIO_ABI_STUB_VERSION, read once at load, in the
// form "<major>.<minor>" (for example "0.15"). One DLL, both refusals, no second project in the solution.
#include "mpcore.h"

#include <cstdio>
#include <cstdlib>

#include <windows.h>

namespace {

uint32_t read_version() {
    char buf[64];
    const DWORD n = GetEnvironmentVariableA("TUNQIO_ABI_STUB_VERSION", buf, sizeof buf);
    if (n == 0 || n >= sizeof buf) {
        return ((MP_ABI_MAJOR + 1u) << 16) | 0u;
    }
    unsigned major = 0;
    unsigned minor = 0;
    if (sscanf_s(buf, "%u.%u", &major, &minor) != 2) {
        return ((MP_ABI_MAJOR + 1u) << 16) | 0u;
    }
    return ((major & 0xFFFFu) << 16) | (minor & 0xFFFFu);
}

} // namespace

extern "C" {

MP_API uint32_t MP_CALL mpcore_abi_version(void) {
    // Read every call rather than once: the tests set the variable, load, probe and free in one process, and a
    // cached value would make the second probe answer the first probe's question.
    return read_version();
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
