// Product version, injected by the build as MP_VERSION_MAJOR/MINOR/PATCH from Directory.Build.props (TunqioVersion).
#pragma once

#if !defined(MP_VERSION_MAJOR) || !defined(MP_VERSION_MINOR) || !defined(MP_VERSION_PATCH)
#error "MP_VERSION_MAJOR/MINOR/PATCH must be defined by the build (see native/tunqio.native.props)"
#endif

#define MP_STRINGIZE_(x) #x
#define MP_STRINGIZE(x) MP_STRINGIZE_(x)
#define MP_VERSION_STRING                                                                                              \
    MP_STRINGIZE(MP_VERSION_MAJOR) "." MP_STRINGIZE(MP_VERSION_MINOR) "." MP_STRINGIZE(MP_VERSION_PATCH)

namespace mp {

constexpr const char* version_string() noexcept {
    return MP_VERSION_STRING;
}

} // namespace mp
