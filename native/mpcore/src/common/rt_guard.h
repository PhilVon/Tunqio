// Real-time guard (docs/solution-structure.md, C++ rules): Debug builds count every C++ allocation made
// while a thread is inside a real-time scope (the WASAPI output callback, the DSP tap). The Catch2 suite
// asserts the count stays at zero across a rendered stretch of audio; Release builds compile the scope away.
#pragma once

#include <cstdint>

namespace mp::rt {

#if defined(MP_DEBUG) && MP_DEBUG
// RAII marker for a real-time section on the current thread. Nestable.
class scope {
public:
    scope() noexcept;
    ~scope() noexcept;
    scope(const scope&) = delete;
    scope& operator=(const scope&) = delete;
};

// True while the calling thread is inside a scope.
bool active() noexcept;

// Allocations observed inside a scope, process-wide, since start-up (or since reset_violations).
uint64_t violations() noexcept;
void reset_violations() noexcept;
#else
class scope {
public:
    scope() noexcept = default;
};
inline bool active() noexcept {
    return false;
}
inline uint64_t violations() noexcept {
    return 0;
}
inline void reset_violations() noexcept {}
#endif

} // namespace mp::rt
