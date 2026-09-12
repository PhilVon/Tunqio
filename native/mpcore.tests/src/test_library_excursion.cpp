// T-175: what a real library does to the three features the reactive theme reads.
//
// This is a measurement harness, not an assertion. It is hidden from the default run ("[.]") because it needs a
// directory of somebody's music that no CI machine has, and it is here rather than in tools/ for one reason: the
// numbers have to come out of the SHIPPED extraction. mpcore.tests already compiles analysis/analyzer.cpp and
// analysis/features.cpp into itself, so analyzer::analyze() here is the same code the app runs, not a
// reimplementation of it that could be wrong in exactly the quantity being reported.
//
// Why it drives analyze() by hand instead of starting the analysis thread and polling
// mp_analysis_try_get_latest: the poll only ever hands back the NEWEST frame, and a headless render makes audio
// far faster than a device plays it, so a poller would see roughly one hop in a hundred and the tap's ring would
// overrun continuously (mp_analysis_frame.discontinuities documents exactly this case). analyze() is public
// precisely "so the tests can drive it without a thread" (analyzer.h), and driven that way every hop is
// published, in order, with discontinuities pinned at zero by construction. Feeding it hop-sized pieces of
// mp_engine_render's output reproduces what the tap would hand it, minus the dropping.
//
// Environment:
//   TUNQIO_EXCURSION_MUSIC  directory to walk recursively for audio files (required; the case skips without it)
//   TUNQIO_EXCURSION_OUT    directory to write the trace into (required)
//   TUNQIO_EXCURSION_LIMIT  optional cap on how many files to process, for a smoke run
//
// Output: manifest.csv (one row per track) plus one trace-NNN.bin per track, little-endian float32 triples of
// (rms, spectral_centroid_hz, harmonic_ratio) at one triple per 512-frame hop - 93.75 Hz at 48 kHz. Binary
// because the whole library is about three million hops and a formatted CSV of that is ninety megabytes of
// nothing; the manifest carries everything needed to interpret it.
#include "mpcore.h"

#include "analysis/analyzer.h"
#include "analysis/tap.h"

#include <algorithm>
#include <catch2/catch_amalgamated.hpp>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <memory>
#include <string>
#include <vector>

namespace {

namespace fs = std::filesystem;

using mp::analysis::analyzer;
using mp::analysis::tap_block;

constexpr uint32_t k_rate = 48000;
constexpr uint32_t k_channels = 2;
constexpr uint32_t k_hop = tap_block::k_frames;

std::string env_or_empty(const char* name) {
    size_t len = 0;
    char buf[1024];
    if (getenv_s(&len, buf, sizeof buf, name) != 0 || len == 0) {
        return {};
    }
    return std::string{buf};
}

std::string last_error_text() {
    char buf[512];
    mp_last_error(buf, sizeof buf);
    return buf;
}

bool is_audio(const fs::path& p) {
    std::string ext = p.extension().string();
    std::transform(ext.begin(), ext.end(), ext.begin(), [](unsigned char c) { return static_cast<char>(tolower(c)); });
    static const char* const k_known[] = {".mp3", ".wav",  ".flac", ".ogg", ".opus", ".m4a",
                                          ".aac", ".aiff", ".aif",  ".wv",  ".ape",  ".wma"};
    return std::any_of(std::begin(k_known), std::end(k_known), [&](const char* k) { return ext == k; });
}

// One track's worth of hops, or an empty vector when the file could not be opened or played.
struct track_trace {
    std::vector<float> rms;
    std::vector<float> centroid;
    std::vector<float> harmonic;
    int64_t duration_ms = 0;
    uint32_t source_rate = 0;
    uint32_t source_channels = 0;
    std::string codec;
    std::string error;
};

track_trace trace_one(mp_engine* engine, const fs::path& path) {
    track_trace out;

    mp_track* track = nullptr;
    if (mp_track_open(engine, path.string().c_str(), &track) != MP_OK) {
        out.error = "open: " + last_error_text();
        return out;
    }

    mp_track_info info{};
    info.struct_size = sizeof info;
    if (mp_track_get_info(track, &info) != MP_OK) {
        out.error = "info: " + last_error_text();
        mp_track_close(track);
        return out;
    }
    out.duration_ms = info.duration_ms;
    out.source_rate = info.sample_rate;
    out.source_channels = info.channels;
    out.codec = info.codec;

    if (mp_engine_play(engine, track, 0) != MP_OK) {
        out.error = "play: " + last_error_text();
        mp_track_close(track);
        return out;
    }

    // The mixer runs at k_rate whatever the file's own rate is, so the hop count follows the duration and not
    // info.total_frames. A fresh analyzer per track is the reset: its sliding window and the extraction's flux
    // history must not carry across a track boundary, and there is no public reset that does not start a thread.
    const auto hops = static_cast<size_t>((static_cast<double>(out.duration_ms) * k_rate / 1000.0) / k_hop);
    out.rms.reserve(hops);
    out.centroid.reserve(hops);
    out.harmonic.reserve(hops);

    auto engine_analyzer = std::make_unique<analyzer>();
    auto block = std::make_unique<tap_block>();
    block->frames = k_hop;
    block->channels = k_channels;

    int64_t mixer_bytes = 0;
    for (size_t h = 0; h < hops; ++h) {
        if (mp_engine_render(engine, block->samples, k_hop) != MP_OK) {
            out.error = "render: " + last_error_text();
            break;
        }
        block->mixer_byte_pos = mixer_bytes;
        mixer_bytes += static_cast<int64_t>(k_hop) * k_channels * static_cast<int64_t>(sizeof(float));
        engine_analyzer->analyze(*block);

        mp_analysis_frame frame{};
        if (!engine_analyzer->try_get_latest(frame)) {
            continue; // the first three hops, while the 2048-sample window fills
        }
        out.rms.push_back(frame.rms);
        out.centroid.push_back(frame.spectral_centroid_hz);
        out.harmonic.push_back(frame.harmonic_ratio);
    }

    mp_engine_stop(engine, MP_FADE_NONE);
    mp_track_close(track);
    return out;
}

} // namespace

