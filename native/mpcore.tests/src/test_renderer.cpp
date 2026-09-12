// Headless renderer tests (offscreen target, WARP): creation, frames, resize storm, visibility, and the preset
// loader E4-S3 put behind mp_renderer_enum_presets/set_preset/set_param. The composition swap chain path needs a
// SwapChainPanel and is exercised by the app's --render-spike mode.
//
// These reach past the C ABI in two places, which is what mpcore.tests compiling the core's sources directly is
// for: renderer::capture_pixel, so an assertion can be about what is on screen rather than about a frame counter,
// and renderer::device_references, so "without leaking device resources" is a number.
#include "mpcore.h"

#include "render/renderer.h"
#include "source_root.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <catch2/catch_amalgamated.hpp>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <string>
#include <thread>
#include <vector>

#include <windows.h>

namespace {

// Points the renderer's preset scan at the fixture directory for the life of the object.
struct preset_root_override {
    explicit preset_root_override(const std::filesystem::path& root) {
        REQUIRE(SetEnvironmentVariableW(L"MPCORE_PRESET_ROOT", root.wstring().c_str()) != 0);
    }
    ~preset_root_override() { SetEnvironmentVariableW(L"MPCORE_PRESET_ROOT", nullptr); }
    preset_root_override(const preset_root_override&) = delete;
    preset_root_override& operator=(const preset_root_override&) = delete;
};

std::filesystem::path preset_fixtures() {
    const auto root = mp::tests::find_preset_fixtures();
    if (!root) {
        SKIP("preset fixtures not found (set MPCORE_SOURCE_ROOT)");
    }
    return *root;
}

// The same cast exports_render.cpp makes; the handle is the object.
mp::render::renderer* core(mp_renderer* r) {
    return reinterpret_cast<mp::render::renderer*>(r);
}

struct renderer_fixture {
    mp_renderer* renderer = nullptr;

    explicit renderer_fixture(bool warp, uint32_t width = 640, uint32_t height = 360) {
        mp_renderer_config cfg{};
        cfg.struct_size = sizeof cfg;
        cfg.width = width;
        cfg.height = height;
        cfg.scale_x = 1.0f;
        cfg.scale_y = 1.0f;
        cfg.force_warp = warp ? 1 : 0;
        cfg.vsync = 0; // headless: no pacing, render as fast as possible
        cfg.headless = 1;
        const mp_result r = mp_renderer_create(nullptr, nullptr, &cfg, &renderer);
        if (r != MP_OK) {
            char err[512];
            mp_last_error(err, sizeof err);
            FAIL("mp_renderer_create failed: " << err);
        }
    }
    ~renderer_fixture() {
        if (renderer != nullptr) {
            mp_renderer_destroy(renderer);
        }
    }

    mp_render_stats stats() const {
        mp_render_stats s{};
        s.struct_size = sizeof s;
        REQUIRE(mp_renderer_get_stats(renderer, &s) == MP_OK);
        return s;
    }

    // B, G, R, A of the centre pixel of the next frame the render thread produces.
    std::array<uint8_t, 4> centre_pixel() const {
        const mp_render_stats s = stats();
        std::array<uint8_t, 4> px{};
        REQUIRE(core(renderer)->capture_pixel(s.width / 2, s.height / 2, px.data()));
        return px;
    }
};

std::string last_error() {
    char buf[512];
    mp_last_error(buf, sizeof buf);
    return buf;
}

// Where the frame counter stops moving. A hidden renderer is one the render thread has yet to notice is hidden,
// and the frame it was already drawing still counts.
//
// "Stopped" has to mean quiet for longer than a frame takes, which is why this waits for a stretch rather than
// for one gap. Two equal reads 5 ms apart prove nothing on a machine where a frame takes longer than 5 ms: the
// count is not still, it is merely between frames, and the next one lands during the check that follows. That is
// what CI did - 392 == 391, one frame past a counter this function had called settled (T-134). Headless WARP runs
// at ~800 fps on the dev machine and far slower on a shared runner, so the window is fixed at a value that is
// many frames on either rather than derived from a rate that varies by two orders of magnitude between them.
uint64_t settled_frames(const renderer_fixture& fx, int timeout_ms = 3000, int quiet_ms = 150) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
    uint64_t previous = fx.stats().frames;
    auto quiet_since = std::chrono::steady_clock::now();
    while (std::chrono::steady_clock::now() < deadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(5));
        const uint64_t now = fx.stats().frames;
        if (now != previous) {
            previous = now;
            quiet_since = std::chrono::steady_clock::now();
            continue;
        }

        if (std::chrono::steady_clock::now() - quiet_since >= std::chrono::milliseconds(quiet_ms)) {
            return now;
        }
    }

    return previous;
}

// Waits for the renderer to have drawn `n` frames, rather than sleeping a span and asserting it managed them.
// The difference matters where it is cheapest to get wrong: headless WARP is CPU rendering, so its rate is a
// property of how busy the machine is, not of the renderer. Hammering this suite while a build ran took it to
// 5 frames in 400 ms and 0 in 300 ms - both assertions this replaces, both red, with nothing wrong (T-134). A
// shared CI runner is that machine on an ordinary day.
bool frames_reach_within(const renderer_fixture& fx, uint64_t n, int timeout_ms) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
    while (std::chrono::steady_clock::now() < deadline) {
        if (fx.stats().frames >= n) {
            return true;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    return false;
}

bool frames_advance_within(const renderer_fixture& fx, uint64_t from, int timeout_ms) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(timeout_ms);
    while (std::chrono::steady_clock::now() < deadline) {
        if (fx.stats().frames > from) {
            return true;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    return false;
}

// A scratch directory the test owns, for the user preset root (T-126/AC-133) - presets written while the
// renderer is already running, which is the whole point of a rescan.
struct scratch_dir {
    std::filesystem::path dir;

    explicit scratch_dir(const std::string& name) {
        dir = std::filesystem::temp_directory_path() /
              ("tunqio-renderer-" + name + "-" + std::to_string(GetCurrentProcessId()));
        std::error_code ec;
        std::filesystem::remove_all(dir, ec);
        std::filesystem::create_directories(dir);
    }
    ~scratch_dir() {
        std::error_code ec;
        std::filesystem::remove_all(dir, ec);
    }
    scratch_dir(const scratch_dir&) = delete;
    scratch_dir& operator=(const scratch_dir&) = delete;
};

// A complete, compilable preset in `dir`: one full-screen triangle in red, which no shipped fixture draws, so
// "this preset is the one on the target" is a pixel read rather than a name comparison.
void write_solid_preset(const std::filesystem::path& dir, const std::string& id, const std::string& name) {
    std::filesystem::create_directories(dir);
    std::ofstream{dir / "solid.hlsl", std::ios::binary} << R"hlsl(
struct VSOut { float4 pos : SV_Position; };
VSOut VSMain(uint vid : SV_VertexID) {
    float2 corners[3] = { float2(-1.0, -3.0), float2(-1.0, 1.0), float2(3.0, 1.0) };
    VSOut o;
    o.pos = float4(corners[vid], 0.0, 1.0);
    return o;
}
float4 PSMain(VSOut i) : SV_Target { return float4(1.0, 0.0, 0.0, 1.0); }
)hlsl";
    std::ofstream{dir / "preset.json", std::ios::binary}
        << R"({"schema": 1, "id": ")" << id << R"(", "name": ")" << name
        << R"(", "shader": "solid.hlsl", "vertex_count": 3, "instance_count": 1, "clear": [0.0, 0.0, 0.0, 1.0]})";
}

