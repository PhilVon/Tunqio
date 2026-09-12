// Writes small PCM WAVs for engine tests and spikes: a continuous sine, and a "position" file whose sample
// values encode the frame index so a test can see exactly where the engine is.
//
// T-164: every fixture lives under a PER-PROCESS directory, %TEMP%\tunqio-tests-<pid>\, and not directly in
// the machine-wide %TEMP% under a literal stem. Two mpcore.tests.exe -- which is what parallel agents in
// separate worktrees produce, and what the local gate sweep produces against a peer's -- otherwise write, read
// and delete the same files. Measured before the change: three copies run twice, four of the six red, the
// failing assertion moving between test_analysis_frame.cpp:348 and test_crossfade.cpp:193 and reported at
// offline_engine.h:57 as REQUIRE_FALSE(path.empty()) -- an assertion that names no fixture and points a reader
// at the audio engine. The directory is removed when the process exits, so fixtures also stop accumulating.
//
// And a failure to write now THROWS, naming the fixture and the OS reason, rather than returning a bare empty
// string. The empty string is what converted a disk problem into an assertion about something else: every
// caller passed it straight into mp_track_open or into REQUIRE_FALSE(path.empty()), so the report described
// the consequence and never the cause.
#pragma once

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <functional>
#include <mutex>
#include <stdexcept>
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

namespace detail {

inline std::string narrow(const std::wstring& w) {
    if (w.empty()) {
        return {};
    }
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (n <= 0) {
        return {};
    }
    std::string out(static_cast<size_t>(n) - 1, '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.c_str(), -1, out.data(), n, nullptr, nullptr);
    return out;
}

// "error 5 (Access is denied)" -- the OS's own words, so a fixture failure reads as the disk problem it is.
inline std::string win32_reason(DWORD err) {
    char* text = nullptr;
    const DWORD n =
        FormatMessageA(FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM | FORMAT_MESSAGE_IGNORE_INSERTS,
                       nullptr, err, 0, reinterpret_cast<char*>(&text), 0, nullptr);
    std::string message = n != 0 && text != nullptr ? std::string(text, n) : std::string{"no description"};
    if (text != nullptr) {
        LocalFree(text);
    }
    while (!message.empty() && (message.back() == '\n' || message.back() == '\r' || message.back() == '.')) {
        message.pop_back();
    }
    return "error " + std::to_string(err) + " (" + message + ")";
}

inline std::string crt_reason(errno_t e) {
    char buf[128]{};
    if (strerror_s(buf, sizeof buf, e) != 0) {
        buf[0] = '\0';
    }
    return "errno " + std::to_string(e) + " (" + (buf[0] != '\0' ? buf : "no description") + ")";
}

// Removes the per-process fixture directory and everything in it when the process exits. Best effort: a file
// BASS still holds open must not turn a green run red on the way out.
struct fixture_dir_cleanup {
    std::wstring dir;
    ~fixture_dir_cleanup() {
        if (dir.empty()) {
            return;
        }
        WIN32_FIND_DATAW found{};
        const HANDLE h = FindFirstFileW((dir + L"*").c_str(), &found);
        if (h != INVALID_HANDLE_VALUE) {
            do {
                if ((found.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0) {
                    DeleteFileW((dir + found.cFileName).c_str());
                }
            } while (FindNextFileW(h, &found) != 0);
            FindClose(h);
        }
        RemoveDirectoryW(dir.c_str());
    }
};

// %TEMP%\tunqio-tests-<pid>\, created once per process. Throws if it cannot be made, because every fixture in
// the run depends on it and "could not create the fixture directory" is the only useful thing to say then.
inline const std::wstring& fixture_dir() {
    static fixture_dir_cleanup cleanup = [] {
        wchar_t temp[MAX_PATH];
        const DWORD n = GetTempPathW(MAX_PATH, temp);
        if (n == 0 || n >= MAX_PATH) {
            throw std::runtime_error("test fixtures: GetTempPathW failed, " + win32_reason(GetLastError()));
        }
        std::wstring dir = std::wstring{temp} + L"tunqio-tests-" + std::to_wstring(GetCurrentProcessId()) + L"\\";
        if (CreateDirectoryW(dir.c_str(), nullptr) == 0) {
            const DWORD err = GetLastError();
            if (err != ERROR_ALREADY_EXISTS) {
                throw std::runtime_error("test fixtures: could not create the fixture directory " + narrow(dir) + ", " +
                                         win32_reason(err));
            }
        }
        return fixture_dir_cleanup{std::move(dir)};
    }();
    return cleanup.dir;
}

} // namespace detail

// The directory this process's fixtures live in, for a test or a report that wants to name it.
inline std::string fixture_root() {
    return detail::narrow(detail::fixture_dir());
}

// Writes a 16-bit PCM WAV whose samples come from `sample(frame, channel)` into this process's own fixture
// directory, and returns the UTF-8 path. Throws std::runtime_error naming the fixture and the OS reason if it
// cannot: Catch2 renders that as the failing test's message, so a disk problem reads as a disk problem.
inline std::string write_wav(uint32_t sample_rate, uint16_t channels, uint32_t frames, const char* stem,
                             const std::function<int16_t(uint32_t, uint16_t)>& sample) {
    static std::mutex write_gate; // Catch2 is single-threaded, but a fixture written from a worker must not tear.
    const std::lock_guard<std::mutex> hold{write_gate};

    std::wstring wide_stem(stem, stem + std::char_traits<char>::length(stem));
    const std::wstring path = detail::fixture_dir() + wide_stem + L".wav";

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

    const std::string shown = detail::narrow(path);
    FILE* f = nullptr;
    if (const errno_t e = _wfopen_s(&f, path.c_str(), L"wb"); e != 0 || f == nullptr) {
        throw std::runtime_error("test fixture \"" + std::string{stem} + "\": could not open " + shown +
                                 " for writing, " + detail::crt_reason(e != 0 ? e : EIO));
    }
    const size_t written = std::fwrite(file.data(), 1, file.size(), f);
    const errno_t write_errno = written != file.size() ? errno : 0;
    const bool closed = std::fclose(f) == 0;
    if (written != file.size()) {
        throw std::runtime_error("test fixture \"" + std::string{stem} + "\": wrote " + std::to_string(written) +
                                 " of " + std::to_string(file.size()) + " bytes to " + shown + ", " +
                                 detail::crt_reason(write_errno != 0 ? write_errno : EIO));
    }
    if (!closed) {
        throw std::runtime_error("test fixture \"" + std::string{stem} + "\": could not flush " + shown + ", " +
                                 detail::crt_reason(errno));
    }
    return shown;
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