TEST_CASE("library excursion trace", "[.][library-excursion]") {
    const std::string music = env_or_empty("TUNQIO_EXCURSION_MUSIC");
    const std::string out_dir = env_or_empty("TUNQIO_EXCURSION_OUT");
    if (music.empty() || out_dir.empty()) {
        SKIP("TUNQIO_EXCURSION_MUSIC and TUNQIO_EXCURSION_OUT must both be set");
    }
    REQUIRE(fs::is_directory(music));
    fs::create_directories(out_dir);

    std::vector<fs::path> files;
    for (const auto& entry : fs::recursive_directory_iterator{music}) {
        if (entry.is_regular_file() && is_audio(entry.path())) {
            files.push_back(entry.path());
        }
    }
    std::sort(files.begin(), files.end());
    const std::string limit = env_or_empty("TUNQIO_EXCURSION_LIMIT");
    if (!limit.empty()) {
        files.resize(std::min<size_t>(files.size(), static_cast<size_t>(std::stoul(limit))));
    }
    REQUIRE_FALSE(files.empty());

    mp_engine_config cfg{};
    cfg.struct_size = sizeof cfg;
    cfg.sample_rate = k_rate;
    cfg.channels = k_channels;
    mp_engine* engine = nullptr;
    REQUIRE(mp_engine_create(&cfg, &engine) == MP_OK);
    mp_output_config out_cfg{};
    out_cfg.struct_size = sizeof out_cfg;
    out_cfg.device_index = MP_DEVICE_NONE;
    REQUIRE(mp_engine_set_output(engine, &out_cfg) == MP_OK);

    std::ofstream manifest{fs::path{out_dir} / "manifest.csv", std::ios::trunc};
    manifest << "index,hops,duration_ms,source_rate,source_channels,codec,path,error\n";

    size_t index = 0;
    size_t ok = 0;
    for (const auto& file : files) {
        track_trace trace = trace_one(engine, file);
        char name[64];
        std::snprintf(name, sizeof name, "trace-%03zu.bin", index);
        if (!trace.rms.empty()) {
            std::ofstream bin{fs::path{out_dir} / name, std::ios::binary | std::ios::trunc};
            for (size_t i = 0; i < trace.rms.size(); ++i) {
                bin.write(reinterpret_cast<const char*>(&trace.rms[i]), sizeof(float));
                bin.write(reinterpret_cast<const char*>(&trace.centroid[i]), sizeof(float));
                bin.write(reinterpret_cast<const char*>(&trace.harmonic[i]), sizeof(float));
            }
            ++ok;
        }
        // The path may contain a comma; it is the last field but one, so quote it and the error both.
        manifest << index << ',' << trace.rms.size() << ',' << trace.duration_ms << ',' << trace.source_rate << ','
                 << trace.source_channels << ',' << trace.codec << ",\"" << file.string() << "\",\"" << trace.error
                 << "\"\n";
        manifest.flush();
        ++index;
    }

    mp_engine_destroy(engine);
    WARN("traced " << ok << " of " << files.size() << " files into " << out_dir);
    REQUIRE(ok > 0);
}
