// The headless engine the transport and gapless tests drive: MP_DEVICE_NONE output, mp_engine_render as the
// output thread, and the buffer arithmetic (RMS, largest step, silence) the assertions are written in.
#pragma once

#include "mpcore.h"

#include "audio/bass_engine.h"

#include <algorithm>
#include <catch2/catch_amalgamated.hpp>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace mp::tests {

inline constexpr uint32_t k_rate = 48000;
inline constexpr uint32_t k_channels = 2;
inline constexpr uint32_t k_fade_frames = k_rate * mp::audio::engine::k_guard_fade_ms / 1000; // 2400
inline constexpr uint32_t k_buffer_frames = 480; // 10 ms, a shared-mode period

inline std::string last_error() {
    char buf[512];
    mp_last_error(buf, sizeof buf);
    return buf;
}

struct offline_engine {
    mp_engine* engine = nullptr;

    offline_engine() {
        mp_engine_config cfg{};
        cfg.struct_size = sizeof cfg;
        cfg.sample_rate = k_rate;
        cfg.channels = k_channels;
        if (mp_engine_create(&cfg, &engine) != MP_OK) {
            FAIL("mp_engine_create failed: " << last_error());
        }
        mp_output_config out{};
        out.struct_size = sizeof out;
        out.device_index = MP_DEVICE_NONE;
        if (mp_engine_set_output(engine, &out) != MP_OK) {
            FAIL("mp_engine_set_output(MP_DEVICE_NONE) failed: " << last_error());
        }
    }
    ~offline_engine() {
        if (engine != nullptr) {
            mp_engine_destroy(engine);
        }
    }
    offline_engine(const offline_engine&) = delete;
    offline_engine& operator=(const offline_engine&) = delete;

    mp_track* open(const std::string& path) {
        REQUIRE_FALSE(path.empty());
        mp_track* t = nullptr;
        if (mp_track_open(engine, path.c_str(), &t) != MP_OK) {
            FAIL("mp_track_open(" << path << ") failed: " << last_error());
        }
        return t;
    }

    // Pulls `frames` frames in k_buffer_frames pieces, as a device would, and returns them concatenated.
    std::vector<float> render(uint32_t frames) {
        std::vector<float> out(static_cast<size_t>(frames) * k_channels);
        uint32_t done = 0;
        while (done < frames) {
            const uint32_t n = std::min(k_buffer_frames, frames - done);
            REQUIRE(mp_engine_render(engine, out.data() + static_cast<size_t>(done) * k_channels, n) == MP_OK);
            done += n;
        }
        return out;
    }

    int64_t position_ms() {
        mp_clock clock{};
        clock.struct_size = sizeof clock;
        REQUIRE(mp_engine_get_clock(engine, &clock) == MP_OK);
        return clock.position_ms;
    }
};

inline double rms(const std::vector<float>& samples, size_t from_frame, size_t to_frame) {
    double sum = 0;
    size_t n = 0;
    for (size_t i = from_frame * k_channels; i < to_frame * k_channels && i < samples.size(); ++i) {
        sum += static_cast<double>(samples[i]) * samples[i];
        ++n;
    }
    return n == 0 ? 0.0 : std::sqrt(sum / static_cast<double>(n));
}

// Largest sample-to-sample step per channel: what a click is.
inline double max_step(const std::vector<float>& samples) {
    double worst = 0;
    for (size_t i = k_channels; i < samples.size(); ++i) {
        worst = std::max(worst, static_cast<double>(std::fabs(samples[i] - samples[i - k_channels])));
    }
    return worst;
}

inline bool all_zero(const std::vector<float>& samples, size_t from_frame) {
    return std::all_of(samples.begin() + static_cast<std::ptrdiff_t>(from_frame * k_channels), samples.end(),
                       [](float s) { return s == 0.0f; });
}

inline void append(std::vector<float>& to, const std::vector<float>& more) {
    to.insert(to.end(), more.begin(), more.end());
}

} // namespace mp::tests
