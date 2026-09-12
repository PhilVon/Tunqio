// The preset loader on its own (E4-S3): what preset.json is allowed to say, what a bad one costs, and what a
// scan of a directory returns. No device is involved here - compilation is tested in test_renderer.cpp, where
// there is one - which is the point of the split in render/preset.h.
#include "render/preset.h"
#include "source_root.h"

#include <algorithm>
#include <catch2/catch_amalgamated.hpp>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace {

namespace fs = std::filesystem;
using mp::render::preset_source;

fs::path fixtures() {
    const auto root = mp::tests::find_preset_fixtures();
    if (!root) {
        SKIP("preset fixtures not found (set MPCORE_SOURCE_ROOT)");
    }
    return *root;
}

// Writes a manifest into a scratch directory the test owns, for the shapes it would be silly to keep on disk.
struct scratch_preset {
    fs::path dir;

    explicit scratch_preset(const std::string& name) {
        dir = fs::temp_directory_path() / ("tunqio-preset-" + name + "-" + std::to_string(GetCurrentProcessId()));
        fs::remove_all(dir);
        fs::create_directories(dir);
    }
    ~scratch_preset() {
        std::error_code ec;
        fs::remove_all(dir, ec);
    }
    scratch_preset(const scratch_preset&) = delete;
    scratch_preset& operator=(const scratch_preset&) = delete;

    void write(const std::string& file, const std::string& text) const {
        std::ofstream out{dir / file, std::ios::binary};
        out << text;
    }
    bool load(preset_source& out, std::string& error) const {
        return mp::render::load_preset_source(dir / "preset.json", out, error);
    }
};

constexpr const char* k_trivial_hlsl = R"hlsl(
struct VSOut { float4 pos : SV_Position; };
VSOut VSMain(uint vid : SV_VertexID) { VSOut o; o.pos = float4(0, 0, 0, 1); return o; }
float4 PSMain(VSOut i) : SV_Target { return float4(1, 1, 1, 1); }
)hlsl";

} // namespace

TEST_CASE("the built-in preset is complete without any file on disk", "[render][preset]") {
    const preset_source& p = mp::render::builtin_preset();
    CHECK(p.id == "builtin-bars");
    CHECK_FALSE(p.name.empty());
    CHECK_FALSE(p.hlsl.empty());
    CHECK(p.vertex_count == 6);
    CHECK(p.instance_count == 64);
    CHECK(p.find_param("gain") == 0);
    CHECK(p.find_param("no-such-parameter") == -1);
    CHECK(p.id.size() < 64);    // mp_preset_info.id
    CHECK(p.name.size() < 128); // mp_preset_info.name
}

TEST_CASE("a well-formed preset.json is read whole", "[render][preset]") {
    preset_source p;
    std::string error;
    REQUIRE(mp::render::load_preset_source(fixtures() / "solid-green" / "preset.json", p, error));
    CHECK(error.empty());
    CHECK(p.id == "solid-green");
    CHECK(p.name == "Solid Green (fixture)");
    CHECK(p.shader_name == "solid-green.hlsl");
    CHECK(p.vs_entry == "VSMain"); // the default, not stated in the manifest
    CHECK(p.ps_entry == "PSMain");
    CHECK(p.vertex_count == 3);
    CHECK(p.instance_count == 1);
    CHECK_FALSE(p.triangle_strip);
    CHECK(p.hlsl.find("PSMain") != std::string::npos);
    REQUIRE(p.params.size() == 1);
    CHECK(p.params[0].name == "level");
    CHECK(p.params[0].default_value == Catch::Approx(1.0f));
    CHECK(p.params[0].min_value == Catch::Approx(0.0f));
    CHECK(p.params[0].max_value == Catch::Approx(1.0f));
}

