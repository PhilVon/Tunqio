// Writes small PCM WAVs to the temp directory for engine tests and spikes: a continuous sine, and a
// "position" file whose sample values encode the frame index so a test can see exactly where the engine is.
#pragma once

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <functional>
#include <string>
#include <vector>

#include <windows.h>

namespace mp::tests {

struct wav_spec {
    uint32_t sample_rate = 48000;
    uint16_t channels = 2;
    double seconds = 2.0;
    double frequency_hz = 440.0;
    double amplitude = 0.1; // -20 dBFS
};

// Writes a 16-bit PCM WAV whose samples come from `sample(frame, channel)`. Returns the UTF-8 path, or
// an empty string on failure.
inline std::string write_wav(uint32_t sample_rate, uint16_t channels, uint32_t frames, const char* stem,
                             const std::function<int16_t(uint32_t, uint16_t)>& sample) {
    wchar_t temp[MAX_PATH];
    const DWORD n = GetTempPathW(MAX_PATH, temp);
    if (n == 0 || n >= MAX_PATH) {
        return {};
    }
    std::wstring wide_stem(stem, stem + std::char_traits<char>::length(stem));
    const std::wstring path = std::wstring{temp} + L"tunqio-" + wide_stem + L".wav";

    const uint16_t block_align = static_cast<uint16_t>(channels * 2);
    const uint32_t data_bytes = frames * block_align;

    std::vector<uint8_t> file;
    file.reserve(44 + data_bytes);
    auto put16 = [&](uint16_t v) {
        file.push_back(static_cast<uint8_t>(v & 0xFF));
        file.push_back(static_cast<uint8_t>(v >> 8));
    };
    auto put32 = [&](uint32_t v) {
        put16(static_cast<uint16_t>(v & 0xFFFF));
        put16(static_cast<uint16_t>(v >> 16));
    };
    auto tag = [&](const char* t) { file.insert(file.end(), t, t + 4); };

    tag("RIFF");
    put32(36 + data_bytes);
    tag("WAVE");
    tag("fmt ");
    put32(16);
    put16(1); // PCM
    put16(channels);
    put32(sample_rate);
    put32(sample_rate * block_align);
    put16(block_align);
    put16(16);
    tag("data");
    put32(data_bytes);

    for (uint32_t i = 0; i < frames; ++i) {
        for (uint16_t c = 0; c < channels; ++c) {
            put16(static_cast<uint16_t>(sample(i, c)));
        }
    }

    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"wb") != 0 || f == nullptr) {
        return {};
    }
    const size_t written = std::fwrite(file.data(), 1, file.size(), f);
    std::fclose(f);
    if (written != file.size()) {
        return {};
    }

    char utf8[MAX_PATH * 3];
    WideCharToMultiByte(CP_UTF8, 0, path.c_str(), -1, utf8, sizeof utf8, nullptr, nullptr);
    return utf8;
}

// A continuous sine on every channel.
inline std::string write_sine_wav(const wav_spec& spec, const char* stem) {
    const auto frames = static_cast<uint32_t>(spec.seconds * spec.sample_rate);
    const double two_pi_f = 6.283185307179586 * spec.frequency_hz;
    return write_wav(spec.sample_rate, spec.channels, frames, stem, [&](uint32_t i, uint16_t) {
        const double s = spec.amplitude * std::sin(two_pi_f * static_cast<double>(i) / spec.sample_rate);
        return static_cast<int16_t>(std::lround(s * 32767.0));
    });
}

// Frame index in the samples: left = index % 32768, right = index / 32768 (files up to 2^30 frames).
// BASS delivers 16-bit PCM as value / 32768 in float, so decode_position_frame() inverts it exactly.
inline std::string write_position_wav(uint32_t sample_rate, double seconds, const char* stem) {
    const auto frames = static_cast<uint32_t>(seconds * sample_rate);
    return write_wav(sample_rate, 2, frames, stem,
                     [](uint32_t i, uint16_t c) { return static_cast<int16_t>(c == 0 ? i % 32768u : i / 32768u); });
}

inline int64_t decode_position_frame(float left, float right) {
    return std::llround(static_cast<double>(left) * 32768.0) +
           std::llround(static_cast<double>(right) * 32768.0) * 32768;
}

} // namespace mp::tests
