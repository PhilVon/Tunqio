// Include-grep architecture test (docs/solution-structure.md, "Dependency rules"; E0-S3 criterion):
//   - only files under mpcore/src/audio/ may include a BASS header;
//   - audio/ and analysis/ never include render/ headers.
// Runs against the source tree found via source_root.h; skips (not fails) when the tree is unavailable,
// which only happens when the test binary is run detached from the repository.
#include "source_root.h"

#include <catch2/catch_amalgamated.hpp>
#include <filesystem>
#include <fstream>
#include <regex>
#include <string>
#include <vector>

namespace {

namespace fs = std::filesystem;

struct include_hit {
    fs::path file;
    std::string header;
};

std::vector<include_hit> collect_includes(const fs::path& root) {
    static const std::regex k_include{R"(^\s*#\s*include\s*[<"]([^>"]+)[>"])"};
    std::vector<include_hit> hits;
    for (const auto& entry : fs::recursive_directory_iterator{root}) {
        if (!entry.is_regular_file()) {
            continue;
        }
        const auto ext = entry.path().extension().string();
        if (ext != ".cpp" && ext != ".h" && ext != ".hpp" && ext != ".c") {
            continue;
        }
        std::ifstream in{entry.path()};
        std::string line;
        while (std::getline(in, line)) {
            std::smatch m;
            if (std::regex_search(line, m, k_include)) {
                hits.push_back({entry.path(), m[1].str()});
            }
        }
    }
    return hits;
}

// A BASS SDK header: one of the known names, or anything that resolves inside native/bass/include (the
// fetched SDK). mpcore's own audio/bass_engine.h is not one.
bool is_bass_header(const std::string& header, const fs::path& native_root) {
    static const char* const k_known[] = {"bass.h",     "bassmix.h", "basswasapi.h", "bassflac.h",
                                          "bassopus.h", "basswv.h",  "bass_ape.h",   "bass_aac.h"};
    const auto name = fs::path{header}.filename().string();
    for (const char* known : k_known) {
        if (name == known) {
            return true;
        }
    }
    return name.starts_with("bass") && name.ends_with(".h") && fs::exists(native_root / "bass" / "include" / name);
}

bool under(const fs::path& file, const fs::path& dir) {
    const auto rel = fs::relative(file, dir);
    return !rel.empty() && rel.native().rfind(L"..", 0) != 0;
}

} // namespace

TEST_CASE("only mpcore/src/audio includes BASS headers", "[architecture]") {
    const auto native = mp::tests::find_native_root();
    if (!native) {
        SKIP("native source root not found (set MPCORE_SOURCE_ROOT)");
    }
    const fs::path src = *native / "mpcore" / "src";
    const fs::path audio = src / "audio";

    std::vector<std::string> violations;
    for (const auto& hit : collect_includes(src)) {
        if (is_bass_header(hit.header, *native) && !under(hit.file, audio)) {
            violations.push_back(fs::relative(hit.file, src).generic_string() + " includes " + hit.header);
        }
    }
    CHECK(violations.empty());
    for (const auto& v : violations) {
        WARN(v);
    }
}

TEST_CASE("audio and analysis never include render headers", "[architecture]") {
    const auto native = mp::tests::find_native_root();
    if (!native) {
        SKIP("native source root not found (set MPCORE_SOURCE_ROOT)");
    }
    const fs::path src = *native / "mpcore" / "src";

    std::vector<std::string> violations;
    for (const auto& hit : collect_includes(src)) {
        const bool lower_layer = under(hit.file, src / "audio") || under(hit.file, src / "analysis");
        if (lower_layer && hit.header.rfind("render/", 0) == 0) {
            violations.push_back(fs::relative(hit.file, src).generic_string() + " includes " + hit.header);
        }
    }
    CHECK(violations.empty());
    for (const auto& v : violations) {
        WARN(v);
    }
}