// T-142. The metadata a settings page needs, and the two claims that matter about it: a manifest that declares
// none of it is unchanged (label falls back to the name, step to continuous, nothing hidden), and a manifest
// that declares all of it is read whole. Neither is a shader-visible change, which is why the schema stays 2.
TEST_CASE("a parameter's settings-page metadata is read off the manifest", "[render][preset][params]") {
    preset_source p;
    std::string error;
    REQUIRE(mp::render::load_preset_source(fixtures() / "param-metadata" / "preset.json", p, error));
    REQUIRE(p.params.size() == 6);

    SECTION("a label is a display name and a name is not") {
        CHECK(p.params[0].label == "Level");
        CHECK(p.params[0].name == "level");
    }

    SECTION("a parameter that declares no label is labelled by its name rather than by nothing") {
        REQUIRE(p.params[4].name == "bare");
        CHECK(p.params[4].label == "bare");
        CHECK(p.params[4].unit.empty());
        CHECK(p.params[4].step == Catch::Approx(0.0f)); // continuous
        CHECK_FALSE(p.params[4].hidden);
        CHECK(p.params[4].choices.empty());
    }

    SECTION("step is what says a count is whole and a gain is not") {
        REQUIRE(p.params[1].name == "count");
        CHECK(p.params[1].step == Catch::Approx(1.0f));
        CHECK(p.params[1].min_value == Catch::Approx(8.0f));
        CHECK(p.params[1].max_value == Catch::Approx(128.0f));
        CHECK(p.params[0].step == Catch::Approx(0.0f));
    }

    SECTION("a unit is carried so a number can be shown as the thing it measures") {
        REQUIRE(p.params[2].name == "thickness");
        CHECK(p.params[2].unit == "px");
    }

    SECTION("a mode carries its choices in value order and is stepped by one whatever the manifest said") {
        REQUIRE(p.params[3].name == "mode");
        REQUIRE(p.params[3].choices.size() == 3);
        CHECK(p.params[3].choices[0] == "First");
        CHECK(p.params[3].choices[2] == "Third");
        CHECK(p.params[3].step == Catch::Approx(1.0f));
    }

    SECTION("hidden is what stops a settings page offering a packed sRGB integer as a slider") {
        REQUIRE(p.params[5].name == "art_primary");
        CHECK(p.params[5].hidden);
        // Every other one is offered: the flag has to be an exception, or it is a way to hide a preset's
        // whole surface by accident.
        for (size_t i = 0; i < 5; ++i) {
            INFO("parameter " << p.params[i].name);
            CHECK_FALSE(p.params[i].hidden);
        }
    }
}

TEST_CASE("parameter metadata is validated rather than truncated", "[render][preset][params]") {
    const std::string head = R"({"schema": 2, "shader": "s.hlsl", "id": "m", "name": "m", "parameters": [)";

    auto refuses = [&](const std::string& name, const std::string& parameter, const std::string& expected) {
        const scratch_preset s{name};
        s.write("s.hlsl", k_trivial_hlsl);
        s.write("preset.json", head + parameter + "]}");
        preset_source p;
        std::string error;
        INFO("manifest: " << head << parameter << "]}");
        CHECK_FALSE(s.load(p, error));
        INFO("error: " << error);
        CHECK(error.find(expected) != std::string::npos);
    };

    // A label that would not fit mp_preset_param_info.label, a unit that would not fit its unit, and choices
    // that would not fit its 256 bytes. Refused at load, because a manifest silently cut in half on the way to
    // a settings page is a preset author debugging the wrong thing.
    refuses("long-label", R"({"name": "p", "label": ")" + std::string(64, 'x') + R"("})", "label");
    refuses("long-unit", R"({"name": "p", "unit": "abcdefghijklmnop"})", "unit");
    refuses("negative-step", R"({"name": "p", "step": -1.0})", "step");
    refuses("empty-choices", R"({"name": "p", "choices": []})", "choices");
    refuses("pipe-in-choice", R"({"name": "p", "min": 0, "max": 1, "choices": ["a|b", "c"]})", "'|'");
    // The range and the choice list have to agree, because the value IS the index from min_value: three labels
    // over 0..1 means one of them can never be selected, and guessing which is worse than refusing.
    refuses("choices-vs-range", R"({"name": "p", "min": 0, "max": 1, "choices": ["a", "b", "c"]})", "choices");

    SECTION("choices that fill the field exactly are accepted, so the bound is the field and not a guess") {
        // 255 bytes of payload: 64 labels of 3 characters plus 63 separators.
        std::string list;
        for (int i = 0; i < 64; ++i) {
            list += list.empty() ? "" : ", ";
            list += "\"" + std::string(3, static_cast<char>('a' + i % 26)) + "\"";
        }
        const scratch_preset s{"choices-exact"};
        s.write("s.hlsl", k_trivial_hlsl);
        s.write("preset.json", head + R"({"name": "p", "min": 0, "max": 63, "choices": [)" + list + "]}]}");
        preset_source p;
        std::string error;
        INFO("error: " << error);
        REQUIRE(s.load(p, error));
        REQUIRE(p.params.size() == 1);
        CHECK(p.params[0].choices.size() == 64);
    }
}

