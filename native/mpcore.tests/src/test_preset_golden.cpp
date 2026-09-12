// Golden-image, parameter and luminance-flash tests for the presets the repository ships (E4-S4, AC-121).
//
// What makes these tests rather than screenshots is that the input is fixed. renderer::set_analysis_override
// hands the render thread one mp_analysis_frame this file builds out of nothing but +, *, / and floor, so the
// same bytes reach the shader on every machine and every run, and no shipped preset reads the clock while
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

// ...and why one preset needs a tighter one. ambient-glow is a dark full-field wash: flash safety caps every
// channel it can emit at 0.28 of full scale, so its whole picture lives in the bottom eighth of the byte range
// and a change that would be glaring in any of the other three moves each byte by one step. Measured, by
// perturbing one constant at a time and reading the comparison: a 1% change to its brightness ceiling (PeakGlow
// 0.28 -> 0.277) moves 146 443 of 230 400 pixels but by a max channel delta of 1, and a 2% shift of one lobe's
// centre moves 88 294 by 2. Both are inside the general bounds above and both PASSED them - which is the
// "golden test that cannot fail" this file's header warns about, found the same way E4-S4 found the 320x180 one.
// The per-channel bound cannot help here (the differences really are one byte); the mean is the bound that sees
// "many tiny differences adding up to a different picture", and 0.06 catches the smaller of those two by 2.4x
// while the real comparison reads 0.0000 in Debug, Release and ASan alike.
constexpr double k_max_mean_delta_ambient_glow = 0.06;

// The presets in presets/, in the order the four built-ins of ADR-009 are listed there. builtin-bars is not one
// of them - it is compiled into the core and E4-S3's tests own it.
constexpr std::array<const char*, 4> k_shipped_presets{"spectrum-bars", "waveform", "radial-spectrum", "ambient-glow"};

// One sRGB colour as ambient-glow's art parameters carry it: r*65536 + g*256 + b. Every value is an integer
// below 2^24 and so exactly representable in the float the ABI takes, which is the whole reason for the packing
// (that, and one atomic store per colour instead of three). See the header of ambient-glow.hlsl.
constexpr float pack_srgb(int r, int g, int b) {
    return static_cast<float>(r * 65536 + g * 256 + b);
}

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
        // Pinned rather than left at the renderer's own MP_QUALITY_AUTO (E4-S7). A golden image is a claim
        // about bytes and the adaptive controller draws into a smaller rectangle when a machine is slow, so
        // leaving it on would make every picture in this file a function of how busy the runner was.
        REQUIRE(mp_renderer_set_quality(handle, MP_QUALITY_HIGH) == MP_OK);
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

// The mean blue, green and red of a whole capture. What the art-palette tests assert on: "the picture took the
// album's colour" is a statement about the field's average hue, not about any one pixel.
struct mean_rgb {
    double b = 0.0;
    double g = 0.0;
    double r = 0.0;
};

mean_rgb mean_channels(const capture& c) {
    mean_rgb m;
    const size_t pixels = c.bgra.size() / 4;
    for (size_t i = 0; i < c.bgra.size(); i += 4) {
        m.b += c.bgra[i];
        m.g += c.bgra[i + 1];
        m.r += c.bgra[i + 2];
    }
    if (pixels > 0) {
        m.b /= static_cast<double>(pixels);
        m.g /= static_cast<double>(pixels);
        m.r /= static_cast<double>(pixels);
    }
    return m;
}

