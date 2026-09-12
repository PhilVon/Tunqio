// Golden-image, parameter and luminance-flash tests for the presets the repository ships (E4-S4, AC-121).
//
// What makes these tests rather than screenshots is that the input is fixed. renderer::set_analysis_override
// hands the render thread one mp_analysis_frame this file builds out of nothing but +, *, / and floor, so the
// same bytes reach the shader on every machine and every run, and neither shipped preset reads the clock while
// something is playing. renderer::capture_frame reads the whole target back. The picture is therefore a pure
// function of (preset, parameters, analysis frame, size), and a checked-in PNG is a fair thing to compare it to.
//
// Everything here runs on WARP - the software rasteriser - for the same reason the rest of the render suite
// does: it is the one rasteriser that is the same on a developer's machine, on a CI runner and on a laptop with
// no GPU at all. A golden image of a hardware rendering would be a golden image of one driver.
//
// To re-record the goldens after an intended change to a preset, set MPCORE_GOLDEN_UPDATE=1 and run
// `mpcore.tests [golden]`. Read the diff before committing it: that is the whole point of the file being in git.
#include "mpcore.h"

#include "png_io.h"
#include "render/renderer.h"
#include "source_root.h"

#include <algorithm>
#include <array>
#include <catch2/catch_amalgamated.hpp>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <string>
#include <thread>
#include <vector>

#include <windows.h>

