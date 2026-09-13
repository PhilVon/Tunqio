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

// The mean bound is NOT a constant, because a constant is wrong for a dark picture. ambient-glow is a full-field
// wash that flash safety caps at 0.28 of full scale, so its goldens live in the bottom third of the byte range
// and a change that would be glaring in a bright preset moves each byte by one step. Measured (T-149), one
// constant perturbed at a time in a scratch copy of the presets: PeakGlow 0.28 -> 0.277 reads a mean of 0.2282
// with a max channel delta of 1 over 146 443 pixels, and the middle lobe moved 0.02 in y reads 0.1632, or 0.1366
// on the themed golden. A mean bound of 0.5 passed every one of them - a golden test that could not fail. T-56
// fixed that instance with a per-preset 0.06, which works until the next dark preset nobody thinks to check.
//
// So the bound is derived from the golden itself, from its range: the brightest channel of each lit pixel (any
// channel above 12), taken at the 99th percentile so a lone highlight cannot loosen a dark preset's bound, as a
// fraction of 255 and SQUARED. The square is fitted to the measurements rather than derived: the linear rule gives
// the themed ambient-glow golden 0.135 against its 0.1366 perturbation, a margin of 1.01, while the square gives
// 0.037 and 0.049 on the two dark goldens (3.7x and 3.3x under their smallest perturbation) and 0.225 to 0.447
// on the bright four, whose real comparison reads 0.0000. A full-range picture keeps the 0.5 it always had.
constexpr double k_mean_delta_at_full_range = 0.5;

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
// everywhere. They are NOT the worst pair of frames (T-181): full scale carries a 12 kHz centroid, and a preset
// coloured by centroid or loudness can paint a brighter field at an input between the two - measured up to 1.9x
// this pair's change. The centroid sweep beside the flash test is what bounds it.
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

// ---- the theme, pinned ---------------------------------------------------------------------------
//
// T-162 made all four presets draw with b0's `theme`, which turns a renderer-wide palette into an input to
// every picture in this file. A golden image of a themed preset is only a golden image if the theme is part of
// the fixture, so it is pinned in both directions rather than left to whatever last ran:
//
//   - the four original goldens are pinned to NO THEME, which is what mp_renderer_set_theme has never been
//     called meaning. That is the state a fresh mp_renderer is in, so it was already true - but it was true by
//     accident of construction, and `assert_unthemed` below makes it an assertion. If a future renderer ever
//     starts life with a default theme, these four goldens must move, and this is what will say so.
//   - the themed goldens are pinned to k_test_theme, a constant in this file.
//
// The theme deliberately OUTLIVES a preset switch (mpcore.h), so it is not enough to set a preset and trust the
// defaults the way a parameter allows: every renderer here is freshly created, which is the only state in which
// "no theme" is guaranteed.

// Four colours no preset's own ramp contains, so a pixel carrying one of these carries it from the theme and
// nowhere else - the same reasoning k_theme in test_renderer.cpp uses. Orange, deep purple and lime against
// ramps that are blue-teal-rose, blue-aqua-amber, violet-magenta-gold and blue-teal-ember.
constexpr float k_test_theme[4][4] = {
    {0.95f, 0.45f, 0.10f, 1.0f}, // primary: orange
    {0.45f, 0.15f, 0.85f, 1.0f}, // secondary: deep purple
    {0.55f, 0.90f, 0.20f, 1.0f}, // accent: lime
    {0.05f, 0.04f, 0.08f, 1.0f}, // background: the shell's, which no preset draws with - it has its own clear
};

mp_theme_colors theme_of(const float rgba[4][4]) {
    mp_theme_colors c{};
    c.struct_size = sizeof c;
    for (int i = 0; i < 4; ++i) {
        c.primary[i] = rgba[0][i];
        c.secondary[i] = rgba[1][i];
        c.accent[i] = rgba[2][i];
        c.background[i] = rgba[3][i];
    }
    return c;
}

// "Nothing has themed this renderer" as an assertion rather than an assumption. A preset reads alpha 0 as "the
// shell has not told me a theme" (mpcore.h), so this is the exact condition the unthemed goldens depend on.
void assert_unthemed(const headless_renderer& fx) {
    const auto theme = core(fx.handle)->theme_now();
    for (size_t i = 0; i < theme.size(); ++i) {
        INFO("theme float " << i << " of a freshly created renderer");
        REQUIRE(theme[i] == 0.0f);
    }
}

