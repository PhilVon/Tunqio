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
#include <catch2/catch_amalgamated.hpp>
#include <chrono>
#include <cstdio>
#include <filesystem>
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

TEST_CASE("theme and quality exports still name their story", "[render][abi]") {
    renderer_fixture fx{true};
    mp_theme_colors colors{};
    colors.struct_size = sizeof colors;
    CHECK(mp_renderer_set_theme(fx.renderer, &colors) == MP_E_STATE);
    CHECK(last_error().find("E4-S6") != std::string::npos);
    CHECK(mp_renderer_set_quality(fx.renderer, MP_QUALITY_AUTO) == MP_E_STATE);
    CHECK(last_error().find("E4-S7") != std::string::npos);
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
    CHECK(count == 4); // built-in + solid-blue + solid-green + broken-shader; the two malformed ones are skipped

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