namespace {

namespace fs = std::filesystem;

// The size every golden is recorded at: the 1080p framing at a third of the linear scale, so a preset's aspect
// and its pixel-space line weights are near the ones it will be seen at.
//
// What this comparison does and does not catch, measured rather than assumed. Perturbing one constant in
// spectrum-bars.hlsl and rerunning: a 1% change to the bar height (TopMargin 0.94 -> 0.93) is a max channel
// delta of 246 over 5040 pixels, and a 2.4% change to one ramp stop's green - colour only, no geometry at all -
// is a max channel delta of 9, which the per-channel bound catches and the mean bound alone would not. In the
// waveform, a 2.4% change to the excursion height is 228. What it does *not* catch is a change small enough to
// move no pixel centre: the bar duty cycle from 0.58 to 0.57 is sub-pixel at 640x360 - and was sub-pixel at
// 320x180 too, where the two goldens were first recorded - and renders byte for byte the same picture. That is
// a real floor under any image comparison and worth knowing the size of; it is the reason for 640 rather than
// 320, which halves it for a few kilobytes of PNG.
constexpr uint32_t k_golden_width = 640;
constexpr uint32_t k_golden_height = 360;

// Tolerance, and why. On this machine WARP is bit-exact: a capture compared against its own golden differs in
// zero channels, and the tests print that. The tolerance is therefore not slack for noise that exists, it is
// headroom for a future WARP or D3DCompile revision rounding a gradient differently in its last bit or two.
// Six of 255 is below a visible step on any of these gradients and far below what a changed constant, a changed
// parameter meaning or a changed shape would move; the mean bound is what stops many tiny differences adding up
// to a different picture that is everywhere-just-inside the per-channel bound.
constexpr int k_max_channel_delta = 6;
constexpr double k_max_mean_delta = 0.5;

struct preset_root_override {
    explicit preset_root_override(const fs::path& root) {
        REQUIRE(SetEnvironmentVariableW(L"MPCORE_PRESET_ROOT", root.wstring().c_str()) != 0);
    }
    ~preset_root_override() { SetEnvironmentVariableW(L"MPCORE_PRESET_ROOT", nullptr); }
    preset_root_override(const preset_root_override&) = delete;
    preset_root_override& operator=(const preset_root_override&) = delete;
};

mp::render::renderer* core(mp_renderer* r) {
    return reinterpret_cast<mp::render::renderer*>(r);
}

fs::path shipped_presets() {
    const auto root = mp::tests::find_shipped_presets();
    if (!root) {
        SKIP("the repository's presets/ was not found (set MPCORE_SOURCE_ROOT)");
    }
    return *root;
}

fs::path golden_dir() {
    const auto root = mp::tests::find_golden_images();
    if (!root) {
        SKIP("native/mpcore.tests/fixtures/golden was not found (set MPCORE_SOURCE_ROOT)");
    }
    return *root;
}

// ---- the fixed analysis frame -------------------------------------------------------------------
//
// Built from arithmetic and floor() only - no sin, no exp, no pow - because a golden image is a claim about
// bytes and a libm that rounds differently in its last place would turn a CRT update into a red test.

// A triangle wave on [-1, 1] with period 1. Exact in binary floating point.
float triangle(float x) {
    const float phase = x - std::floor(x);
    return 4.0f * std::fabs(phase - 0.5f) - 1.0f;
}

// A narrow, smooth bump centred on `centre` bins wide `width`, falling off as 1/(1 + d^4).
float partial(float bin, float centre, float width, float amplitude) {
    const float d = (bin - centre) / width;
    const float d2 = d * d;
    return amplitude / (1.0f + d2 * d2);
}

const mp_analysis_frame& fixed_frame() {
    static const mp_analysis_frame frame = [] {
        mp_analysis_frame f{};
        f.struct_size = sizeof(mp_analysis_frame);
        f.sequence = 4242; // non-zero: "something is playing", which is the branch the goldens are of
        f.mixer_byte_pos = 176400;
        f.qpc_ticks = 1234567;

        // A tilted noise floor with six partials on it - a plausible mix rather than a test card, so a change
        // to the logarithmic bar slicing or the smoothing window shows up as a different picture.
        for (uint32_t i = 0; i < MP_ANALYSIS_SPECTRUM_BINS; ++i) {
            const float bin = static_cast<float>(i);
            float m = 0.018f / (1.0f + bin * 0.012f);
            m += partial(bin, 7.0f, 2.5f, 0.62f);
            m += partial(bin, 14.0f, 2.5f, 0.31f);
            m += partial(bin, 28.0f, 3.0f, 0.17f);
            m += partial(bin, 57.0f, 4.0f, 0.09f);
            m += partial(bin, 131.0f, 6.0f, 0.05f);
            m += partial(bin, 305.0f, 10.0f, 0.022f);
            f.spectrum[i] = std::min(m, 1.0f);
        }

        // Three triangle components at unrelated rates, so the ribbon has both a slow swing and visible detail.
        for (uint32_t j = 0; j < MP_ANALYSIS_WAVEFORM_SAMPLES; ++j) {
            const float t = static_cast<float>(j) / static_cast<float>(MP_ANALYSIS_WAVEFORM_SAMPLES - 1);
            f.waveform[j] =
                0.56f * triangle(2.0f * t) + 0.24f * triangle(7.0f * t + 0.2f) + 0.11f * triangle(23.0f * t + 0.6f);
        }

        for (uint32_t b = 0; b < MP_ANALYSIS_OCTAVE_BANDS; ++b) {
            f.bands[b] = 0.55f / (1.0f + static_cast<float>(b) * 0.45f);
        }
        f.rms = 0.21f;
        f.peak = 0.78f;
        f.spectral_centroid_hz = 1850.0f;
        f.harmonic_ratio = 0.62f;
        f.onset = 0;
        return f;
    }();
    return frame;
}

// The two extremes of what the analysis stream can carry, for the flash test: digital silence, and full scale
// everywhere. Any real pair of consecutive frames is inside the change between these two.
mp_analysis_frame silent_frame() {
    mp_analysis_frame f{};
    f.struct_size = sizeof(mp_analysis_frame);
    f.sequence = 1;
    return f;
}

mp_analysis_frame full_scale_frame() {
    mp_analysis_frame f{};
    f.struct_size = sizeof(mp_analysis_frame);
    f.sequence = 2;
    for (float& bin : f.spectrum) {
        bin = 1.0f;
    }
    for (float& sample : f.waveform) {
        sample = 1.0f;
    }
    for (float& band : f.bands) {
        band = 1.0f;
    }
    f.rms = 1.0f;
    f.peak = 1.0f;
    f.spectral_centroid_hz = 12000.0f;
    f.harmonic_ratio = 1.0f;
    f.onset = 1;
    return f;
}

// ---- rendering one deterministic frame ----------------------------------------------------------

struct capture {
    std::vector<uint8_t> bgra;
    uint32_t width = 0;
    uint32_t height = 0;
};

struct headless_renderer {
    mp_renderer* handle = nullptr;

