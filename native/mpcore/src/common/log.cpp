#include "common/log.h"

#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <mutex>

namespace mp {
namespace {

struct sink_state {
    mp_log_cb callback = nullptr;
    void* user = nullptr;
    mp_log_level min_level = MP_LOG_INFO;
};

std::mutex g_mutex;
sink_state g_sink;
std::atomic<int> g_min_level{MP_LOG_ERROR + 1}; // fast reject when no sink

} // namespace

void log_set_sink(mp_log_cb sink, void* user, mp_log_level min_level) noexcept {
    std::lock_guard lock{g_mutex};
    g_sink = {sink, user, min_level};
    g_min_level.store(sink ? static_cast<int>(min_level) : MP_LOG_ERROR + 1, std::memory_order_release);
}

void log(mp_log_level level, const char* fmt, ...) noexcept {
    if (static_cast<int>(level) < g_min_level.load(std::memory_order_acquire)) {
        return;
    }
    char text[1024];
    va_list args;
    va_start(args, fmt);
    std::vsnprintf(text, sizeof text, fmt, args);
    va_end(args);

    std::lock_guard lock{g_mutex};
    if (g_sink.callback != nullptr && level >= g_sink.min_level) {
        g_sink.callback(level, text, g_sink.user);
    }
}

} // namespace mp
