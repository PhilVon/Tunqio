// Process-wide log sink behind mp_log_set_sink. Never call from the audio callback (it formats and may
// allocate); the engine only logs from control-plane calls.
#pragma once

#include "mpcore.h"

namespace mp {

void log_set_sink(mp_log_cb sink, void* user, mp_log_level min_level) noexcept;

// printf-style; silently dropped when no sink is installed or the level is below the sink's minimum.
void log(mp_log_level level, const char* fmt, ...) noexcept;

} // namespace mp