    headless_renderer(uint32_t width, uint32_t height) {
        mp_renderer_config cfg{};
        cfg.struct_size = sizeof cfg;
        cfg.width = width;
        cfg.height = height;
        cfg.scale_x = 1.0f;
        cfg.scale_y = 1.0f;
        cfg.force_warp = 1; // a golden image of a hardware rendering would be a golden image of one driver
        cfg.vsync = 0;
        cfg.headless = 1;
        const mp_result r = mp_renderer_create(nullptr, nullptr, &cfg, &handle);
        if (r != MP_OK) {
            char err[512];
            mp_last_error(err, sizeof err);
            FAIL("mp_renderer_create failed: " << err);
        }
    }
    ~headless_renderer() {
        if (handle != nullptr) {
            mp_renderer_destroy(handle);
        }
    }
    headless_renderer(const headless_renderer&) = delete;
    headless_renderer& operator=(const headless_renderer&) = delete;

    capture shoot(const mp_analysis_frame& frame) const {
        core(handle)->set_analysis_override(&frame);
        capture c;
        REQUIRE(core(handle)->capture_frame(c.bgra, c.width, c.height));
        return c;
    }
};

// Renders one preset once, with its declared defaults, against the fixed frame.
capture render_preset(const std::string& id, uint32_t width = k_golden_width, uint32_t height = k_golden_height) {
    const headless_renderer fx{width, height};
    if (mp_renderer_set_preset(fx.handle, id.c_str()) != MP_OK) {
        char err[512];
        mp_last_error(err, sizeof err);
        FAIL("mp_renderer_set_preset(\"" << id << "\") failed: " << err);
    }
    return fx.shoot(fixed_frame());
}

// ---- comparison ---------------------------------------------------------------------------------

struct difference {
    int max_channel = 0;
    double mean_channel = 0.0;
    size_t pixels_differing = 0;
};

difference compare(const capture& a, const capture& b) {
    REQUIRE(a.width == b.width);
    REQUIRE(a.height == b.height);
    REQUIRE(a.bgra.size() == b.bgra.size());
    difference d;
    double total = 0.0;
    for (size_t i = 0; i < a.bgra.size(); i += 4) {
        bool differs = false;
        for (size_t c = 0; c < 4; ++c) {
            const int delta = std::abs(static_cast<int>(a.bgra[i + c]) - static_cast<int>(b.bgra[i + c]));
            d.max_channel = std::max(d.max_channel, delta);
            total += delta;
            differs = differs || delta != 0;
        }
        d.pixels_differing += differs ? 1 : 0;
    }
    d.mean_channel = a.bgra.empty() ? 0.0 : total / static_cast<double>(a.bgra.size());
    return d;
}

// WCAG relative luminance of one sRGB-encoded pixel. The render target is B8G8R8A8_UNORM, not _SRGB, so the
// bytes in it are exactly what a display is handed; linearising them is what turns "how bright" into a number
// the accessibility threshold is expressed in.
double relative_luminance(uint8_t b, uint8_t g, uint8_t r) {
    const auto linear = [](uint8_t v) {
        const double c = v / 255.0;
        return c <= 0.03928 ? c / 12.92 : std::pow((c + 0.055) / 1.055, 2.4);
    };
    return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
}

struct flash_measurement {
    double mean_delta = 0.0;    // the full field's mean relative-luminance change between the two frames
    double flashing_area = 0.0; // the fraction of the field whose change reaches the 0.10 flash threshold
};

flash_measurement measure_flash(const capture& a, const capture& b) {
    REQUIRE(a.bgra.size() == b.bgra.size());
    flash_measurement m;
    double total = 0.0;
    size_t flashing = 0;
    const size_t pixels = a.bgra.size() / 4;
    for (size_t i = 0; i < a.bgra.size(); i += 4) {
        const double la = relative_luminance(a.bgra[i], a.bgra[i + 1], a.bgra[i + 2]);
        const double lb = relative_luminance(b.bgra[i], b.bgra[i + 1], b.bgra[i + 2]);
        const double delta = std::abs(la - lb);
        total += delta;
        // WCAG 2.3.1: a flash is an opposing change of at least 10% of maximum relative luminance where the
        // darker of the two is below 0.80. Both clauses, so a change between two already-bright images is not
        // counted - it cannot be, these presets never get there.
        if (delta >= 0.10 && std::min(la, lb) < 0.80) {
            ++flashing;
        }
    }
    m.mean_delta = pixels == 0 ? 0.0 : total / static_cast<double>(pixels);
    m.flashing_area = pixels == 0 ? 0.0 : static_cast<double>(flashing) / static_cast<double>(pixels);
    return m;
}

bool golden_update_requested() {
    char value[8]{};
    return GetEnvironmentVariableA("MPCORE_GOLDEN_UPDATE", value, sizeof value) > 0 && value[0] != '0';
}

// Compares one capture against its checked-in PNG, and says exactly what to look at when it does not match.
void check_against_golden(const std::string& id, const capture& shot) {
    const fs::path golden = golden_dir() / (id + ".png");

    if (golden_update_requested()) {
        std::string error;
        REQUIRE(mp::tests::write_png(golden, shot.bgra, shot.width, shot.height, error));
        WARN("MPCORE_GOLDEN_UPDATE: rewrote " << golden.string() << " - read the diff before committing it");
        return;
    }
    if (!fs::exists(golden)) {
        std::string error;
        (void)mp::tests::write_png(golden, shot.bgra, shot.width, shot.height, error);
        FAIL("no golden image at " << golden.string()
                                   << "; one has just been written from this run - inspect it, then commit it");
    }

    capture reference;
    std::string error;
    INFO("golden: " << golden.string());
    if (!mp::tests::read_png(golden, reference.bgra, reference.width, reference.height, error)) {
        FAIL(error);
    }
    REQUIRE(reference.width == shot.width);
    REQUIRE(reference.height == shot.height);

    const difference d = compare(shot, reference);
    char note[320];
    std::snprintf(note, sizeof note,
                  "%s vs golden: max channel delta %d (tolerance %d), mean %.4f (tolerance %.2f), %zu of %zu "
                  "pixels differ at all",
                  id.c_str(), d.max_channel, k_max_channel_delta, d.mean_channel, k_max_mean_delta, d.pixels_differing,
                  shot.bgra.size() / 4);
    WARN(note); // a comparison that does not print its numbers is not a measurement

    if (d.max_channel > k_max_channel_delta || d.mean_channel > k_max_mean_delta) {
        const fs::path actual = fs::temp_directory_path() / ("tunqio-golden-" + id + "-actual.png");
        std::string write_error;
        (void)mp::tests::write_png(actual, shot.bgra, shot.width, shot.height, write_error);
        FAIL(id << " does not match its golden image. What was rendered has been written to " << actual.string()
                << " for comparison with " << golden.string());
    }
}

} // namespace