// Renders one preset once, with its declared defaults, against the fixed frame. `theme` is applied before the
// shot when given; when it is not, the renderer is asserted to be unthemed - see the note above.
capture render_preset(const std::string& id, uint32_t width = k_golden_width, uint32_t height = k_golden_height,
                      const mp_theme_colors* theme = nullptr) {
    const headless_renderer fx{width, height};
    if (mp_renderer_set_preset(fx.handle, id.c_str()) != MP_OK) {
        char err[512];
        mp_last_error(err, sizeof err);
        FAIL("mp_renderer_set_preset(\"" << id << "\") failed: " << err);
    }
    if (theme != nullptr) {
        REQUIRE(mp_renderer_set_theme(fx.handle, theme) == MP_OK);
    } else {
        assert_unthemed(fx);
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

// The mean of only the pixels that are lit, by the same threshold lit_pixels uses.
//
// Necessary rather than tidier, and waveform is why. That preset draws a ribbon covering about 1.6% of the
// field; the other 98.4% is Background, which is (0.02, 0.022, 0.035) - a near-black whose LARGEST channel is
// blue. So the whole-field mean of a waveform capture is a measurement of its background, and under an
// all-red theme it reads B 8.89 R 7.99: blue still leads a picture whose every lit pixel is pure red. Worse,
// the same arithmetic makes an all-BLUE theme look like a pass for a reason that has nothing to do with the
// theme. Averaging over the lit pixels is what makes the reading about what the preset drew.
mean_rgb mean_lit_channels(const capture& c) {
    mean_rgb m;
    size_t n = 0;
    for (size_t i = 0; i < c.bgra.size(); i += 4) {
        if (c.bgra[i] > 12 || c.bgra[i + 1] > 12 || c.bgra[i + 2] > 12) {
            m.b += c.bgra[i];
            m.g += c.bgra[i + 1];
            m.r += c.bgra[i + 2];
            ++n;
        }
    }
    if (n > 0) {
        m.b /= static_cast<double>(n);
        m.g /= static_cast<double>(n);
        m.r /= static_cast<double>(n);
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

struct mean_tolerance {
    int range = 0;      // the lit-pixel 99th-percentile brightest channel the bound was derived from
    double bound = 0.0; // the mean channel delta a capture may differ from the golden by
};

// The mean bound for one golden, from its own range (see k_mean_delta_at_full_range for why, and for the numbers).
mean_tolerance derived_mean_tolerance(const capture& golden) {
    std::array<size_t, 256> histogram{};
    size_t lit = 0;
    for (size_t i = 0; i < golden.bgra.size(); i += 4) {
        const uint8_t brightest = std::max({golden.bgra[i], golden.bgra[i + 1], golden.bgra[i + 2]});
        if (brightest > 12) {
            ++histogram[brightest];
            ++lit;
        }
    }
    mean_tolerance t;
    // A golden with nothing lit has no range to be sensitive to; it gets the full-range bound rather than zero,
    // which a single rounded byte anywhere would fail.
    if (lit == 0) {
        t.range = 255;
    } else {
        const size_t target = (lit * 99 + 99) / 100;
        size_t seen = 0;
        for (int v = 0; v < 256; ++v) {
            seen += histogram[static_cast<size_t>(v)];
            if (seen >= target) {
                t.range = v;
                break;
            }
        }
    }
    const double fraction = static_cast<double>(t.range) / 255.0;
    t.bound = k_mean_delta_at_full_range * fraction * fraction;
    return t;
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
    const mean_tolerance tolerance = derived_mean_tolerance(reference);
    char note[360];
    std::snprintf(note, sizeof note,
                  "%s vs golden: max channel delta %d (tolerance %d), mean %.4f (tolerance %.3f, from a lit range of "
                  "%d), %zu of %zu pixels differ at all",
                  id.c_str(), d.max_channel, k_max_channel_delta, d.mean_channel, tolerance.bound, tolerance.range,
                  d.pixels_differing, shot.bgra.size() / 4);
    WARN(note); // a comparison that does not print its numbers is not a measurement

    if (d.max_channel > k_max_channel_delta || d.mean_channel > tolerance.bound) {
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
    check_against_golden("ambient-glow", render_preset("ambient-glow"));
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

// ---- T-162: the theme reaches the screen --------------------------------------------------------
//
// E4-S6 built the theme and measured it exhaustively along a path that ended at b0: T-156's check reads the
// palette arriving at 30.3 per second on the live shell. What was never written is the last step - no shipped
// preset READ theme[], so sixteen floats arrived every frame and changed nothing. These tests are that step,
// and they are deliberately pictures: the two goldens below are the first checked-in images in this repository
// in which a theme colour is visible.
//
// The design they encode, per preset:
//   spectrum-bars, waveform, radial-spectrum  the theme replaces the ramp's three STOPS. `colour` still
//                                             chooses where on the ramp to sample, so the two are orthogonal
//                                             and every combination is meaningful.
//   ambient-glow                              the theme is the FALLBACK under the album art, replacing
//                                             default_stop(). Art outranks it; see the shader's header.
// and in all four, theme_mix (default 1) scales from the preset's own palette to the theme's.

TEST_CASE("Spectrum Bars draws with the theme", "[render][preset][golden][theme]") {
    const preset_root_override root{shipped_presets()};
    const mp_theme_colors theme = theme_of(k_test_theme);
    check_against_golden("spectrum-bars-themed",
                         render_preset("spectrum-bars", k_golden_width, k_golden_height, &theme));
}

// Ambient Glow at its declared defaults, which are art_* = -1: no art, so this is the theme arm of art_or's
// three-way branch and the picture is the theme's colours rather than the built-in blue-teal-ember.
TEST_CASE("Ambient Glow draws with the theme when there is no album art", "[render][preset][golden][theme]") {
    const preset_root_override root{shipped_presets()};
    const mp_theme_colors theme = theme_of(k_test_theme);
    check_against_golden("ambient-glow-themed", render_preset("ambient-glow", k_golden_width, k_golden_height, &theme));
}

// The goldens above are two presets. This is the claim for all four, and it is the one T-162 exists to make.
//
// It is asserted with three SINGLE-HUE themes rather than with k_test_theme, and that is worth explaining
// because the first version of this test used k_test_theme and was wrong in a way the pictures caught.
//
// The plausible-looking assertion is "an orange-purple-lime theme makes the field redder". It is false twice
// over. That theme's purple carries a 0.85 blue channel, so mean blue stays ahead of mean red even when the
// theme has completely taken over (spectrum-bars measured B 22.07 -> 16.88 while R 9.74 -> 14.42: both moved
// toward the theme, and blue still led). And radial-spectrum's own ramp is violet-magenta-gold, which is
// ALREADY warmer than the theme, so for that preset a correct wiring makes the field LESS red (R 13.26 ->
// 10.48). An assertion about absolute channel order is therefore an assertion about which palette happens to
// be redder, not about whether the theme arrived.
//
// A theme whose three stops are all the same saturated primary has no such ambiguity: whatever the preset's
// own ramp was, the field must end up dominated by that channel. That is a claim about the theme arriving and
// nothing else, and it holds for all four presets and all three channels.
TEST_CASE("every shipped preset draws with the theme, and theme_mix takes it back", "[render][preset][theme]") {
    const preset_root_override root{shipped_presets()};

    struct probe {
        const char* name;
        float rgb[3];
    };
    constexpr std::array<probe, 3> k_probes{{
        {"red", {1.0f, 0.0f, 0.0f}},
        {"green", {0.0f, 1.0f, 0.0f}},
        {"blue", {0.0f, 0.0f, 1.0f}},
    }};

    for (const char* id : k_shipped_presets) {
        INFO("preset " << id);
        const headless_renderer fx{k_golden_width, k_golden_height};
        REQUIRE(mp_renderer_set_preset(fx.handle, id) == MP_OK);

        assert_unthemed(fx);
        const capture unthemed = fx.shoot(fixed_frame());

        for (const auto& p : k_probes) {
            INFO("an all-" << p.name << " theme");
            float rgba[4][4]{};
            for (size_t c = 0; c < 4; ++c) {
                for (size_t ch = 0; ch < 3; ++ch) {
                    rgba[c][ch] = p.rgb[ch];
                }
                rgba[c][3] = 1.0f;
            }
            const mp_theme_colors theme = theme_of(rgba);
            REQUIRE(mp_renderer_set_theme(fx.handle, &theme) == MP_OK);
            const capture themed = fx.shoot(fixed_frame());
            // Over the LIT pixels, not the whole field - see the note on mean_lit_channels. A waveform ribbon
            // is 1.6% of its picture and the background behind it is blue, so a whole-field mean would be
            // measuring the clear colour.
            const mean_rgb m = mean_lit_channels(themed);

            char note[288];
            std::snprintf(note, sizeof note,
                          "%s under an all-%s theme: mean lit B %.2f G %.2f R %.2f (%zu of %zu "
                          "pixels differ from the unthemed picture)",
                          id, p.name, m.b, m.g, m.r, compare(unthemed, themed).pixels_differing,
                          unthemed.bgra.size() / 4);
            WARN(note);

            CHECK(compare(unthemed, themed).pixels_differing > 0);
            // The field takes the theme's hue, whatever the preset's own ramp was.
            const double own = p.rgb[0] > 0.0f ? m.r : (p.rgb[1] > 0.0f ? m.g : m.b);
            const double other_a = p.rgb[0] > 0.0f ? m.g : m.r;
            const double other_b = p.rgb[2] > 0.0f ? m.g : m.b;
            CHECK(own > other_a);
            CHECK(own > other_b);
        }

        // theme_mix 0 is the preset's own palette back, byte for byte, with a theme still set. This is what
        // makes the parameter an escape hatch rather than an approximation of one.
        REQUIRE(mp_renderer_set_param(fx.handle, "theme_mix", 0.0f) == MP_OK);
        const capture opted_out = fx.shoot(fixed_frame());
        CHECK(compare(unthemed, opted_out).max_channel == 0);
    }
}

// k_test_theme is a realistic three-colour palette rather than a single-hue probe - it is what the two golden
// images above are recorded under - so what it can assert is that the picture MOVES, and by how much. The
// numbers it prints are the ones worth reading when a golden changes.
TEST_CASE("a realistic theme moves every preset's picture", "[render][preset][theme]") {
    const preset_root_override root{shipped_presets()};
    const mp_theme_colors theme = theme_of(k_test_theme);

    for (const char* id : k_shipped_presets) {
        INFO("preset " << id);
        const headless_renderer fx{k_golden_width, k_golden_height};
        REQUIRE(mp_renderer_set_preset(fx.handle, id) == MP_OK);
        const capture unthemed = fx.shoot(fixed_frame());
        const mean_rgb before = mean_channels(unthemed);

        REQUIRE(mp_renderer_set_theme(fx.handle, &theme) == MP_OK);
        const capture themed = fx.shoot(fixed_frame());
        const mean_rgb after = mean_channels(themed);
        const difference d = compare(unthemed, themed);

        char note[320];
        std::snprintf(note, sizeof note,
                      "%s unthemed -> themed: mean field B %.2f -> %.2f, G %.2f -> %.2f, R %.2f -> %.2f; %zu of "
                      "%zu pixels differ, max channel delta %d",
                      id, before.b, after.b, before.g, after.g, before.r, after.r, d.pixels_differing,
                      unthemed.bgra.size() / 4, d.max_channel);
        WARN(note);

        // A visible change, not a rounding one. The smallest of the four is waveform, which draws a ribbon a
        // couple of pixels thick over an unlit field, so the bound is expressed against its own lit area.
        CHECK(d.pixels_differing > lit_pixels(unthemed) / 4);
        CHECK(d.max_channel > 8);
    }
}

// The reason the theme replaces the ramp's stops rather than becoming a fourth value of `colour`: the two
// answer different questions, so all six combinations have to mean something. Radial Spectrum is the preset
// whose colour mode is most visibly user-facing, so it is the one asserted on.
TEST_CASE("the theme survives every colour mode", "[render][preset][theme]") {
    const preset_root_override root{shipped_presets()};
    // A single-hue probe rather than k_test_theme, for the reason given on the test above: only an all-one-
    // channel theme lets "the field took the theme's hue" be asserted without knowing which of the two
    // palettes happened to be redder. Green, because radial-spectrum's own ramp has the least of it.
    constexpr float k_all_green[4][4] = {
        {0.0f, 1.0f, 0.0f, 1.0f},
        {0.0f, 1.0f, 0.0f, 1.0f},
        {0.0f, 1.0f, 0.0f, 1.0f},
        {0.0f, 1.0f, 0.0f, 1.0f},
    };
    const mp_theme_colors theme = theme_of(k_all_green);
    const headless_renderer fx{k_golden_width, k_golden_height};
    REQUIRE(mp_renderer_set_preset(fx.handle, "radial-spectrum") == MP_OK);

    std::array<capture, 3> unthemed;
    for (int mode = 0; mode < 3; ++mode) {
        REQUIRE(mp_renderer_set_param(fx.handle, "colour", static_cast<float>(mode)) == MP_OK);
        unthemed[static_cast<size_t>(mode)] = fx.shoot(fixed_frame());
    }

    REQUIRE(mp_renderer_set_theme(fx.handle, &theme) == MP_OK);
    std::array<capture, 3> themed;
    for (int mode = 0; mode < 3; ++mode) {
        REQUIRE(mp_renderer_set_param(fx.handle, "colour", static_cast<float>(mode)) == MP_OK);
        themed[static_cast<size_t>(mode)] = fx.shoot(fixed_frame());
    }

    for (size_t mode = 0; mode < 3; ++mode) {
        INFO("colour mode " << mode);
        // The theme reaches this mode...
        CHECK(compare(unthemed[mode], themed[mode]).pixels_differing > 0);
        const mean_rgb m = mean_channels(themed[mode]);
        CHECK(m.g > m.r);
        CHECK(m.g > m.b);
    }
    // ...and the mode still does its own job under the theme. If the theme had been folded into `colour` as a
    // fourth value, these three would be one picture instead of three.
    CHECK(compare(themed[0], themed[1]).pixels_differing > 0);
    CHECK(compare(themed[1], themed[2]).pixels_differing > 0);
    CHECK(compare(themed[0], themed[2]).pixels_differing > 0);
}

// Ambient Glow's ordering decision, as three assertions. AC-124 is a shipped promise and T-147 made it true;
// the theme must not quietly un-ship it.
TEST_CASE("in Ambient Glow the album art outranks the theme", "[render][preset][theme][art]") {
    const preset_root_override root{shipped_presets()};
    const mp_theme_colors theme = theme_of(k_test_theme);
    const headless_renderer fx{k_golden_width, k_golden_height};
    REQUIRE(mp_renderer_set_preset(fx.handle, "ambient-glow") == MP_OK);

    const capture built_in = fx.shoot(fixed_frame());
    REQUIRE(mp_renderer_set_theme(fx.handle, &theme) == MP_OK);
    const capture themed = fx.shoot(fixed_frame());

    // The theme beats the built-in ramp, which is the whole point of wiring it in here.
    CHECK(compare(built_in, themed).pixels_differing > 0);

    SECTION("art beats the theme") {
        // A green sleeve against an orange-purple-lime theme: if the theme won, the field would not be green.
        for (const char* name : {"art_primary", "art_secondary", "art_accent"}) {
            REQUIRE(mp_renderer_set_param(fx.handle, name, pack_srgb(30, 200, 40)) == MP_OK);
        }
        const capture with_art = fx.shoot(fixed_frame());
        const mean_rgb m = mean_channels(with_art);
        char note[224];
        std::snprintf(note, sizeof note,
                      "ambient-glow, themed orange but with a (30,200,40) sleeve: mean field B %.2f G %.2f R %.2f", m.b,
                      m.g, m.r);
        WARN(note);
        CHECK(m.g > m.r); // the sleeve's hue, not the theme's
        CHECK(compare(themed, with_art).pixels_differing > 0);

        // And taking the art away hands the field back to the theme rather than to the built-in ramp.
        for (const char* name : {"art_primary", "art_secondary", "art_accent"}) {
            REQUIRE(mp_renderer_set_param(fx.handle, name, -1.0f) == MP_OK);
        }
        CHECK(compare(themed, fx.shoot(fixed_frame())).max_channel == 0);
    }

    SECTION("a sleeve too dark to glow falls through to the theme, not to the built-in ramp") {
        // The ArtFloor branch. Before T-162 this drew the built-in blue-teal-ember; now it draws the theme,
        // which is the case the theme was wired in for.
        for (const char* name : {"art_primary", "art_secondary", "art_accent"}) {
            REQUIRE(mp_renderer_set_param(fx.handle, name, pack_srgb(6, 4, 9)) == MP_OK);
        }
        const capture black_art = fx.shoot(fixed_frame());
        CHECK(compare(themed, black_art).max_channel == 0);
        CHECK(compare(built_in, black_art).pixels_differing > 0);
    }
}

// A near-black theme is not a colour a preset can draw with, so it falls back rather than drawing black - the
// same argument ambient-glow's ArtFloor makes about a black-and-white sleeve. Without this a shell that
// published a very dark palette would put the visualizer out rather than tint it.
TEST_CASE("a theme too dark to draw with leaves the preset its own palette", "[render][preset][theme]") {
    const preset_root_override root{shipped_presets()};
    constexpr float k_near_black[4][4] = {
        {0.02f, 0.02f, 0.03f, 1.0f},
        {0.03f, 0.02f, 0.02f, 1.0f},
        {0.01f, 0.03f, 0.02f, 1.0f},
        {0.00f, 0.00f, 0.00f, 1.0f},
    };
    const mp_theme_colors dark = theme_of(k_near_black);

    for (const char* id : k_shipped_presets) {
        INFO("preset " << id);
        const headless_renderer fx{k_golden_width, k_golden_height};
        REQUIRE(mp_renderer_set_preset(fx.handle, id) == MP_OK);
        const capture unthemed = fx.shoot(fixed_frame());
        REQUIRE(mp_renderer_set_theme(fx.handle, &dark) == MP_OK);
        CHECK(compare(unthemed, fx.shoot(fixed_frame())).max_channel == 0);
    }
}

// ---- the accessibility contract -----------------------------------------------------------------
//
// docs/ui-screens-and-flows.md: "never flashes above 3 Hz full-field luminance change". The analysis stream runs
// at ~93 Hz, so a preset absolutely can change faster than 3 Hz - what it must not do is change the *field*
// that much. Measured between digital silence and full scale in every bin, against the two thresholds WCAG 2.3.1
// states: 10% of maximum relative luminance, over more than 25% of the field. That pair is the extremes of the
// INPUT, not of the picture - T-181 measured inputs between them that change the field up to 1.9x as much - so the
// centroid sweep below, not this test, is the one that bounds every colour mode.

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

// T-181. The test above says silence and full scale bound every real pair of frames, and it measures that pair
// at each preset's DEFAULT parameters. That holds where a preset's luminance only grows with its input, and the
// `colour` parameter is where it may not: at colour=2 the ramp coordinate is the spectral centroid, log-mapped
// over 300 Hz - 8.5 kHz, and spectrum-bars' and waveform's brightest stop is the MIDDLE one (aqua). full_scale_frame
// carries 12 kHz, which samples the end of the ramp, so a full-loudness frame at a centroid near 1.6 kHz - the
// middle of that log range - may paint a brighter field than the pair above ever measures.
//
// So this sweeps instead of trusting the pair: every shipped preset, every colour mode, and a centroid sweep that
// includes both ends of the map, its middle and the 12 kHz the pair uses, each measured from silence on a fresh
// renderer. It reports the worst it found and where, beside the default pair, so the two can be compared.
TEST_CASE("no shipped preset can flash the field at any centroid in any colour mode", "[render][preset][a11y]") {
    const preset_root_override root{shipped_presets()};
    constexpr std::array<float, 9> k_centroids{0.0f,    300.0f,  600.0f,  1200.0f, 1600.0f,
                                               2400.0f, 4800.0f, 8500.0f, 12000.0f};

    for (const char* id : k_shipped_presets) {
        double worst_area = 0.0;
        double worst_mean = 0.0;
        int worst_mean_colour = 0;
        float worst_mean_centroid = 0.0f;
        double default_mean = 0.0;

        for (int colour = 0; colour <= 2; ++colour) {
            for (const float centroid : k_centroids) {
                const headless_renderer fx{k_golden_width, k_golden_height};
                REQUIRE(mp_renderer_set_preset(fx.handle, id) == MP_OK);
                REQUIRE(mp_renderer_set_param(fx.handle, "colour", static_cast<float>(colour)) == MP_OK);
                mp_analysis_frame loud_frame = full_scale_frame();
                loud_frame.spectral_centroid_hz = centroid;
                const capture quiet = fx.shoot(silent_frame());
                const capture loud = fx.shoot(loud_frame);

                const flash_measurement m = measure_flash(quiet, loud);
                worst_area = std::max(worst_area, m.flashing_area);
                if (m.mean_delta > worst_mean) {
                    worst_mean = m.mean_delta;
                    worst_mean_colour = colour;
                    worst_mean_centroid = centroid;
                }
                if (colour == 0 && centroid == 12000.0f) {
                    default_mean = m.mean_delta;
                }
                INFO(id << " at colour=" << colour << ", silence -> full scale at a " << centroid << " Hz centroid");
                CHECK(m.flashing_area < 0.25);
                CHECK(m.mean_delta < 0.10);
            }
        }

        char note[320];
        std::snprintf(note, sizeof note,
                      "%s over 3 colour modes x %zu centroids: worst mean full-field luminance change %.4f (colour=%d, "
                      "%.0f Hz) against %.4f for the default pair; worst flashing area %.2f%% - against 0.10 and 25%%",
                      id, k_centroids.size(), worst_mean, worst_mean_colour, worst_mean_centroid, default_mean,
                      worst_area * 100.0);
        WARN(note);
    }
}

// T-162 made every preset's colours an input the SHELL controls at 30 Hz, so the measurement above - taken
// against each preset's own hand-chosen ramp - is no longer the whole claim. The shaders answer this by
// scaling a theme colour so it is never more luminous than the stop it replaces, which is meant to make every
// figure above an upper bound over all themes. "Meant to" is the part that needs measuring: the scaling uses
// luma2, an approximation of WCAG relative luminance at gamma 2 rather than 2.4, and the residual of that
// approximation is exactly what could push a preset over.
//
// So this sweeps the corners of the sRGB cube - the most saturated themes there are, and the ones where the
// approximation is furthest out - and measures the same two numbers. It is the test that would catch a theme
// making a preset flash, and it is also the test that would catch someone replacing the luminance cap with a
// plain assignment.
TEST_CASE("no shipped preset can flash the field under a worst-case theme", "[render][preset][a11y][theme]") {
    const preset_root_override root{shipped_presets()};
    // White plus the six fully saturated hues, each used for all three of primary, secondary and accent so the
    // whole ramp is that colour and nothing dilutes it.
    constexpr float k_corners[7][3] = {
        {1.0f, 1.0f, 1.0f}, {1.0f, 0.0f, 0.0f}, {0.0f, 1.0f, 0.0f}, {0.0f, 0.0f, 1.0f},
        {0.0f, 1.0f, 1.0f}, {1.0f, 0.0f, 1.0f}, {1.0f, 1.0f, 0.0f},
    };
    static const char* const k_names[7] = {"white", "red", "green", "blue", "cyan", "magenta", "yellow"};

    for (const char* id : k_shipped_presets) {
        double worst_area = 0.0;
        double worst_mean = 0.0;
        const char* worst_name = "";

        for (size_t k = 0; k < 7; ++k) {
            float rgba[4][4]{};
            for (size_t c = 0; c < 4; ++c) {
                for (size_t ch = 0; ch < 3; ++ch) {
                    rgba[c][ch] = k_corners[k][ch];
                }
                rgba[c][3] = 1.0f;
            }
            const mp_theme_colors theme = theme_of(rgba);

            const headless_renderer fx{k_golden_width, k_golden_height};
            REQUIRE(mp_renderer_set_preset(fx.handle, id) == MP_OK);
            REQUIRE(mp_renderer_set_theme(fx.handle, &theme) == MP_OK);
            const capture quiet = fx.shoot(silent_frame());
            const capture loud = fx.shoot(full_scale_frame());

            const flash_measurement m = measure_flash(quiet, loud);
            if (m.flashing_area > worst_area) {
                worst_area = m.flashing_area;
                worst_name = k_names[k];
            }
            worst_mean = std::max(worst_mean, m.mean_delta);
            INFO(id << " under an all-" << k_names[k] << " theme");
            CHECK(m.flashing_area < 0.25);
            CHECK(m.mean_delta < 0.10);
        }

        char note[288];
        std::snprintf(note, sizeof note,
                      "%s over the seven sRGB corner themes: worst flashing area %.2f%% (all-%s), worst mean "
                      "full-field luminance change %.4f - against 25%% and 0.10",
                      id, worst_area * 100.0, worst_name, worst_mean);
        WARN(note);
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