TEST_CASE("a malformed manifest is refused with a message that names the file", "[render][preset]") {
    preset_source p;
    std::string error;
    const auto path = fixtures() / "broken-json" / "preset.json";
    CHECK_FALSE(mp::render::load_preset_source(path, p, error));
    CHECK(error.find("broken-json") != std::string::npos);
    CHECK(error.size() > 20); // the parser's own complaint, not just the path
}

TEST_CASE("a manifest whose shader is not on disk is refused", "[render][preset]") {
    preset_source p;
    std::string error;
    CHECK_FALSE(mp::render::load_preset_source(fixtures() / "missing-shader" / "preset.json", p, error));
    CHECK(error.find("not-on-disk.hlsl") != std::string::npos);
}

TEST_CASE("a manifest missing preset.json entirely is refused", "[render][preset]") {
    preset_source p;
    std::string error;
    CHECK_FALSE(mp::render::load_preset_source(fixtures() / "no-such-preset" / "preset.json", p, error));
    CHECK_FALSE(error.empty());
}

TEST_CASE("preset.json validation", "[render][preset]") {
    const std::string manifest_head = R"({"schema": 1, "shader": "s.hlsl", )";

    SECTION("an id with a path separator in it is refused") {
        const scratch_preset s{"bad-id"};
        s.write("s.hlsl", k_trivial_hlsl);
        s.write("preset.json", manifest_head + R"("id": "../escape", "name": "x"})");
        preset_source p;
        std::string error;
        CHECK_FALSE(s.load(p, error));
        CHECK(error.find("id") != std::string::npos);
    }

    SECTION("a shader path that climbs out of the preset directory is refused, not resolved") {
        const scratch_preset s{"traversal"};
        s.write("s.hlsl", k_trivial_hlsl);
        s.write("preset.json", R"({"schema": 1, "id": "t", "name": "t", "shader": "../../secrets.hlsl"})");
        preset_source p;
        std::string error;
        CHECK_FALSE(s.load(p, error));
        CHECK(error.find("beside preset.json") != std::string::npos);
    }

    SECTION("an absolute shader path is refused") {
        const scratch_preset s{"absolute"};
        s.write("s.hlsl", k_trivial_hlsl);
        s.write("preset.json", R"({"schema": 1, "id": "t", "name": "t", "shader": "C:\\windows\\x.hlsl"})");
        preset_source p;
        std::string error;
        CHECK_FALSE(s.load(p, error));
        CHECK(error.find("beside preset.json") != std::string::npos);
    }

    SECTION("a schema this build does not know is refused rather than guessed at") {
        const scratch_preset s{"schema"};
        s.write("s.hlsl", k_trivial_hlsl);
        s.write("preset.json", R"({"schema": 99, "id": "t", "name": "t", "shader": "s.hlsl"})");
        preset_source p;
        std::string error;
        CHECK_FALSE(s.load(p, error));
        CHECK(error.find("schema") != std::string::npos);
    }

    SECTION("an unknown topology is refused") {
        const scratch_preset s{"topology"};
        s.write("s.hlsl", k_trivial_hlsl);
        s.write("preset.json", R"({"schema": 1, "id": "t", "name": "t", "shader": "s.hlsl", "topology": "points"})");
        preset_source p;
        std::string error;
        CHECK_FALSE(s.load(p, error));
        CHECK(error.find("topology") != std::string::npos);
    }

    SECTION("trianglestrip is accepted") {
        const scratch_preset s{"strip"};
        s.write("s.hlsl", k_trivial_hlsl);
        s.write("preset.json",
                R"({"schema": 1, "id": "t", "name": "t", "shader": "s.hlsl", "topology": "trianglestrip"})");
        preset_source p;
        std::string error;
        REQUIRE(s.load(p, error));
        CHECK(p.triangle_strip);
    }

    SECTION("a parameter declared twice is refused") {
        const scratch_preset s{"dup-param"};
        s.write("s.hlsl", k_trivial_hlsl);
        s.write("preset.json", R"({"schema": 1, "id": "t", "name": "t", "shader": "s.hlsl",
                "parameters": [{"name": "gain"}, {"name": "gain"}]})");
        preset_source p;
        std::string error;
        CHECK_FALSE(s.load(p, error));
        CHECK(error.find("twice") != std::string::npos);
    }

    SECTION("more parameters than the constant buffer carries is refused") {
        const scratch_preset s{"too-many-params"};
        s.write("s.hlsl", k_trivial_hlsl);
        std::string params;
        for (uint32_t i = 0; i <= mp::render::k_max_preset_params; ++i) {
            params += (i == 0 ? "" : ", ");
            params += R"({"name": "p)" + std::to_string(i) + R"("})";
        }
        s.write("preset.json",
                R"({"schema": 1, "id": "t", "name": "t", "shader": "s.hlsl", "parameters": [)" + params + "]}");
        preset_source p;
        std::string error;
        CHECK_FALSE(s.load(p, error));
        CHECK(error.find("exceeds the limit") != std::string::npos);
    }

    SECTION("a default outside min..max is clamped rather than refused") {
        const scratch_preset s{"clamp"};
        s.write("s.hlsl", k_trivial_hlsl);
        s.write("preset.json", R"({"schema": 1, "id": "t", "name": "t", "shader": "s.hlsl",
                "parameters": [{"name": "gain", "min": 0.0, "max": 2.0, "default": 9.0}]})");
        preset_source p;
        std::string error;
        REQUIRE(s.load(p, error));
        REQUIRE(p.params.size() == 1);
        CHECK(p.params[0].default_value == Catch::Approx(2.0f));
    }

    SECTION("an empty shader file is refused") {
        const scratch_preset s{"empty-shader"};
        s.write("s.hlsl", "");
        s.write("preset.json", R"({"schema": 1, "id": "t", "name": "t", "shader": "s.hlsl"})");
        preset_source p;
        std::string error;
        CHECK_FALSE(s.load(p, error));
        CHECK(error.find("empty") != std::string::npos);
    }
}