// ---- AC-121 -------------------------------------------------------------------------------------

TEST_CASE("every preset the repository ships compiles", "[render][preset][golden]") {
    const preset_root_override root{shipped_presets()};
    const headless_renderer fx{64, 64};

    uint32_t count = 0;
    REQUIRE(mp_renderer_enum_presets(fx.handle, nullptr, &count) == MP_OK);
    std::vector<mp_preset_info> presets(count);
    for (auto& p : presets) {
        p.struct_size = sizeof(mp_preset_info);
    }
    REQUIRE(mp_renderer_enum_presets(fx.handle, presets.data(), &count) == MP_OK);

    std::vector<std::string> ids;
    for (const auto& p : presets) {
        ids.emplace_back(p.id);
        if (ids.back() == "builtin-bars") {
            continue; // compiled into the core, and E4-S3's tests already cover it
        }
        INFO("preset " << ids.back() << " (" << p.name << ")");
        if (mp_renderer_set_preset(fx.handle, p.id) != MP_OK) {
            char err[512];
            mp_last_error(err, sizeof err);
            FAIL(err);
        }
        CHECK(core(fx.handle)->active_preset_id() == ids.back());
    }

    // The two this story owns are there by name, so a preset that stopped being found is a failure and not a
    // loop over an empty catalogue quietly passing.
    CHECK(std::find(ids.begin(), ids.end(), "spectrum-bars") != ids.end());
    CHECK(std::find(ids.begin(), ids.end(), "waveform") != ids.end());
}

