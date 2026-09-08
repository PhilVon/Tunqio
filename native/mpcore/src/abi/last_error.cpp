#include "abi/last_error.h"

#include <algorithm>
#include <cstring>

namespace mp::abi {
namespace {

constexpr size_t k_capacity = 512;

struct thread_error {
    char text[k_capacity] = {};
    size_t length = 0;
};

thread_local thread_error g_error;

} // namespace

void set_last_error(std::string_view msg) noexcept {
    const size_t n = std::min(msg.size(), k_capacity - 1);
    std::memcpy(g_error.text, msg.data(), n);
    g_error.text[n] = '\0';
    g_error.length = n;
}

void clear_last_error() noexcept {
    g_error.text[0] = '\0';
    g_error.length = 0;
}

std::string_view last_error() noexcept {
    return {g_error.text, g_error.length};
}

} // namespace mp::abi
