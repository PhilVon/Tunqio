/*
 * mpcore.h - the C ABI of mpcore.dll (docs/solution-structure.md, "ABI rules").
 *
 * This is the only header the managed side (Tunqio.Interop) reads. Every export is extern "C",
 * __cdecl, returns mp_result (or a plain scalar for pure queries), never throws, and never
 * invokes a callback after the owning handle is destroyed.
 *
 * E0-S1 checks in the version and error surface only. E0-S4 (BASS spike) drafts the engine
 * exports; later stories append. Appending is ABI-minor; changing or removing is ABI-major.
 */
#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(MP_STATIC)
#define MP_API
#elif defined(MP_BUILDING_DLL)
#define MP_API __declspec(dllexport)
#else
#define MP_API __declspec(dllimport)
#endif

#define MP_CALL __cdecl

/* ABI version. Interop refuses to load on a MAJOR mismatch (mpcore_abi_version() >> 16). */
#define MP_ABI_MAJOR 0u
#define MP_ABI_MINOR 1u

typedef enum mp_result {
    MP_OK = 0,
    MP_E_INVALID_ARG = 1,
    MP_E_BASS = 2,
    MP_E_DEVICE = 3,
    MP_E_D3D = 4,
    MP_E_STATE = 5,
    MP_E_INTERNAL = 6
} mp_result;

/* (MP_ABI_MAJOR << 16) | MP_ABI_MINOR. */
MP_API uint32_t MP_CALL mpcore_abi_version(void);

/* Product version string ("major.minor.patch"), the same value the MSIX and the managed assemblies carry.
 * Static storage; never freed by the caller. */
MP_API const char* MP_CALL mp_version(void);

/* Copies the calling thread's last error message (UTF-8, NUL-terminated, possibly empty) into buf.
 * Returns MP_E_INVALID_ARG when buf is NULL or len is 0; the message is truncated to fit. */
MP_API mp_result MP_CALL mp_last_error(char* buf, size_t len);

#ifdef __cplusplus
} /* extern "C" */
#endif