TEST_CASE("Spectrum Bars renders its golden image from a fixed analysis frame", "[render][preset][golden]") {
    const preset_root_override root{shipped_presets()};
    check_against_golden("spectrum-bars", render_preset("spectrum-bars"));
}

TEST_CASE("Waveform renders its golden image from a fixed analysis frame", "[render][preset][golden]") {
    const preset_root_override root{shipped_presets()};
    check_against_golden("waveform", render_preset("waveform"));
}

// The golden images are only worth having if the same input twice is the same picture twice. If this fails, a
// golden mismatch elsewhere means nothing, so it is checked before the comparisons are believed.
TEST_CASE("a preset renders the same picture twice from the same frame", "[render][preset][golden]") {
    const preset_root_override root{shipped_presets()};
    const headless_renderer fx{k_golden_width, k_golden_height};
    REQUIRE(mp_renderer_set_preset(fx.handle, "spectrum-bars") == MP_OK);

    const capture first = fx.shoot(fixed_frame());
    std::this_thread::sleep_for(std::chrono::milliseconds(80)); // several frames of clock pass between the two
    const capture second = fx.shoot(fixed_frame());
    const difference d = compare(first, second);
    CHECK(d.max_channel == 0);
    CHECK(d.pixels_differing == 0);
}

// ---- AC-122's substance: the parameters do something --------------------------------------------
//
// "Exposed in Settings › Visualization" is T-60's screen and this story does not build it. What this story owns
// is that the parameters exist with the meanings the criterion names, that mp_renderer_set_param reaches them,
// and that each one changes the picture - which is the thing a settings page would be binding to.

TEST_CASE("bar count, smoothing and colour source each change what is drawn", "[render][preset][golden]") {
    const preset_root_override root{shipped_presets()};
    const headless_renderer fx{k_golden_width, k_golden_height};
    REQUIRE(mp_renderer_set_preset(fx.handle, "spectrum-bars") == MP_OK);
    const capture defaults = fx.shoot(fixed_frame());

    SECTION("bar count") {
        REQUIRE(mp_renderer_set_param(fx.handle, "bars", 16.0f) == MP_OK);
        const capture few = fx.shoot(fixed_frame());
        CHECK(compare(defaults, few).pixels_differing > 0);

        REQUIRE(mp_renderer_set_param(fx.handle, "bars", 128.0f) == MP_OK);
        const capture many = fx.shoot(fixed_frame());
        CHECK(compare(few, many).pixels_differing > 0);

        // Sixteen wide bars light more of the field than 128 narrow ones do not: what is asserted is that the
        // count is honoured, by counting the columns that have anything in them at all along one row.
        const auto lit_columns = [](const capture& c, uint32_t y) {
            size_t runs = 0;
            bool inside = false;
            for (uint32_t x = 0; x < c.width; ++x) {
                const size_t i = (static_cast<size_t>(y) * c.width + x) * 4;
                const bool lit = c.bgra[i] > 12 || c.bgra[i + 1] > 12 || c.bgra[i + 2] > 12;
                runs += (lit && !inside) ? 1 : 0;
                inside = lit;
            }
            return runs;
        };
        CHECK(lit_columns(few, k_golden_height - 4) < lit_columns(many, k_golden_height - 4));
    }

    SECTION("smoothing") {
        REQUIRE(mp_renderer_set_param(fx.handle, "smoothing", 0.0f) == MP_OK);
        const capture sharp = fx.shoot(fixed_frame());
        REQUIRE(mp_renderer_set_param(fx.handle, "smoothing", 1.0f) == MP_OK);
        const capture smooth = fx.shoot(fixed_frame());
        CHECK(compare(sharp, smooth).pixels_differing > 0);
    }

    SECTION("colour source") {
        REQUIRE(mp_renderer_set_param(fx.handle, "colour", 0.0f) == MP_OK);
        const capture by_position = fx.shoot(fixed_frame());
        REQUIRE(mp_renderer_set_param(fx.handle, "colour", 1.0f) == MP_OK);
        const capture by_level = fx.shoot(fixed_frame());
        REQUIRE(mp_renderer_set_param(fx.handle, "colour", 2.0f) == MP_OK);
        const capture by_centroid = fx.shoot(fixed_frame());
        CHECK(compare(by_position, by_level).pixels_differing > 0);
        CHECK(compare(by_level, by_centroid).pixels_differing > 0);

        // A colour source changes colour and not shape: the same pixels are lit either way.
        const auto lit = [](const capture& c) {
            size_t n = 0;
            for (size_t i = 0; i < c.bgra.size(); i += 4) {
                n += (c.bgra[i] > 12 || c.bgra[i + 1] > 12 || c.bgra[i + 2] > 12) ? 1 : 0;
            }
            return n;
        };
        CHECK(lit(by_position) == lit(by_level));
    }

    SECTION("a parameter neither preset declares is refused") {
        CHECK(mp_renderer_set_param(fx.handle, "bar_count", 32.0f) == MP_E_INVALID_ARG);
        char err[512];
        mp_last_error(err, sizeof err);
        const std::string message{err};
        CHECK(message.find("bar_count") != std::string::npos);
        CHECK(message.find("bars") != std::string::npos); // and says what it does declare
    }
}

