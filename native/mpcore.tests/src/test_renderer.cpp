// Headless renderer tests (offscreen target, WARP): creation, frames, resize storm, visibility, stub exports.
// The composition swap chain path needs a SwapChainPanel and is exercised by the app's --render-spike mode.
#include "mpcore.h"

#include <catch2/catch_amalgamated.hpp>
#include <chrono>
#include <string>
#include <thread>

namespace {

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
};

std::string last_error() {
    char buf[512];
    mp_last_error(buf, sizeof buf);
    return buf;
}

} // namespace

TEST_CASE("renderer rejects bad arguments", "[render][abi]") {
    mp_renderer* r = nullptr;
    mp_renderer_config cfg{};
    cfg.struct_size = sizeof cfg - 1;
    CHECK(mp_renderer_create(nullptr, nullptr, &cfg, &r) == MP_E_INVALID_ARG);
    cfg.struct_size = sizeof cfg;
    cfg.headless = 0;
    CHECK(mp_renderer_create(nullptr, nullptr, &cfg, &r) == MP_E_INVALID_ARG); // no panel and not headless
    CHECK(last_error().find("swap_chain_panel_native") != std::string::npos);
    CHECK(r == nullptr);
}

TEST_CASE("headless WARP renderer produces frames", "[render]") {
    renderer_fixture fx{true};
    std::this_thread::sleep_for(std::chrono::milliseconds(400));
    const mp_render_stats s = fx.stats();
    CHECK(s.frames > 5);
    CHECK(s.warp == 1);
    CHECK(s.headless == 1);
    CHECK(s.device_lost == 0);
    CHECK(s.width == 640);
    CHECK(s.height == 360);
    CHECK(std::string{s.adapter}.find("Basic Render") != std::string::npos); // "Microsoft Basic Render Driver"
    uint64_t counted = 0;
    for (uint32_t b : s.frame_ms_histogram) {
        counted += b;
    }
    CHECK(counted == s.frames - 1); // every frame-to-frame interval is bucketed
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
    std::this_thread::sleep_for(std::chrono::milliseconds(120));
    const uint64_t paused = fx.stats().frames;
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
    std::this_thread::sleep_for(std::chrono::milliseconds(300));
    const mp_render_stats s = fx.stats();
    CHECK(s.frames > 0);
    CHECK(std::string{s.adapter}.size() > 0);
}

TEST_CASE("preset, theme and quality exports name their story", "[render][abi]") {
    renderer_fixture fx{true};
    uint32_t count = 0;
    CHECK(mp_renderer_enum_presets(fx.renderer, nullptr, &count) == MP_E_STATE);
    CHECK(last_error().find("E4-S3") != std::string::npos);
    CHECK(mp_renderer_set_preset(fx.renderer, "spectrum-bars") == MP_E_STATE);
    CHECK(mp_renderer_set_param(fx.renderer, "gain", 1.0f) == MP_E_STATE);
    mp_theme_colors colors{};
    colors.struct_size = sizeof colors;
    CHECK(mp_renderer_set_theme(fx.renderer, &colors) == MP_E_STATE);
    CHECK(last_error().find("E4-S6") != std::string::npos);
    CHECK(mp_renderer_set_quality(fx.renderer, MP_QUALITY_AUTO) == MP_E_STATE);
}