uint32_t preset_count(const renderer_fixture& fx) {
    uint32_t count = 0;
    REQUIRE(mp_renderer_enum_presets(fx.renderer, nullptr, &count) == MP_OK);
    return count;
}

bool has_preset(const renderer_fixture& fx, const std::string& id) {
    uint32_t count = preset_count(fx);
    if (count == 0) {
        return false;
    }
    std::vector<mp_preset_info> presets(count);
    for (auto& p : presets) {
        p.struct_size = sizeof(mp_preset_info);
    }
    REQUIRE(mp_renderer_enum_presets(fx.renderer, presets.data(), &count) == MP_OK);
    return std::any_of(presets.begin(), presets.begin() + count,
                       [&id](const mp_preset_info& p) { return std::string{p.id} == id; });
}

} // namespace

TEST_CASE("renderer rejects bad arguments", "[render][abi]") {
    mp_renderer* r = nullptr;
    mp_renderer_config cfg{};
    // Since ABI 0.12 a smaller struct_size is an older header, served to its own size; what is refused is a
    // size no header had - longer than this build's, here, and test_abi.cpp has the too-short end.
    cfg.struct_size = sizeof cfg + 1;
    CHECK(mp_renderer_create(nullptr, nullptr, &cfg, &r) == MP_E_INVALID_ARG);
    cfg.struct_size = sizeof cfg;
    cfg.headless = 0;
    CHECK(mp_renderer_create(nullptr, nullptr, &cfg, &r) == MP_E_INVALID_ARG); // no panel and not headless
    CHECK(last_error().find("swap_chain_panel_native") != std::string::npos);
    CHECK(r == nullptr);
}

TEST_CASE("headless WARP renderer produces frames", "[render]") {
    renderer_fixture fx{true};
    // Waited for rather than slept at: six frames is the claim, and how long six frames take is the machine's
    // business. Ten seconds is not a budget, it is long enough that failing it means nothing is being drawn.
    REQUIRE(frames_reach_within(fx, 6, 10000));

    // Stop the loop before reading the counters, because the last assertion in here is about two of them
    // agreeing. get_stats samples frames_ and the histogram as separate loads, and the render thread counts a
    // frame before it buckets that frame's interval, so one landing between the two samples leaves the
    // histogram exactly one ahead - 1 run in 25, seen as 1810 == 1809 (T-119). That is a true fact about
    // sampling a running thread, not a defect in either counter, so the fix is to ask when the answer is
    // stable rather than to weaken the question: paused, every frame has been counted and every interval
    // bucketed. The test above proves set_visible(0) really does stop the loop rather than slow it.
    REQUIRE(mp_renderer_set_visible(fx.renderer, 0) == MP_OK);
    std::this_thread::sleep_for(std::chrono::milliseconds(120));

    const mp_render_stats s = fx.stats();
    CHECK(s.frames > 5); // already waited for above; here it reads back off the paused counter
    CHECK(s.warp == 1);
    CHECK(s.headless == 1);
    CHECK(s.visible == 0); // the pause above, and the reason the two counters below can be compared at all
    CHECK(s.device_lost == 0);
    CHECK(s.width == 640);
    CHECK(s.height == 360);
    CHECK(std::string{s.adapter}.find("Basic Render") != std::string::npos); // "Microsoft Basic Render Driver"
    uint64_t counted = 0;
    for (uint32_t b : s.frame_ms_histogram) {
        counted += b;
    }
    CHECK(counted == s.frames - 1); // every frame-to-frame interval is bucketed: n frames, n-1 gaps
}