TEST_CASE("the waveform's point count, smoothing and colour source each change what is drawn",
          "[render][preset][golden]") {
    const preset_root_override root{shipped_presets()};
    const headless_renderer fx{k_golden_width, k_golden_height};
    REQUIRE(mp_renderer_set_preset(fx.handle, "waveform") == MP_OK);
    const capture defaults = fx.shoot(fixed_frame());

    REQUIRE(mp_renderer_set_param(fx.handle, "points", 16.0f) == MP_OK);
    CHECK(compare(defaults, fx.shoot(fixed_frame())).pixels_differing > 0);
    REQUIRE(mp_renderer_set_param(fx.handle, "points", 192.0f) == MP_OK);

    REQUIRE(mp_renderer_set_param(fx.handle, "smoothing", 1.0f) == MP_OK);
    CHECK(compare(defaults, fx.shoot(fixed_frame())).pixels_differing > 0);
    REQUIRE(mp_renderer_set_param(fx.handle, "smoothing", 0.2f) == MP_OK);

    REQUIRE(mp_renderer_set_param(fx.handle, "colour", 2.0f) == MP_OK);
    CHECK(compare(defaults, fx.shoot(fixed_frame())).pixels_differing > 0);
    REQUIRE(mp_renderer_set_param(fx.handle, "colour", 0.0f) == MP_OK);

    REQUIRE(mp_renderer_set_param(fx.handle, "thickness", 8.0f) == MP_OK);
    CHECK(compare(defaults, fx.shoot(fixed_frame())).pixels_differing > 0);
}

// ---- the accessibility contract -----------------------------------------------------------------
//
// docs/ui-screens-and-flows.md: "never flashes above 3 Hz full-field luminance change". The analysis stream runs
// at ~93 Hz, so a preset absolutely can change faster than 3 Hz - what it must not do is change the *field*
// that much. Measured between digital silence and full scale in every bin, which bounds any pair of real
// consecutive frames, against the two thresholds WCAG 2.3.1 states: 10% of maximum relative luminance, over
// more than 25% of the field.

TEST_CASE("neither shipped preset can flash the field", "[render][preset][a11y]") {
    const preset_root_override root{shipped_presets()};
    for (const char* id : {"spectrum-bars", "waveform"}) {
        const headless_renderer fx{k_golden_width, k_golden_height};
        REQUIRE(mp_renderer_set_preset(fx.handle, id) == MP_OK);
        const capture quiet = fx.shoot(silent_frame());
        const capture loud = fx.shoot(full_scale_frame());

        const flash_measurement m = measure_flash(quiet, loud);
        char note[256];
        std::snprintf(note, sizeof note,
                      "%s silence -> full scale: mean full-field luminance change %.4f, flashing area %.2f%% "
                      "(WCAG 2.3.1 allows up to 25%%)",
                      id, m.mean_delta, m.flashing_area * 100.0);
        WARN(note);
        CHECK(m.flashing_area < 0.25);
        CHECK(m.mean_delta < 0.10);
    }
}

