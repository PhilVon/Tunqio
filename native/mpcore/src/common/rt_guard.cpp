// See rt_guard.h. The replacement global operator new/delete is compiled into the module that holds this
// file (mpcore.dll, or the test executable that compiles the same sources), which is what makes an
// allocation inside a real-time scope observable without touching BASS or the CRT.
#include "common/rt_guard.h"

#if defined(MP_DEBUG) && MP_DEBUG

#include <atomic>
#include <cstdlib>
#include <new>

namespace mp::rt {
namespace {

thread_local int t_depth = 0;
std::atomic<uint64_t> g_violations{0};

void note_allocation() noexcept {
    if (t_depth > 0) {
        g_violations.fetch_add(1, std::memory_order_relaxed);
    }
}

} // namespace

scope::scope() noexcept {
    ++t_depth;
}

scope::~scope() noexcept {
    --t_depth;
}

bool active() noexcept {
    return t_depth > 0;
}

uint64_t violations() noexcept {
    return g_violations.load(std::memory_order_relaxed);
}

void reset_violations() noexcept {
    g_violations.store(0, std::memory_order_relaxed);
}

} // namespace mp::rt

void* operator new(std::size_t size) {
    mp::rt::note_allocation();
    void* p = std::malloc(size == 0 ? 1 : size);
    if (p == nullptr) {
        throw std::bad_alloc{};
    }
    return p;
}

void* operator new[](std::size_t size) {
    return operator new(size);
}

void* operator new(std::size_t size, const std::nothrow_t&) noexcept {
    mp::rt::note_allocation();
    return std::malloc(size == 0 ? 1 : size);
}

void* operator new[](std::size_t size, const std::nothrow_t& tag) noexcept {
    return operator new(size, tag);
}

void operator delete(void* p) noexcept {
    std::free(p);
}

void operator delete(void* p, std::size_t) noexcept {
    std::free(p);
}

void operator delete[](void* p) noexcept {
    std::free(p);
}

void operator delete[](void* p, std::size_t) noexcept {
    std::free(p);
}

#endif