TEST_CASE("renderer survives a storm of 100 rapid resizes", "[render][stress]") {
    renderer_fixture fx{true};
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    for (uint32_t i = 0; i < 100; ++i) {
        const uint32_t w = 320 + (i * 37) % 1600;
        const uint32_t h = 180 + (i * 53) % 900;
        const float scale = (i % 2 == 0) ? 1.0f : 1.5f; // simulates a DPI change between monitors
        REQUIRE(mp_renderer_resize(fx.renderer, w, h, scale, scale) == MP_OK);
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
    const mp_render_stats s = fx.stats();
    CHECK(s.device_lost == 0);
    CHECK(s.resizes >= 1); // coalesced: the render thread applies the latest pending size
    CHECK(s.frames > 10);
    const uint64_t before = s.frames;
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
    CHECK(fx.stats().frames > before); // still rendering afterwards
}

TEST_CASE("hidden renderer stops rendering and resumes", "[render]") {
    renderer_fixture fx{true};
    std::this_thread::sleep_for(std::chrono::milliseconds(150));
    REQUIRE(mp_renderer_set_visible(fx.renderer, 0) == MP_OK);
    // Settled rather than slept at, for the reason settled_frames gives: a fixed wait is a guess about how long
    // a frame takes on a machine nobody has measured, and this test has the same shape as the one T-134 caught.
    const uint64_t paused = settled_frames(fx);
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
    CHECK(fx.stats().frames == paused);
    CHECK(fx.stats().visible == 0);
    REQUIRE(mp_renderer_set_visible(fx.renderer, 1) == MP_OK);
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
    CHECK(fx.stats().frames > paused);
}

TEST_CASE("hardware renderer falls back to WARP when no adapter exists", "[render]") {
    // On a machine with a GPU this exercises the hardware path; on a CI runner it exercises the fallback.
    renderer_fixture fx{false};
    // Same reason as above: that a frame is drawn at all is the claim, not that one is drawn inside 300 ms.
    CHECK(frames_reach_within(fx, 1, 10000));
    const mp_render_stats s = fx.stats();
    CHECK(s.frames > 0);
    CHECK(std::string{s.adapter}.size() > 0);
}

TEST_CASE("the quality export still names its story", "[render][abi]") {
    renderer_fixture fx{true};
    CHECK(mp_renderer_set_quality(fx.renderer, MP_QUALITY_AUTO) == MP_E_STATE);
    CHECK(last_error().find("E4-S7") != std::string::npos);
}

// ---- E4-S6: the renderer-wide theme ---------------------------------------------------------------

namespace {

// Four colours chosen so that no two channels of any two of them are equal and none of them is a value a ramp
// in this repo happens to produce: a pixel carrying one of these carries it from set_theme and nowhere else.
// Every number is a multiple of 1/255 so it survives the 8-bit UNORM target exactly.
constexpr float k_theme[4][4] = {
    {200.0f / 255, 30.0f / 255, 40.0f / 255, 255.0f / 255},  // primary
    {10.0f / 255, 160.0f / 255, 90.0f / 255, 128.0f / 255},  // secondary
    {70.0f / 255, 20.0f / 255, 220.0f / 255, 64.0f / 255},   // accent
    {240.0f / 255, 210.0f / 255, 50.0f / 255, 192.0f / 255}, // background
};

mp_theme_colors theme_of(const float rgba[4][4], uint32_t struct_size = sizeof(mp_theme_colors)) {
    mp_theme_colors c{};
    c.struct_size = struct_size;
    std::memcpy(c.primary, rgba[0], sizeof c.primary);
    std::memcpy(c.secondary, rgba[1], sizeof c.secondary);
    std::memcpy(c.accent, rgba[2], sizeof c.accent);
    std::memcpy(c.background, rgba[3], sizeof c.background);
    return c;
}

// The theme-probe fixture paints colour `which` into the `which`-th column: RGB in the top half, alpha in all
// three channels in the bottom half. Returns {r, g, b, a} as bytes, which is what the fixture makes readable.
std::array<int, 4> probe_colour(const renderer_fixture& fx, int which) {
    const mp_render_stats s = fx.stats();
    const uint32_t x = (s.width * (2u * static_cast<uint32_t>(which) + 1u)) / 8u;
    std::array<uint8_t, 4> top{};
    std::array<uint8_t, 4> bottom{};
    REQUIRE(core(fx.renderer)->capture_pixel(x, s.height / 4, top.data()));
    REQUIRE(core(fx.renderer)->capture_pixel(x, (s.height * 3) / 4, bottom.data()));
    return {top[2], top[1], top[0], bottom[1]}; // capture_pixel is B, G, R, A
}

std::array<int, 4> expected_bytes(const float rgba[4]) {
    std::array<int, 4> out{};
    for (size_t i = 0; i < 4; ++i) {
        out[i] = static_cast<int>(std::lround(rgba[i] * 255.0f));
    }
    return out;
}

} // namespace

TEST_CASE("all sixteen floats of the theme reach the shader through b0", "[render][theme]") {
    const preset_root_override root{preset_fixtures()};
    renderer_fixture fx{true, 64, 64};
    REQUIRE(mp_renderer_set_preset(fx.renderer, "theme-probe") == MP_OK);

    // Before anything sets one, every channel is zero - which is what alpha 0 means to a preset: "the shell has
    // not told me a theme". Proved before setting one, so the assertions below cannot be reading a default.
    for (int i = 0; i < 4; ++i) {
        CHECK(probe_colour(fx, i) == std::array<int, 4>{0, 0, 0, 0});
    }

    const mp_theme_colors colors = theme_of(k_theme);
    REQUIRE(mp_renderer_set_theme(fx.renderer, &colors) == MP_OK);

    for (int i = 0; i < 4; ++i) {
        INFO("colour " << i);
        const auto expected = expected_bytes(k_theme[i]);
        const auto got = probe_colour(fx, i);
        for (size_t ch = 0; ch < 4; ++ch) {
            INFO("channel " << ch);
            CHECK(std::abs(got[ch] - expected[ch]) <= 1);
        }
    }
}

TEST_CASE("the theme outlives a preset switch, unlike a parameter", "[render][theme]") {
    const preset_root_override root{preset_fixtures()};
    renderer_fixture fx{true, 64, 64};
    REQUIRE(mp_renderer_set_preset(fx.renderer, "theme-probe") == MP_OK);
    const mp_theme_colors colors = theme_of(k_theme);
    REQUIRE(mp_renderer_set_theme(fx.renderer, &colors) == MP_OK);
    REQUIRE(probe_colour(fx, 0)[0] == 200);

    // Away and back. A parameter would be at its default again; the theme belongs to the renderer, not to a
    // preset, because the shell's gradient does not change colour when someone picks a different visualizer.
    REQUIRE(mp_renderer_set_preset(fx.renderer, "solid-green") == MP_OK);
    REQUIRE(fx.centre_pixel()[1] == 255);
    REQUIRE(mp_renderer_set_preset(fx.renderer, "theme-probe") == MP_OK);

    for (int i = 0; i < 4; ++i) {
        INFO("colour " << i);
        CHECK(probe_colour(fx, i) == expected_bytes(k_theme[i]));
    }
}

TEST_CASE("a schema 1 preset still draws against the longer b0", "[render][theme][preset]") {
    // solid-green declares schema 1 and a cbuffer block without `theme`. Appending a field to the end of b0 is
    // a schema bump precisely so this stays true: nothing before `theme` moved, so its shader reads what it
    // always read out of a buffer that is merely longer than the block it names.
    const preset_root_override root{preset_fixtures()};
    renderer_fixture fx{true, 64, 64};
    REQUIRE(mp_renderer_set_preset(fx.renderer, "solid-green") == MP_OK);
    const mp_theme_colors colors = theme_of(k_theme);
    REQUIRE(mp_renderer_set_theme(fx.renderer, &colors) == MP_OK);
    REQUIRE(mp_renderer_set_param(fx.renderer, "level", 1.0f) == MP_OK);

    const auto px = fx.centre_pixel();
    CHECK(static_cast<int>(px[1]) == 255); // green, exactly as before the theme existed
    CHECK(static_cast<int>(px[0]) == 0);
    CHECK(static_cast<int>(px[2]) == 0);
}

TEST_CASE("set_theme validates its colours", "[render][theme][abi]") {
    const preset_root_override root{preset_fixtures()};
    renderer_fixture fx{true, 64, 64};
    REQUIRE(mp_renderer_set_preset(fx.renderer, "theme-probe") == MP_OK);

    SECTION("a channel outside 0..1 is clamped to it, as a parameter is clamped to its declared range") {
        float wild[4][4] = {{4.0f, -2.0f, 0.5f, 1.0f}, {0, 0, 0, 0}, {0, 0, 0, 0}, {0, 0, 0, 0}};
        const mp_theme_colors colors = theme_of(wild);
        REQUIRE(mp_renderer_set_theme(fx.renderer, &colors) == MP_OK);
        const auto got = probe_colour(fx, 0);
        CHECK(got[0] == 255);
        CHECK(got[1] == 0);
        CHECK(std::abs(got[2] - 128) <= 2);
    }

    SECTION("a channel that is not a finite number is refused and nothing changes") {
        const mp_theme_colors good = theme_of(k_theme);
        REQUIRE(mp_renderer_set_theme(fx.renderer, &good) == MP_OK);
        REQUIRE(probe_colour(fx, 0)[0] == 200);

        float broken[4][4] = {{0.1f, 0.1f, 0.1f, 1.0f}, {0, 0, 0, 0}, {0, 0, 0, 0}, {0, 0, 0, 0}};
        broken[2][1] = std::numeric_limits<float>::quiet_NaN();
        const mp_theme_colors colors = theme_of(broken);
        CHECK(mp_renderer_set_theme(fx.renderer, &colors) == MP_E_INVALID_ARG);
        CHECK(last_error().find("finite") != std::string::npos);
        CHECK(probe_colour(fx, 0)[0] == 200); // the theme that was drawing is still drawing
    }

    SECTION("a NULL renderer or a NULL struct is MP_E_INVALID_ARG") {
        const mp_theme_colors colors = theme_of(k_theme);
        CHECK(mp_renderer_set_theme(nullptr, &colors) == MP_E_INVALID_ARG);
        CHECK(mp_renderer_set_theme(fx.renderer, nullptr) == MP_E_INVALID_ARG);
    }
}

TEST_CASE("a caller that carries only the first colour is served that prefix", "[render][theme][abi]") {
    // The struct_size rule (T-140, ABI 0.12) in the one export that landed after it: a shell built against a
    // header where mp_theme_colors stopped after `primary` sends 20 bytes, and the three colours its header did
    // not have take their documented zero default rather than whatever was behind its struct.
    const preset_root_override root{preset_fixtures()};
    renderer_fixture fx{true, 64, 64};
    REQUIRE(mp_renderer_set_preset(fx.renderer, "theme-probe") == MP_OK);

    const uint32_t prefix =
        static_cast<uint32_t>(offsetof(mp_theme_colors, primary) + sizeof(mp_theme_colors::primary));
    const mp_theme_colors colors = theme_of(k_theme, prefix);
    REQUIRE(mp_renderer_set_theme(fx.renderer, &colors) == MP_OK);

    CHECK(probe_colour(fx, 0) == expected_bytes(k_theme[0]));
    for (int i = 1; i < 4; ++i) {
        INFO("colour " << i);
        CHECK(probe_colour(fx, i) == std::array<int, 4>{0, 0, 0, 0});
    }

    // And the other direction is still refused: a struct longer than this build's asks for fields it cannot fill.
    mp_theme_colors too_long = theme_of(k_theme, sizeof(mp_theme_colors) + 4u);
    CHECK(mp_renderer_set_theme(fx.renderer, &too_long) == MP_E_INVALID_ARG);
}

// ---- E4-S3: the preset loader --------------------------------------------------------------------

TEST_CASE("a renderer with no preset directory still enumerates and draws the built-in", "[render][preset]") {
    const preset_root_override root{std::filesystem::temp_directory_path() / "tunqio-no-such-preset-root"};
    renderer_fixture fx{true, 64, 64};

    uint32_t count = 0;
    REQUIRE(mp_renderer_enum_presets(fx.renderer, nullptr, &count) == MP_OK);
    REQUIRE(count == 1);
    std::vector<mp_preset_info> presets(count);
    presets[0].struct_size = sizeof(mp_preset_info);
    REQUIRE(mp_renderer_enum_presets(fx.renderer, presets.data(), &count) == MP_OK);
    CHECK(count == 1);
    CHECK(std::string{presets[0].id} == "builtin-bars");
    CHECK(std::string{presets[0].name}.size() > 0);
    CHECK(core(fx.renderer)->active_preset_id() == "builtin-bars");
    CHECK(fx.stats().frames >= 0);
}

TEST_CASE("presets on disk are enumerated beside the built-in", "[render][preset]") {
    const preset_root_override root{preset_fixtures()};
    renderer_fixture fx{true, 64, 64};

    uint32_t count = 0;
    REQUIRE(mp_renderer_enum_presets(fx.renderer, nullptr, &count) == MP_OK);
    // built-in + broken-shader + param-metadata + solid-blue + solid-green + theme-probe; the two malformed
    // ones are skipped
    CHECK(count == 6);

    std::vector<mp_preset_info> presets(count);
    for (auto& p : presets) {
        p.struct_size = sizeof(mp_preset_info);
    }
    REQUIRE(mp_renderer_enum_presets(fx.renderer, presets.data(), &count) == MP_OK);
    std::vector<std::string> ids;
    for (const auto& p : presets) {
        ids.emplace_back(p.id);
    }
    CHECK(std::find(ids.begin(), ids.end(), "solid-green") != ids.end());
    CHECK(std::find(ids.begin(), ids.end(), "broken-json") == ids.end());

    SECTION("a buffer smaller than the catalogue is filled and says how many it took") {
        uint32_t room = 2;
        std::vector<mp_preset_info> two(2);
        two[0].struct_size = sizeof(mp_preset_info);
        two[1].struct_size = sizeof(mp_preset_info);
        REQUIRE(mp_renderer_enum_presets(fx.renderer, two.data(), &room) == MP_OK);
        CHECK(room == 2);
    }

    SECTION("a caller whose mp_preset_info stops after the id is served at its own element size") {
        // An older header's element, so the caller's array is shorter than one of this build's: out[0] names
        // the element size and so the stride, and nothing past the last element may be written (T-140).
        constexpr auto k_element =
            static_cast<uint32_t>(offsetof(mp_preset_info, id) + sizeof(mp_preset_info::id)); // no `name`
        std::vector<unsigned char> storage(static_cast<size_t>(count) * k_element + 16, 0xCD);
        auto* out = reinterpret_cast<mp_preset_info*>(storage.data());
        out->struct_size = k_element;
        uint32_t room = count;
        REQUIRE(mp_renderer_enum_presets(fx.renderer, out, &room) == MP_OK);
        CHECK(room == count);
        for (uint32_t i = 0; i < room; ++i) {
            const auto* element =
                reinterpret_cast<const mp_preset_info*>(storage.data() + static_cast<size_t>(i) * k_element);
            INFO("element " << i);
            CHECK(element->struct_size == k_element);
            CHECK(std::string{element->id} == ids[i]);
        }
        for (size_t i = static_cast<size_t>(room) * k_element; i < storage.size(); ++i) {
            INFO("byte " << i << ", past the last element");
            REQUIRE(storage[i] == 0xCD);
        }
    }

    SECTION("an unknown id is refused by name and changes nothing") {
        CHECK(mp_renderer_set_preset(fx.renderer, "no-such-preset") == MP_E_INVALID_ARG);
        CHECK(last_error().find("no-such-preset") != std::string::npos);
        CHECK(core(fx.renderer)->active_preset_id() == "builtin-bars");
    }
}

// AC-117. The assertion that matters is the last one: the previous preset is still the one being drawn, checked
// by reading the pixel back off the render target, not by trusting that nothing threw.
TEST_CASE("a preset whose shader will not compile is refused and the previous one keeps drawing", "[render][preset]") {
    const preset_root_override root{preset_fixtures()};
    renderer_fixture fx{true, 64, 64};

    REQUIRE(mp_renderer_set_preset(fx.renderer, "solid-green") == MP_OK);
    auto px = fx.centre_pixel();
    REQUIRE(px[1] == 255); // green, so there is something specific to lose
    REQUIRE(px[0] == 0);
    REQUIRE(px[2] == 0);

    CHECK(mp_renderer_set_preset(fx.renderer, "broken-shader") == MP_E_D3D);
    const std::string error = last_error();
    INFO("mp_last_error after the broken preset: " << error);
    CHECK(error.find("broken-shader") != std::string::npos);                  // which preset
    CHECK(error.find("error X") != std::string::npos);                        // the compiler's own code
    CHECK(error.find("colour_that_was_never_declared") != std::string::npos); // and what is wrong with it

    // Several frames later, the green preset is still the active one and still the one on the target.
    CHECK(core(fx.renderer)->active_preset_id() == "solid-green");
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    px = fx.centre_pixel();
    CHECK(px[1] == 255);
    CHECK(px[0] == 0);
    CHECK(px[2] == 0);
    CHECK(fx.stats().device_lost == 0);

    SECTION("and a good preset still loads afterwards, so the failure left nothing behind") {
        REQUIRE(mp_renderer_set_preset(fx.renderer, "solid-blue") == MP_OK);
        const auto after = fx.centre_pixel();
        CHECK(after[0] == 255); // blue
        CHECK(after[1] == 0);
    }
}

TEST_CASE("a preset parameter reaches the shader, and one it does not declare is refused", "[render][preset]") {
    const preset_root_override root{preset_fixtures()};
    renderer_fixture fx{true, 64, 64};
    REQUIRE(mp_renderer_set_preset(fx.renderer, "solid-green") == MP_OK);

    REQUIRE(mp_renderer_set_param(fx.renderer, "level", 0.5f) == MP_OK);
    auto px = fx.centre_pixel();
    CHECK(static_cast<int>(px[1]) >= 126); // 0.5 through an 8-bit UNORM target
    CHECK(static_cast<int>(px[1]) <= 129);

    // Out of the declared range, clamped rather than refused: a preset says what its parameter means.
    REQUIRE(mp_renderer_set_param(fx.renderer, "level", 9.0f) == MP_OK);
    px = fx.centre_pixel();
    CHECK(static_cast<int>(px[1]) == 255);

    CHECK(mp_renderer_set_param(fx.renderer, "not-declared", 1.0f) == MP_E_INVALID_ARG);
    CHECK(last_error().find("not-declared") != std::string::npos);
    CHECK(last_error().find("level") != std::string::npos); // says what it does declare

    REQUIRE(mp_renderer_set_preset(fx.renderer, "solid-blue") == MP_OK);
    CHECK(mp_renderer_set_param(fx.renderer, "level", 1.0f) == MP_E_INVALID_ARG); // the new preset declares none
    CHECK(last_error().find("declares none") != std::string::npos);
}

// AC-118. "Without leaking device resources" as a number: every ID3D11DeviceChild holds a reference on the
// device, so the device's own reference count is a live count of the objects that exist. The first block proves
// the instrument moves before the rest asks it to stay still.
TEST_CASE("toggling visibility repeatedly leaks no device resources", "[render][stress]") {
    const preset_root_override root{preset_fixtures()};
    renderer_fixture fx{true, 320, 180};
    auto* r = core(fx.renderer);

    // Warm up everything created lazily - the capture staging texture, and a preset that is not the built-in.
    REQUIRE(mp_renderer_set_preset(fx.renderer, "solid-green") == MP_OK);
    REQUIRE(fx.centre_pixel()[1] == 255);

    const uint32_t baseline = r->device_references();
    REQUIRE(baseline > 0);
    {
        Microsoft::WRL::ComPtr<ID3D11Buffer> probe;
        D3D11_BUFFER_DESC bd{};
        bd.ByteWidth = 16;
        bd.Usage = D3D11_USAGE_DEFAULT;
        bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        REQUIRE(SUCCEEDED(r->device()->CreateBuffer(&bd, nullptr, &probe)));
        REQUIRE(r->device_references() > baseline); // one more device child, one more reference
    }
    REQUIRE(r->device_references() == baseline); // and back down when it goes

    for (int i = 0; i < 200; ++i) {
        REQUIRE(mp_renderer_set_visible(fx.renderer, 0) == MP_OK);
        REQUIRE(mp_renderer_set_visible(fx.renderer, 1) == MP_OK);
    }
    // Slower toggles too, so the render thread really parks and really wakes rather than never observing the
    // hidden state at all. Waited on rather than slept at: set_visible is asynchronous by construction - the
    // render thread notices between frames - so a fixed sleep would be asserting a scheduling guarantee the
    // renderer does not make, and would flake on the slow configurations for the wrong reason.
    for (int i = 0; i < 20; ++i) {
        REQUIRE(mp_renderer_set_visible(fx.renderer, 0) == MP_OK);
        const uint64_t parked = settled_frames(fx);
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        CHECK(fx.stats().frames == parked); // stopped, and stayed stopped
        REQUIRE(mp_renderer_set_visible(fx.renderer, 1) == MP_OK);
        CHECK(frames_advance_within(fx, parked, 1000)); // and restarted
    }

    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    CHECK(r->device_references() == baseline);
    CHECK(r->device()->GetDeviceRemovedReason() == S_OK);
    CHECK(fx.stats().device_lost == 0);
    CHECK(fx.centre_pixel()[1] == 255); // still drawing the preset it had before the storm
}

// AC-119. WARP is the software rasteriser, so this number is honest on any machine; what it does not tell you is
// what the reference iGPU does, which is E4-S4's business. The figure is printed so a run is a measurement.
TEST_CASE("WARP renders at 1080p above 30 fps", "[render][perf]") {
    const preset_root_override root{preset_fixtures()};
    renderer_fixture fx{true, 1920, 1080};
    REQUIRE(fx.stats().warp == 1);

    std::this_thread::sleep_for(std::chrono::milliseconds(400)); // first frames pay for lazy driver work
    const uint64_t before = fx.stats().frames;
    const auto start = std::chrono::steady_clock::now();
    std::this_thread::sleep_for(std::chrono::milliseconds(2000));
    const uint64_t after = fx.stats().frames;
    const double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
    const double fps = static_cast<double>(after - before) / seconds;

    char note[256];
    std::snprintf(note, sizeof note, "WARP 1920x1080, built-in preset: %.1f fps over %.2f s (%llu frames), adapter %s",
                  fps, seconds, static_cast<unsigned long long>(after - before), fx.stats().adapter);
    WARN(note); // not a failure: a [perf] test that does not print its number is not a measurement
    CHECK(fps >= 30.0);
    CHECK(fx.stats().device_lost == 0);
}

// ---- T-142: parameter metadata over the ABI ---------------------------------------------------------------
//
// The point of these is what a settings page can do with them and could not do before: build a control for a
// preset it has never seen, including a user's own, without a table of ranges compiled into it.

TEST_CASE("a preset's parameters are enumerable with the metadata a settings page needs", "[render][preset][params]") {
    const preset_root_override root{preset_fixtures()};
    renderer_fixture fx{true, 64, 64};

    uint32_t count = 0;
    REQUIRE(mp_renderer_enum_preset_params(fx.renderer, "param-metadata", nullptr, &count) == MP_OK);
    REQUIRE(count == 6);

    std::vector<mp_preset_param_info> params(count);
    for (auto& p : params) {
        p.struct_size = sizeof(mp_preset_param_info);
    }
    REQUIRE(mp_renderer_enum_preset_params(fx.renderer, "param-metadata", params.data(), &count) == MP_OK);
    REQUIRE(count == 6);

    SECTION("the range, the default and the step come off the manifest, not out of the caller") {
        CHECK(std::string{params[1].name} == "count");
        CHECK(std::string{params[1].label} == "Count");
        CHECK(params[1].min_value == Catch::Approx(8.0f));
        CHECK(params[1].max_value == Catch::Approx(128.0f));
        CHECK(params[1].default_value == Catch::Approx(64.0f));
        CHECK(params[1].step == Catch::Approx(1.0f));
        CHECK(params[1].flags == MP_PARAM_NONE);
    }

    SECTION("a unit reaches the page, and a parameter with no label is labelled by its name") {
        CHECK(std::string{params[2].unit} == "px");
        CHECK(std::string{params[4].name} == "bare");
        CHECK(std::string{params[4].label} == "bare");
        CHECK(std::string{params[4].unit}.empty());
    }

    SECTION("a mode arrives as its labels rather than as a number between two numbers") {
        CHECK(std::string{params[3].name} == "mode");
        CHECK((params[3].flags & MP_PARAM_CHOICE) != 0u);
        CHECK(std::string{params[3].choices} == "First|Second|Third");
        CHECK(params[3].step == Catch::Approx(1.0f));
    }

    SECTION("the hidden flag is what keeps a packed sRGB integer off a settings page") {
        CHECK(std::string{params[5].name} == "art_primary");
        CHECK((params[5].flags & MP_PARAM_HIDDEN) != 0u);
        // And it is the exception rather than the rule, or the flag would be a way to lose a preset's surface.
        for (uint32_t i = 0; i < 5; ++i) {
            INFO("parameter " << params[i].name);
            CHECK((params[i].flags & MP_PARAM_HIDDEN) == 0u);
        }
        CHECK(std::string{params[5].choices}.empty());
    }

    SECTION("a preset that declares none answers zero rather than refusing") {
        uint32_t none = 0;
        REQUIRE(mp_renderer_enum_preset_params(fx.renderer, "theme-probe", nullptr, &none) == MP_OK);
        CHECK(none == 0);
    }

    SECTION("an unknown preset is refused by name") {
        uint32_t n = 0;
        CHECK(mp_renderer_enum_preset_params(fx.renderer, "no-such-preset", nullptr, &n) == MP_E_INVALID_ARG);
        CHECK(last_error().find("no-such-preset") != std::string::npos);
    }

    SECTION("the answer is about the preset asked for, not the one that is drawing") {
        // The whole reason this takes an id: a settings page describes a preset before switching to it, and
        // the renderer is still on the built-in here.
        REQUIRE(core(fx.renderer)->active_preset_id() == "builtin-bars");
        uint32_t green = 0;
        REQUIRE(mp_renderer_enum_preset_params(fx.renderer, "solid-green", nullptr, &green) == MP_OK);
        CHECK(green == 1);
    }

    SECTION("a caller whose mp_preset_param_info stops after the name is served at its own element size") {
        // The T-140 rule on a struct that did not exist when it was written: out[0].struct_size is the stride,
        // the prefix is filled and the bytes past the last element are not the callee's to touch.
        constexpr auto k_element =
            static_cast<uint32_t>(offsetof(mp_preset_param_info, name) + sizeof(mp_preset_param_info::name));
        std::vector<unsigned char> storage(static_cast<size_t>(count) * k_element + 16, 0xCD);
        auto* out = reinterpret_cast<mp_preset_param_info*>(storage.data());
        out->struct_size = k_element;
        uint32_t room = count;
        REQUIRE(mp_renderer_enum_preset_params(fx.renderer, "param-metadata", out, &room) == MP_OK);
        CHECK(room == count);
        for (uint32_t i = 0; i < room; ++i) {
            const auto* element =
                reinterpret_cast<const mp_preset_param_info*>(storage.data() + static_cast<size_t>(i) * k_element);
            INFO("element " << i);
            CHECK(element->struct_size == k_element);
            CHECK(std::string{element->name} == std::string{params[i].name});
        }
        for (size_t i = static_cast<size_t>(room) * k_element; i < storage.size(); ++i) {
            INFO("byte " << i << ", past the last element");
            REQUIRE(storage[i] == 0xCD);
        }
    }

    SECTION("a struct_size larger than this build's is refused, as every other struct's is") {
        std::vector<unsigned char> storage(sizeof(mp_preset_param_info) * 2, 0);
        auto* out = reinterpret_cast<mp_preset_param_info*>(storage.data());
        out->struct_size = sizeof(mp_preset_param_info) + 8u;
        uint32_t room = 1;
        CHECK(mp_renderer_enum_preset_params(fx.renderer, "param-metadata", out, &room) == MP_E_INVALID_ARG);
        CHECK(last_error().find("newer mpcore.h") != std::string::npos);
    }
}

TEST_CASE("every shipped preset describes its own parameters", "[render][preset][params]") {
    // Against the real presets/ rather than the fixtures, because this is the claim the settings page depends
    // on: no shipped parameter reaches a person without a label, and the three set from the album art palette
    // are the only ones that do not reach a person at all.
    const auto shipped = mp::tests::find_shipped_presets();
    if (!shipped) {
        SKIP("the shipped preset root was not found (set MPCORE_SOURCE_ROOT)");
    }
    const preset_root_override root{*shipped};
    renderer_fixture fx{true, 64, 64};

    uint32_t total = 0;
    REQUIRE(mp_renderer_enum_presets(fx.renderer, nullptr, &total) == MP_OK);
    std::vector<mp_preset_info> presets(total);
    for (auto& p : presets) {
        p.struct_size = sizeof(mp_preset_info);
    }
    REQUIRE(mp_renderer_enum_presets(fx.renderer, presets.data(), &total) == MP_OK);
    CHECK(total == 5); // the four built-ins plus the compiled-in one

    int hidden_seen = 0;
    int offered = 0;
    for (const auto& preset : presets) {
        uint32_t n = 0;
        REQUIRE(mp_renderer_enum_preset_params(fx.renderer, preset.id, nullptr, &n) == MP_OK);
        std::vector<mp_preset_param_info> params(n);
        for (auto& p : params) {
            p.struct_size = sizeof(mp_preset_param_info);
        }
        if (n > 0) {
            REQUIRE(mp_renderer_enum_preset_params(fx.renderer, preset.id, params.data(), &n) == MP_OK);
        }
        for (const auto& p : params) {
            INFO(preset.id << "." << p.name);
            CHECK(std::string{p.label}.size() > 0);
            CHECK(p.min_value <= p.max_value);
            CHECK(p.default_value >= p.min_value);
            CHECK(p.default_value <= p.max_value);
            if ((p.flags & MP_PARAM_HIDDEN) != 0u) {
                ++hidden_seen;
                CHECK(std::string{p.name}.rfind("art_", 0) == 0);
            } else {
                ++offered;
                // The claim this exists for: nothing a person is offered has a range they could not reason
                // about. 16777215 is a packed colour, and no visible parameter carries one.
                CHECK(p.max_value <= 1000.0f);
            }
        }
        if (std::string{preset.id} != "builtin-bars") {
            // Every preset on disk declares a colour source, and it arrives as named modes rather than 0..2.
            const auto colour = std::find_if(params.begin(), params.end(), [](const mp_preset_param_info& p) {
                return std::string{p.name} == "colour";
            });
            REQUIRE(colour != params.end());
            CHECK(std::string{colour->choices} == "Position|Loudness|Spectral centroid");
        }
    }
    CHECK(hidden_seen == 3); // ambient-glow's art_primary / art_secondary / art_accent, and nothing else
    // builtin-bars gain, spectrum-bars 4, waveform 5, radial-spectrum 5, ambient-glow 5 of its 8.
    CHECK(offered == 20);
}

// ---- T-126 / AC-133: a second root, and a refresh -----------------------------------------------------------

TEST_CASE("a user preset root is scanned in addition to the shipped one", "[render][preset][rescan]") {
    const preset_root_override root{preset_fixtures()};
    const scratch_dir user{"user-root"};
    renderer_fixture fx{true, 64, 64};

    const uint32_t shipped = preset_count(fx);
    REQUIRE(shipped > 1);

    SECTION("a preset dropped in before the root is named is there as soon as it is") {
        write_solid_preset(user.dir / "user-solid", "user-solid", "User Solid (scratch)");
        REQUIRE(mp_renderer_set_user_preset_root(fx.renderer, user.dir.string().c_str()) == MP_OK);
        CHECK(preset_count(fx) == shipped + 1);
        CHECK(has_preset(fx, "user-solid"));

        SECTION("and it is a preset like any other: it loads, and it draws") {
            REQUIRE(mp_renderer_set_preset(fx.renderer, "user-solid") == MP_OK);
            const auto px = fx.centre_pixel();
            CHECK(px[2] == 255); // red, which no shipped fixture draws
            CHECK(px[1] == 0);
        }
    }

    SECTION("a preset dropped in while the renderer runs appears on a rescan and not before it") {
        REQUIRE(mp_renderer_set_user_preset_root(fx.renderer, user.dir.string().c_str()) == MP_OK);
        CHECK(preset_count(fx) == shipped);

        write_solid_preset(user.dir / "late-arrival", "late-arrival", "Late Arrival (scratch)");
        // T-126's whole point: the catalogue is read once, so without the refresh it is still yesterday's.
        CHECK_FALSE(has_preset(fx, "late-arrival"));

        uint32_t after = 0;
        REQUIRE(mp_renderer_rescan_presets(fx.renderer, &after) == MP_OK);
        CHECK(after == shipped + 1);
        CHECK(has_preset(fx, "late-arrival"));
    }

    SECTION("the preset that is drawing keeps drawing across a rescan, even one that removes it") {
        write_solid_preset(user.dir / "doomed", "doomed", "Doomed (scratch)");
        REQUIRE(mp_renderer_set_user_preset_root(fx.renderer, user.dir.string().c_str()) == MP_OK);
        REQUIRE(mp_renderer_set_preset(fx.renderer, "doomed") == MP_OK);
        REQUIRE(fx.centre_pixel()[2] == 255);

        std::error_code ec;
        std::filesystem::remove_all(user.dir / "doomed", ec);
        REQUIRE(mp_renderer_rescan_presets(fx.renderer, nullptr) == MP_OK);
        CHECK_FALSE(has_preset(fx, "doomed"));

        // Already compiled, so it is still on the target. Taking the picture away to punish someone for moving
        // a file would be a black rectangle where a visualizer was.
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        const auto px = fx.centre_pixel();
        CHECK(px[2] == 255);
        CHECK(core(fx.renderer)->active_preset_id() == "doomed");
        CHECK(fx.stats().device_lost == 0);
    }

    SECTION("a user preset claiming a shipped id is refused the id, so a file cannot shadow a built-in") {
        write_solid_preset(user.dir / "impostor", "solid-green", "Not the real solid-green");
        REQUIRE(mp_renderer_set_user_preset_root(fx.renderer, user.dir.string().c_str()) == MP_OK);
        CHECK(preset_count(fx) == shipped); // it was skipped, not added and not substituted
        REQUIRE(mp_renderer_set_preset(fx.renderer, "solid-green") == MP_OK);
        const auto px = fx.centre_pixel();
        CHECK(px[1] == 255); // the shipped green, not the impostor's red
        CHECK(px[2] == 0);
    }

    SECTION("a manifest that will not parse costs the user that preset and not the others") {
        write_solid_preset(user.dir / "good-one", "good-one", "Good (scratch)");
        std::filesystem::create_directories(user.dir / "bad-one");
        std::ofstream{user.dir / "bad-one" / "preset.json", std::ios::binary} << "{ this is not json";
        REQUIRE(mp_renderer_set_user_preset_root(fx.renderer, user.dir.string().c_str()) == MP_OK);
        CHECK(preset_count(fx) == shipped + 1);
        CHECK(has_preset(fx, "good-one"));
    }

    SECTION("an empty path removes the second root again") {
        write_solid_preset(user.dir / "temporary", "temporary", "Temporary (scratch)");
        REQUIRE(mp_renderer_set_user_preset_root(fx.renderer, user.dir.string().c_str()) == MP_OK);
        REQUIRE(preset_count(fx) == shipped + 1);
        REQUIRE(mp_renderer_set_user_preset_root(fx.renderer, "") == MP_OK);
        CHECK(preset_count(fx) == shipped);
        REQUIRE(mp_renderer_set_user_preset_root(fx.renderer, nullptr) == MP_OK);
        CHECK(preset_count(fx) == shipped);
    }

    SECTION("a root that does not exist is an empty second root rather than an error") {
        REQUIRE(mp_renderer_set_user_preset_root(fx.renderer, "Z:\\no\\such\\user\\presets") == MP_OK);
        CHECK(preset_count(fx) == shipped);
    }
}

// ---- AC-132: no black frame across a switch ------------------------------------------------------------------
//
// The 200 ms half of AC-132 is a Tunqio.Benchmarks [Budget] gate, because a single stopwatch reading on a machine
// that is also building is how T-119, T-134 and T-150 went red with nothing wrong. What belongs here is the half a
// number cannot express: that there is no frame between the two presets in which nothing is drawn.
//
// "Blank" is defined against the preset's own clear colour rather than against black, and both fixtures here
// clear to black and then cover the frame, so a dropped frame is unmistakably a third reading.
TEST_CASE("no frame between two presets is blank", "[render][preset][switch]") {
    const preset_root_override root{preset_fixtures()};
    renderer_fixture fx{true, 160, 90};
    REQUIRE(mp_renderer_set_preset(fx.renderer, "solid-green") == MP_OK);

    int frames = 0;
    int green = 0;
    int blue = 0;
    std::atomic<bool> switched{false};
    std::atomic<bool> flip_failed{false};
    std::thread flipper{[&] {
        for (int i = 0; i < 6; ++i) {
            std::this_thread::sleep_for(std::chrono::milliseconds(15));
            const char* id = (i % 2 == 0) ? "solid-blue" : "solid-green";
            if (mp_renderer_set_preset(fx.renderer, id) != MP_OK) {
                flip_failed.store(true);
                break;
            }
        }
        switched.store(true);
    }};

    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    while (std::chrono::steady_clock::now() < deadline && (!switched.load() || frames < 20)) {
        std::array<uint8_t, 4> px{};
        if (!core(fx.renderer)->capture_pixel(80, 45, px.data())) {
            break;
        }
        ++frames;
        const bool is_green = px[1] > 200 && px[0] < 40 && px[2] < 40;
        const bool is_blue = px[0] > 200 && px[1] < 40 && px[2] < 40;
        green += is_green ? 1 : 0;
        blue += is_blue ? 1 : 0;
        INFO("frame " << frames << " read B=" << int(px[0]) << " G=" << int(px[1]) << " R=" << int(px[2]));
        // The assertion: every frame captured across six switches is one preset's picture or the other's.
        // Neither is the clear colour, which is black here, so a dropped frame reads as a failure and not as
        // a colour nobody noticed.
        REQUIRE((is_green || is_blue));
    }
    flipper.join();
    CHECK_FALSE(flip_failed.load());

    char note[256];
    std::snprintf(note, sizeof note, "160x90 WARP, six switches: %d frames captured, %d green, %d blue, 0 blank",
                  frames, green, blue);
    WARN(note); // a [switch] test that does not print what it saw is not a measurement
    CHECK(frames >= 20);
    // Both were seen, or the loop was watching one preset and calling it proof.
    CHECK(green > 0);
    CHECK(blue > 0);
    CHECK(fx.stats().device_lost == 0);
}