// Pixels that are anything other than the near-black clear colour. "Still renders something sensible" is this.
size_t lit_pixels(const capture& c) {
    size_t n = 0;
    for (size_t i = 0; i < c.bgra.size(); i += 4) {
        n += (c.bgra[i] > 12 || c.bgra[i + 1] > 12 || c.bgra[i + 2] > 12) ? 1 : 0;
    }
    return n;
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
    double peak_delta = 0.0;    // the largest change any single pixel makes
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
        m.peak_delta = std::max(m.peak_delta, delta);
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
void check_against_golden(const std::string& id, const capture& shot, double max_mean_delta = k_max_mean_delta) {
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
                  id.c_str(), d.max_channel, k_max_channel_delta, d.mean_channel, max_mean_delta, d.pixels_differing,
                  shot.bgra.size() / 4);
    WARN(note); // a comparison that does not print its numbers is not a measurement

    if (d.max_channel > k_max_channel_delta || d.mean_channel > max_mean_delta) {
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

    // All four are there by name, so a preset that stopped being found is a failure and not a loop over an empty
    // catalogue quietly passing. E4-S4 wrote the first two and E4-S5 the last two; that is ADR-009's four.
    for (const char* id : k_shipped_presets) {
        INFO("the repository ships " << id);
        CHECK(std::find(ids.begin(), ids.end(), id) != ids.end());
    }
}

TEST_CASE("Spectrum Bars renders its golden image from a fixed analysis frame", "[render][preset][golden]") {
    const preset_root_override root{shipped_presets()};
    check_against_golden("spectrum-bars", render_preset("spectrum-bars"));
}

TEST_CASE("Waveform renders its golden image from a fixed analysis frame", "[render][preset][golden]") {
    const preset_root_override root{shipped_presets()};
    check_against_golden("waveform", render_preset("waveform"));
}

TEST_CASE("Radial Spectrum renders its golden image from a fixed analysis frame", "[render][preset][golden]") {
    const preset_root_override root{shipped_presets()};
    check_against_golden("radial-spectrum", render_preset("radial-spectrum"));
}

// At its declared defaults, which are art_primary/secondary/accent = -1: the golden is the no-art picture, so a
// change to the built-in ramp is caught here and a change to the art path is caught by the tests below it.
TEST_CASE("Ambient Glow renders its golden image from a fixed analysis frame", "[render][preset][golden]") {
    const preset_root_override root{shipped_presets()};
    check_against_golden("ambient-glow", render_preset("ambient-glow"), k_max_mean_delta_ambient_glow);
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

// ---- E4-S5's two, the same criterion ------------------------------------------------------------

TEST_CASE("the radial spectrum's ray count, smoothing, colour source and hub each change what is drawn",
          "[render][preset][golden]") {
    const preset_root_override root{shipped_presets()};
    const headless_renderer fx{k_golden_width, k_golden_height};
    REQUIRE(mp_renderer_set_preset(fx.handle, "radial-spectrum") == MP_OK);
    const capture defaults = fx.shoot(fixed_frame());

    SECTION("ray count") {
        REQUIRE(mp_renderer_set_param(fx.handle, "rays", 12.0f) == MP_OK);
        const capture few = fx.shoot(fixed_frame());
        CHECK(compare(defaults, few).pixels_differing > 0);

        REQUIRE(mp_renderer_set_param(fx.handle, "rays", 128.0f) == MP_OK);
        const capture many = fx.shoot(fixed_frame());
        CHECK(compare(few, many).pixels_differing > 0);

        // The count is honoured and not merely "something changed": the wedges keep a fixed duty cycle, so more
        // of them at the same radius means more separate lit runs around a circle. Counted along the row through
        // the hub, on the half of it to the right of centre.
        const auto lit_runs = [](const capture& c) {
            size_t runs = 0;
            bool inside = false;
            const uint32_t y = c.height / 2;
            for (uint32_t x = c.width / 2; x < c.width; ++x) {
                const size_t i = (static_cast<size_t>(y) * c.width + x) * 4;
                const bool lit = c.bgra[i] > 12 || c.bgra[i + 1] > 12 || c.bgra[i + 2] > 12;
                runs += (lit && !inside) ? 1 : 0;
                inside = lit;
            }
            return runs;
        };
        CHECK(lit_runs(few) < lit_runs(many));
    }

    SECTION("smoothing") {
        REQUIRE(mp_renderer_set_param(fx.handle, "smoothing", 0.0f) == MP_OK);
        const capture sharp = fx.shoot(fixed_frame());
        REQUIRE(mp_renderer_set_param(fx.handle, "smoothing", 1.0f) == MP_OK);
        CHECK(compare(sharp, fx.shoot(fixed_frame())).pixels_differing > 0);
    }

    SECTION("colour source") {
        REQUIRE(mp_renderer_set_param(fx.handle, "colour", 1.0f) == MP_OK);
        const capture by_level = fx.shoot(fixed_frame());
        REQUIRE(mp_renderer_set_param(fx.handle, "colour", 2.0f) == MP_OK);
        const capture by_centroid = fx.shoot(fixed_frame());
        CHECK(compare(defaults, by_level).pixels_differing > 0);
        CHECK(compare(by_level, by_centroid).pixels_differing > 0);

        // A colour source changes colour and not shape: the same pixels are lit either way.
        CHECK(lit_pixels(defaults) == lit_pixels(by_level));
    }

    SECTION("hub radius") {
        // The hub is the empty disc the rays start from, so what is asserted is its size in pixels: the distance
        // from the centre of the field to the nearest lit pixel. Not the lit area - a bigger hub leaves less
        // room outside it, so the rays are shorter and the lit area goes DOWN, which is what this test claimed
        // first time round and what the picture disproved.
        const auto inner_radius = [](const capture& c) {
            const double cx = c.width * 0.5;
            const double cy = c.height * 0.5;
            double nearest = static_cast<double>(c.width + c.height);
            for (uint32_t y = 0; y < c.height; ++y) {
                for (uint32_t x = 0; x < c.width; ++x) {
                    const size_t i = (static_cast<size_t>(y) * c.width + x) * 4;
                    if (c.bgra[i] > 12 || c.bgra[i + 1] > 12 || c.bgra[i + 2] > 12) {
                        const double dx = x + 0.5 - cx;
                        const double dy = y + 0.5 - cy;
                        nearest = std::min(nearest, std::sqrt(dx * dx + dy * dy));
                    }
                }
            }
            return nearest;
        };

        REQUIRE(mp_renderer_set_param(fx.handle, "hub", 0.05f) == MP_OK);
        const capture tight = fx.shoot(fixed_frame());
        REQUIRE(mp_renderer_set_param(fx.handle, "hub", 0.5f) == MP_OK);
        const capture wide = fx.shoot(fixed_frame());
        CHECK(compare(tight, wide).pixels_differing > 0);

        const double tight_px = inner_radius(tight);
        const double wide_px = inner_radius(wide);
        char note[192];
        std::snprintf(note, sizeof note,
                      "radial-spectrum hub 0.05 -> 0.5 at 640x360: empty centre %.1f px -> %.1f px (the half-height "
                      "is 180 px, so the parameter is a fraction of it)",
                      tight_px, wide_px);
        WARN(note);
        CHECK(wide_px > tight_px * 3.0);
    }

    SECTION("it stays circular when the field is not square") {
        // The wheel is fitted to the minor dimension, so on a 2:1 field its widest extent across is the same
        // number of pixels as its tallest extent down. Measured as the bounding box of everything lit.
        const headless_renderer wide{640, 320};
        REQUIRE(mp_renderer_set_preset(wide.handle, "radial-spectrum") == MP_OK);
        const capture c = wide.shoot(full_scale_frame());
        uint32_t x0 = c.width;
        uint32_t x1 = 0;
        uint32_t y0 = c.height;
        uint32_t y1 = 0;
        for (uint32_t y = 0; y < c.height; ++y) {
            for (uint32_t x = 0; x < c.width; ++x) {
                const size_t i = (static_cast<size_t>(y) * c.width + x) * 4;
                if (c.bgra[i] > 12 || c.bgra[i + 1] > 12 || c.bgra[i + 2] > 12) {
                    x0 = std::min(x0, x);
                    x1 = std::max(x1, x);
                    y0 = std::min(y0, y);
                    y1 = std::max(y1, y);
                }
            }
        }
        REQUIRE(x1 > x0);
        const double across = static_cast<double>(x1 - x0);
        const double down = static_cast<double>(y1 - y0);
        char note[192];
        std::snprintf(note, sizeof note, "radial-spectrum on a 640x320 field: %.0f px across, %.0f px down", across,
                      down);
        WARN(note);
        // Two per cent, which is a wedge's chord rather than an aspect error; an unfitted circle would be 2:1.
        CHECK(std::abs(across - down) / down < 0.02);
    }
}

TEST_CASE("the ambient glow's spread, smoothing, colour source and glow each change what is drawn",
          "[render][preset][golden]") {
    const preset_root_override root{shipped_presets()};
    const headless_renderer fx{k_golden_width, k_golden_height};
    REQUIRE(mp_renderer_set_preset(fx.handle, "ambient-glow") == MP_OK);
    const capture defaults = fx.shoot(fixed_frame());

    REQUIRE(mp_renderer_set_param(fx.handle, "spread", 1.2f) == MP_OK);
    const capture wide = fx.shoot(fixed_frame());
    CHECK(compare(defaults, wide).pixels_differing > 0);
    CHECK(lit_pixels(wide) > lit_pixels(defaults)); // wider lobes reach more of the field
    REQUIRE(mp_renderer_set_param(fx.handle, "spread", 0.6f) == MP_OK);

    REQUIRE(mp_renderer_set_param(fx.handle, "smoothing", 1.0f) == MP_OK);
    CHECK(compare(defaults, fx.shoot(fixed_frame())).pixels_differing > 0);
    REQUIRE(mp_renderer_set_param(fx.handle, "smoothing", 0.4f) == MP_OK);

    REQUIRE(mp_renderer_set_param(fx.handle, "colour", 1.0f) == MP_OK);
    CHECK(compare(defaults, fx.shoot(fixed_frame())).pixels_differing > 0);
    REQUIRE(mp_renderer_set_param(fx.handle, "colour", 2.0f) == MP_OK);
    CHECK(compare(defaults, fx.shoot(fixed_frame())).pixels_differing > 0);
    REQUIRE(mp_renderer_set_param(fx.handle, "colour", 0.0f) == MP_OK);

    // glow only attenuates: 1.0 is the top of its declared range because that is where the flash measurement is
    // taken, so what is asserted is that turning it down makes the field darker.
    REQUIRE(mp_renderer_set_param(fx.handle, "glow", 0.25f) == MP_OK);
    const capture dim = fx.shoot(fixed_frame());
    CHECK(compare(defaults, dim).pixels_differing > 0);
    const mean_rgb bright_mean = mean_channels(defaults);
    const mean_rgb dim_mean = mean_channels(dim);
    CHECK(dim_mean.b + dim_mean.g + dim_mean.r < bright_mean.b + bright_mean.g + bright_mean.r);

    CHECK(mp_renderer_set_param(fx.handle, "glow", 2.0f) == MP_OK); // clamped to the declared 1.0, not refused
}

// ---- AC-124: Ambient Glow uses palette colours from album art when available --------------------
//
// The palette is E3-S7's (IArtCache.LoadPaletteAsync -> ArtPalette, five colours by median cut). It reaches this
// preset as three parameters through the existing mp_renderer_set_param, one packed sRGB colour each, with a
// negative value meaning "no art". The managed half of that path - ArtPalette to those three floats - is
// AmbientGlowPalette in Tunqio.Core and is tested in Tunqio.Core.Tests; what is tested here is the half that
// needs a GPU: that the colours arrive, that they are what is drawn, and that every "when available" failure
// still draws a picture.

TEST_CASE("Ambient Glow takes its colours from the album art palette", "[render][preset][golden][art]") {
    const preset_root_override root{shipped_presets()};
    const headless_renderer fx{k_golden_width, k_golden_height};
    REQUIRE(mp_renderer_set_preset(fx.handle, "ambient-glow") == MP_OK);

    // No art is the default, and the default is what the golden image is of: a blue-teal-ember ramp of the
    // preset's own, so the field reads blue.
    const capture no_art = fx.shoot(fixed_frame());
    const mean_rgb none = mean_channels(no_art);
    CHECK(none.b > none.r);

    SECTION("a red sleeve makes a red field") {
        for (const char* name : {"art_primary", "art_secondary", "art_accent"}) {
            REQUIRE(mp_renderer_set_param(fx.handle, name, pack_srgb(200, 30, 40)) == MP_OK);
        }
        const capture red = fx.shoot(fixed_frame());
        const mean_rgb m = mean_channels(red);
        char note[192];
        std::snprintf(note, sizeof note, "ambient-glow with a (200,30,40) palette: mean field B %.2f G %.2f R %.2f",
                      m.b, m.g, m.r);
        WARN(note);
        CHECK(compare(no_art, red).pixels_differing > 0);
        CHECK(m.r > m.b);          // the sleeve's hue, not the preset's
        CHECK(m.r > none.r + 2.0); // and by a margin no rounding accounts for
    }

    SECTION("each of the three colours is used, not just the first") {
        REQUIRE(mp_renderer_set_param(fx.handle, "art_primary", pack_srgb(200, 30, 40)) == MP_OK);
        const capture one = fx.shoot(fixed_frame());
        REQUIRE(mp_renderer_set_param(fx.handle, "art_secondary", pack_srgb(30, 200, 40)) == MP_OK);
        const capture two = fx.shoot(fixed_frame());
        REQUIRE(mp_renderer_set_param(fx.handle, "art_accent", pack_srgb(40, 30, 200)) == MP_OK);
        const capture three = fx.shoot(fixed_frame());
        CHECK(compare(one, two).pixels_differing > 0);
        CHECK(compare(two, three).pixels_differing > 0);
    }

    SECTION("the packing survives the float the ABI takes") {
        // The one value where a rounding error would be invisible in a hue test: the largest colour there is.
        REQUIRE(mp_renderer_set_param(fx.handle, "art_primary", pack_srgb(255, 255, 255)) == MP_OK);
        const capture white = fx.shoot(fixed_frame());
        REQUIRE(mp_renderer_set_param(fx.handle, "art_primary", pack_srgb(255, 255, 254)) == MP_OK);
        const capture almost = fx.shoot(fixed_frame());
        // One step of blue out of 16 777 216 packed values: if the pack/unpack lost a bit these would be equal.
        CHECK(compare(white, almost).pixels_differing > 0);
    }
}

// "When available" is load-bearing: a track with no art, and art whose palette cannot be a glow, both have to
// draw something. Neither is an assumption here - the shader has a branch for each and this is that branch.
TEST_CASE("Ambient Glow still draws when there is no usable palette", "[render][preset][golden][art]") {
    const preset_root_override root{shipped_presets()};
    const headless_renderer fx{k_golden_width, k_golden_height};
    REQUIRE(mp_renderer_set_preset(fx.handle, "ambient-glow") == MP_OK);
    const capture no_art = fx.shoot(fixed_frame());
    const size_t lit_without_art = lit_pixels(no_art);
    CHECK(lit_without_art > no_art.bgra.size() / 4 / 10); // a tenth of the field at least: this is a wash

    SECTION("a track with no art") {
        // -1 is the declared default and what the managed side sends for a null palette; -1 is also what the
        // parameter clamps to for anything below it, so "no art" has one representation and not several.
        for (const char* name : {"art_primary", "art_secondary", "art_accent"}) {
            REQUIRE(mp_renderer_set_param(fx.handle, name, -5000.0f) == MP_OK);
        }
        const capture clamped = fx.shoot(fixed_frame());
        CHECK(compare(no_art, clamped).max_channel == 0);
    }

    SECTION("a sleeve whose palette is all but black") {
        // A near-black cover quantises to near-black entries. Scaling one of those up is not a glow, it is
        // amplified quantisation noise, so the shader falls back to its own stop for each colour that dark.
        for (const char* name : {"art_primary", "art_secondary", "art_accent"}) {
            REQUIRE(mp_renderer_set_param(fx.handle, name, pack_srgb(6, 4, 9)) == MP_OK);
        }
        const capture black_art = fx.shoot(fixed_frame());
        CHECK(lit_pixels(black_art) == lit_without_art);
        CHECK(compare(no_art, black_art).max_channel == 0); // exactly the no-art picture, by the same branch
    }

    SECTION("one dark colour in an otherwise usable palette") {
        // Per colour, not all or nothing: a palette with one black entry keeps the two that work.
        REQUIRE(mp_renderer_set_param(fx.handle, "art_primary", pack_srgb(4, 4, 4)) == MP_OK);
        REQUIRE(mp_renderer_set_param(fx.handle, "art_secondary", pack_srgb(200, 30, 40)) == MP_OK);
        const capture mixed = fx.shoot(fixed_frame());
        CHECK(compare(no_art, mixed).pixels_differing > 0);
        CHECK(lit_pixels(mixed) > lit_without_art / 2);
    }
}

// ---- the accessibility contract -----------------------------------------------------------------
//
// docs/ui-screens-and-flows.md: "never flashes above 3 Hz full-field luminance change". The analysis stream runs
// at ~93 Hz, so a preset absolutely can change faster than 3 Hz - what it must not do is change the *field*
// that much. Measured between digital silence and full scale in every bin, which bounds any pair of real
// consecutive frames, against the two thresholds WCAG 2.3.1 states: 10% of maximum relative luminance, over
// more than 25% of the field.

TEST_CASE("no shipped preset can flash the field", "[render][preset][a11y]") {
    const preset_root_override root{shipped_presets()};
    for (const char* id : k_shipped_presets) {
        const headless_renderer fx{k_golden_width, k_golden_height};
        REQUIRE(mp_renderer_set_preset(fx.handle, id) == MP_OK);
        const capture quiet = fx.shoot(silent_frame());
        const capture loud = fx.shoot(full_scale_frame());

        const flash_measurement m = measure_flash(quiet, loud);
        char note[320];
        std::snprintf(note, sizeof note,
                      "%s silence -> full scale: mean full-field luminance change %.4f, largest single-pixel "
                      "change %.4f (a flash is 0.10), flashing area %.2f%% (WCAG 2.3.1 allows up to 25%%)",
                      id, m.mean_delta, m.peak_delta, m.flashing_area * 100.0);
        WARN(note); // the peak is printed because ambient-glow's area is zero, and a zero needs a reading beside it
        CHECK(m.flashing_area < 0.25);
        CHECK(m.mean_delta < 0.10);
    }
}

// Ambient Glow's flash safety is a different claim from the other three - not "only a small part of the field
// changes" but "no pixel can get bright enough for the change to count", which is what lets a preset cover the
// whole field at all. It holds for every input and every parameter, so it is worth asserting separately from the
// silence-to-full-scale measurement: the brightest colour the shader can emit is Background + PeakGlow.
TEST_CASE("Ambient Glow cannot reach the flash threshold at any input", "[render][preset][a11y]") {
    const preset_root_override root{shipped_presets()};
    const headless_renderer fx{k_golden_width, k_golden_height};
    REQUIRE(mp_renderer_set_preset(fx.handle, "ambient-glow") == MP_OK);
    // The worst case the parameters allow: full gain, the widest lobes, the brightest glow, and three white
    // palette colours (the shader renormalises art to a fixed maximum channel, so white is the brightest art
    // there is). Against a full-scale analysis frame, which saturates every band.
    REQUIRE(mp_renderer_set_param(fx.handle, "gain", 4.0f) == MP_OK);
    REQUIRE(mp_renderer_set_param(fx.handle, "spread", 1.2f) == MP_OK);
    REQUIRE(mp_renderer_set_param(fx.handle, "glow", 1.0f) == MP_OK);
    for (const char* name : {"art_primary", "art_secondary", "art_accent"}) {
        REQUIRE(mp_renderer_set_param(fx.handle, name, pack_srgb(255, 255, 255)) == MP_OK);
    }
    const capture loud = fx.shoot(full_scale_frame());

    double brightest = 0.0;
    for (size_t i = 0; i < loud.bgra.size(); i += 4) {
        brightest = std::max(brightest, relative_luminance(loud.bgra[i], loud.bgra[i + 1], loud.bgra[i + 2]));
    }
    char note[256];
    std::snprintf(note, sizeof note,
                  "ambient-glow at its brightest possible setting: peak relative luminance %.4f, against the 0.10 "
                  "change WCAG 2.3.1 counts as a flash from a near-black background",
                  brightest);
    WARN(note);
    CHECK(brightest < 0.10);
}

// ---- AC-120's WARP half ---------------------------------------------------------------------------
//
// This is not the reference iGPU and cannot be: WARP is the software rasteriser. The iGPU figure AC-120 asks for
// belongs to T-90, which collects the reference-machine criteria.
//
// What is left here is a smoke floor, and the number below is chosen to be one. It was 30 fps, on the reasoning
// that a software rasteriser gives a figure honest on any machine. It does the opposite: WARP renders on the CPU,
// so its frame rate is the most contention-sensitive number in this suite, not the least. ambient-glow measured
// 150 fps on the dev machine and 21.4 on a shared CI runner and turned the build red with nothing wrong (T-150) -
// and it is the expensive one by construction, a screen-covering quad computing its wash per pixel because the
// pipeline sets no blend state (T-148).
//
// So: far enough below the worst legitimate observation to mean something has broken rather than something is
// busy, which is the same division 187609f drew for the upsert bounds. Against 21.4 fps on a loaded runner and
// 150 on an idle desktop, five says a preset has stopped drawing rather than that a machine is busy. The rate a
// user actually gets is AC-119's and AC-120's, and both of those belong on hardware nobody is sharing.

TEST_CASE("every shipped preset renders 1080p on WARP", "[render][preset][perf]") {
    const preset_root_override root{shipped_presets()};
    for (const char* id : k_shipped_presets) {
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
        CHECK(fps >= 5.0); // a smoke floor, not a budget - see the note above this test
        CHECK(s.device_lost == 0);
    }
}

// A measurement and deliberately not a gate. Whatever adapter this machine has is not the reference iGPU, and a
// frame rate is a property of how busy the machine is as much as of the renderer - asserting one on hardware is
// how T-119 and T-134 went red with nothing wrong. So this prints the number, names the adapter it came from,
// and asserts only that frames were drawn and the device survived.
TEST_CASE("every shipped preset draws 1080p on whatever adapter this machine has", "[render][preset][perf]") {
    const preset_root_override root{shipped_presets()};
    for (const char* id : k_shipped_presets) {
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
        REQUIRE(mp_renderer_set_quality(handle, MP_QUALITY_HIGH) == MP_OK); // the number below is a full-scale one
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
