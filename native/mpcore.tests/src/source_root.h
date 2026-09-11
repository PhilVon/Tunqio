// Locates the repository's native/ directory at runtime so the architecture test can grep sources.
// Order: MPCORE_SOURCE_ROOT environment variable, then walk up from the executable until a
// directory containing native/mpcore/include/mpcore.h is found.
#pragma once

#include <filesystem>
#include <optional>
#include <string>

#include <windows.h>

namespace mp::tests {

inline std::optional<std::filesystem::path> find_native_root() {
    namespace fs = std::filesystem;
    const auto is_root = [](const fs::path& p) { return fs::exists(p / "native" / "mpcore" / "include" / "mpcore.h"); };

    wchar_t env[MAX_PATH];
    if (const DWORD len = GetEnvironmentVariableW(L"MPCORE_SOURCE_ROOT", env, MAX_PATH); len > 0 && len < MAX_PATH) {
        fs::path p{env};
        if (is_root(p)) {
            return p / "native";
        }
        if (is_root(p.parent_path())) {
            return p.parent_path() / "native";
        }
    }

    wchar_t exe[MAX_PATH];
    const DWORD n = GetModuleFileNameW(nullptr, exe, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) {
        return std::nullopt;
    }
    fs::path dir = fs::path{exe}.parent_path();
    for (int depth = 0; depth < 12 && !dir.empty(); ++depth) {
        if (is_root(dir)) {
            return dir / "native";
        }
        const fs::path parent = dir.parent_path();
        if (parent == dir) {
            break;
        }
        dir = parent;
    }
    return std::nullopt;
}

// The preset fixture directory (native/mpcore.tests/fixtures/presets), or nullopt when the tree is unavailable.
inline std::optional<std::filesystem::path> find_preset_fixtures() {
    const auto native = find_native_root();
    if (!native) {
        return std::nullopt;
    }
    auto path = *native / "mpcore.tests" / "fixtures" / "presets";
    if (!std::filesystem::exists(path)) {
        return std::nullopt;
    }
    return path;
}

} // namespace mp::tests