// ---- AC-120's WARP half ---------------------------------------------------------------------------
//
// This is not the reference iGPU and cannot be: WARP is the software rasteriser. What it is good for is a floor
// that is honest on any machine, and a number printed next to the preset it is of. The iGPU figure AC-120 asks
// for belongs to T-90, which collects the reference-machine criteria.

TEST_CASE("both shipped presets render 1080p on WARP", "[render][preset][perf]") {
    const preset_root_override root{shipped_presets()};
    for (const char* id : {"spectrum-bars", "waveform"}) {
        const headless_renderer fx{1920, 1080};
        REQUIRE(mp_renderer_set_preset(fx.handle, id) == MP_OK);
        core(fx.handle)->set_analysis_override(&fixed_frame());

        mp_render_stats s{};
        s.struct_size = sizeof s;
        std::this_thread::sleep_for(std::chrono::milliseconds(400)); // the first frames pay for lazy driver work
        REQUIRE(mp_renderer_get_stats(fx.handle, &s) == MP_OK);
        const uint64_t before = s.frames;
        const auto start = std::chrono::steady_clock::now();
        std::this_thread::sleep_for(std::chrono::milliseconds(2000));
        REQUIRE(mp_renderer_get_stats(fx.handle, &s) == MP_OK);
        const double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
        const double fps = static_cast<double>(s.frames - before) / seconds;

        char note[256];
        std::snprintf(note, sizeof note, "%s at 1920x1080 on WARP (%s): %.1f fps over %.2f s", id, s.adapter, fps,
                      seconds);
        WARN(note);
        CHECK(fps >= 30.0); // the same floor E4-S3's AC-119 test holds, now per preset
        CHECK(s.device_lost == 0);
    }
}

// A measurement and deliberately not a gate. Whatever adapter this machine has is not the reference iGPU, and a
// frame rate is a property of how busy the machine is as much as of the renderer - asserting one on hardware is
// how T-119 and T-134 went red with nothing wrong. So this prints the number, names the adapter it came from,
// and asserts only that frames were drawn and the device survived.
TEST_CASE("both shipped presets draw 1080p on whatever adapter this machine has", "[render][preset][perf]") {
    const preset_root_override root{shipped_presets()};
    for (const char* id : {"spectrum-bars", "waveform"}) {
        mp_renderer* handle = nullptr;
        mp_renderer_config cfg{};
        cfg.struct_size = sizeof cfg;
        cfg.width = 1920;
        cfg.height = 1080;
        cfg.scale_x = 1.0f;
        cfg.scale_y = 1.0f;
        cfg.force_warp = 0; // hardware where there is any, WARP on a runner without one
        cfg.vsync = 0;
        cfg.headless = 1;
        REQUIRE(mp_renderer_create(nullptr, nullptr, &cfg, &handle) == MP_OK);
        REQUIRE(mp_renderer_set_preset(handle, id) == MP_OK);
        core(handle)->set_analysis_override(&fixed_frame());

        mp_render_stats s{};
        s.struct_size = sizeof s;
        std::this_thread::sleep_for(std::chrono::milliseconds(400));
        REQUIRE(mp_renderer_get_stats(handle, &s) == MP_OK);
        const uint64_t before = s.frames;
        const auto start = std::chrono::steady_clock::now();
        std::this_thread::sleep_for(std::chrono::milliseconds(2000));
        REQUIRE(mp_renderer_get_stats(handle, &s) == MP_OK);
        const double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();

        char note[256];
        std::snprintf(note, sizeof note, "%s at 1920x1080 on \"%s\"%s: %.1f fps, worst frame %.2f ms", id, s.adapter,
                      s.warp ? " (WARP: no hardware adapter here)" : "",
                      static_cast<double>(s.frames - before) / seconds, static_cast<double>(s.frame_ms_max));
        WARN(note);
        CHECK(s.frames > before);
        CHECK(s.device_lost == 0);
        mp_renderer_destroy(handle);
    }
}
