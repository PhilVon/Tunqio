// Per-thread last-error storage behind mp_last_error(). Internal to mpcore.
#pragma once

#include <string_view>

namespace mp::abi {

// Records msg for the calling thread. Never allocates after the first call on a thread
// (the storage is a fixed thread_local buffer); longer messages are truncated.
void set_last_error(std::string_view msg) noexcept;

// Clears the calling thread's message.
void clear_last_error() noexcept;

// The calling thread's message (empty when none).
std::string_view last_error() noexcept;

} // namespace mp::abi