TEST_CASE("scanning a directory keeps the good presets and reports the bad ones", "[render][preset]") {
    std::vector<std::string> warnings;
    const auto found = mp::render::scan_preset_root(fixtures(), warnings);

    std::vector<std::string> ids;
    for (const auto& p : found) {
        ids.push_back(p.id);
    }
    // broken-shader is here on purpose: a shader that will not compile is still a well-formed manifest, and the
    // loader must hand it over so that set_preset is what refuses it - with the compiler's words.
    CHECK(std::find(ids.begin(), ids.end(), "solid-green") != ids.end());
    CHECK(std::find(ids.begin(), ids.end(), "solid-blue") != ids.end());
    CHECK(std::find(ids.begin(), ids.end(), "broken-shader") != ids.end());
    CHECK(std::find(ids.begin(), ids.end(), "broken-json") == ids.end());
    CHECK(std::find(ids.begin(), ids.end(), "missing-shader") == ids.end());
    CHECK(std::is_sorted(ids.begin(), ids.end()));
    CHECK(warnings.size() == 2); // one for the malformed manifest, one for the absent shader
}

TEST_CASE("scanning a root that does not exist is empty rather than an error", "[render][preset]") {
    std::vector<std::string> warnings;
    CHECK(mp::render::scan_preset_root(fs::path{"Z:/no/such/preset/root"}, warnings).empty());
    CHECK(warnings.empty());
    CHECK(mp::render::scan_preset_root(fs::path{}, warnings).empty());
}

TEST_CASE("the default preset root follows MPCORE_PRESET_ROOT when it is set", "[render][preset]") {
    const fs::path override_path = fs::temp_directory_path() / "tunqio-preset-root-probe";
    REQUIRE(SetEnvironmentVariableW(L"MPCORE_PRESET_ROOT", override_path.wstring().c_str()) != 0);
    CHECK(mp::render::default_preset_root() == override_path);
    REQUIRE(SetEnvironmentVariableW(L"MPCORE_PRESET_ROOT", nullptr) != 0);
    // Without it, the root sits beside the binary, which is where the package puts the built-in presets.
    CHECK(mp::render::default_preset_root().filename() == "presets");
}
