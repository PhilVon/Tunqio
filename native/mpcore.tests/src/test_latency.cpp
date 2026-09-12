// Audio-to-picture sync (E4-S8, ABI 0.17): the renderer choosing WHICH analysis frame to draw, and the probe
// that reports what it chose.
//
// Nothing in this file is a stopwatch, and that is deliberate. The repository has retired four wall-clock
// bounds in one day (T-143, T-134, T-130, T-119) for the same reason: the machine's spare capacity was never
// part of the claim, so the claim could not survive a busy machine. What IS asserted here is arithmetic on the
// mixer's own byte axis - "the picture is drawn from audio this many bytes behind the listener" - which is a
// property of the selection and not of how fast this box happens to be. The clock that converts those bytes to
// milliseconds is the sample rate, not QueryPerformanceCounter.
//
// The measurement of real latency on a real output device is not here either. It is tools/LatencyRunner, which
// is a harness that reports a distribution rather than a test that ticks; docs/spikes/e4-s8-latency-floor.md says why
// that split is the honest one.
#include "mpcore.h"

#include "offline_engine.h"
#include "wav_fixture.h"

#include <algorithm>
#include <catch2/catch_amalgamated.hpp>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string>
#include <thread>
#include <vector>

namespace {

using mp::tests::k_channels;
using mp::tests::k_rate;

// Bytes of mp_clock.mixer_byte_pos per millisecond of audio: the only conversion this file needs, and it is
// the mixer's format rather than anything to do with wall time.
constexpr double k_bytes_per_ms = static_cast<double>(k_rate) * k_channels * 4.0 / 1000.0;

std::string last_error() {
    char buf[512];
    mp_last_error(buf, sizeof buf);
    return buf;
}

// A headless renderer over a real engine, so the render thread polls mp_analysis_try_get_latest for real.
// vsync 0: no pacing, so the poll is far denser than the 93.75 Hz the analysis publishes at, which is what
// makes the history ring hold consecutive hops rather than every other one.
struct latency_fixture {
    mp::tests::offline_engine engine;
    mp_renderer* renderer = nullptr;

    latency_fixture() {
        mp_renderer_config cfg{};
        cfg.struct_size = sizeof cfg;
        cfg.width = 64;
        cfg.height = 64;
        cfg.scale_x = 1.0f;
        cfg.scale_y = 1.0f;
        cfg.force_warp = 1;
        cfg.vsync = 0;
        cfg.headless = 1;
        if (mp_renderer_create(engine.engine, nullptr, &cfg, &renderer) != MP_OK) {
            FAIL("mp_renderer_create failed: " << last_error());
        }
        REQUIRE(mp_renderer_set_quality(renderer, MP_QUALITY_HIGH) == MP_OK);
    }
    ~latency_fixture() {
        if (renderer != nullptr) {
            mp_renderer_destroy(renderer);
        }
    }
    latency_fixture(const latency_fixture&) = delete;
    latency_fixture& operator=(const latency_fixture&) = delete;

    void set_sync(mp_av_sync_mode mode, float offset_ms, uint32_t probe) const {
        mp_av_sync_config cfg{};
        cfg.struct_size = sizeof cfg;
        cfg.mode = static_cast<uint32_t>(mode);
        cfg.offset_ms = offset_ms;
        cfg.probe_capacity = probe;
        REQUIRE(mp_renderer_set_av_sync(renderer, &cfg) == MP_OK);
    }

    std::vector<mp_latency_sample> drain() const {
        uint32_t waiting = 0;
        REQUIRE(mp_renderer_drain_latency(renderer, nullptr, &waiting) == MP_OK);
        if (waiting == 0) {
            return {};
        }
        std::vector<mp_latency_sample> out(waiting);
        for (auto& s : out) {
            s.struct_size = sizeof s;
        }
        uint32_t taken = waiting;
        REQUIRE(mp_renderer_drain_latency(renderer, out.data(), &taken) == MP_OK);
        out.resize(taken);
        return out;
    }

    // Pulls `ms` milliseconds of audio through the offline engine at ROUGHLY the rate a device would,
    // draining the probe as it goes, and returns every sample of the run.
    //
    // The pacing is not the measurement - nothing here asserts on wall time - it is there because an offline
    // engine asked for a second of audio in one go would produce ninety-four hops before the render thread
    // drew a single frame, and a history of one frame is a history the selection cannot be about.
    //
    // The draining is not tidiness either. An unpaced headless renderer at 64x64 on WARP draws THOUSANDS of
    // pictures a second against the analysis's 93.75 frames, so a probe that is only read at the end holds
    // the last fraction of a second and perhaps ten distinct analysis frames. That over-sampling is a real
    // property of this path and worth knowing (it is why tools/LatencyRunner drains on a timer), but it is
    // not what these cases are about.
    std::vector<mp_latency_sample> play_and_drain(int ms) {
        std::vector<mp_latency_sample> all;
        std::vector<float> buffer(static_cast<size_t>(mp::tests::k_buffer_frames) * k_channels);
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(ms);
        while (std::chrono::steady_clock::now() < deadline) {
            REQUIRE(mp_engine_render(engine.engine, buffer.data(), mp::tests::k_buffer_frames) == MP_OK);
            std::this_thread::sleep_for(std::chrono::milliseconds(10)); // k_buffer_frames is 10 ms of audio
            const auto batch = drain();
            all.insert(all.end(), batch.begin(), batch.end());
        }
        const auto last = drain();
        all.insert(all.end(), last.begin(), last.end());
        return all;
    }

    void play_for(int ms) {
        std::vector<float> buffer(static_cast<size_t>(mp::tests::k_buffer_frames) * k_channels);
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(ms);
        while (std::chrono::steady_clock::now() < deadline) {
            REQUIRE(mp_engine_render(engine.engine, buffer.data(), mp::tests::k_buffer_frames) == MP_OK);
            std::this_thread::sleep_for(std::chrono::milliseconds(10));
        }
    }
};

// How far BEHIND the listener the picture was drawn from, in milliseconds of audio. This is av_error_ms as
// mpcore.h defines it: positive is a late picture, and the picture's own instant is the MIDDLE of the hop it
// was drawn from rather than the byte that hop begins at. Half a hop is 5.33 ms, which is a third of the
// refresh interval this story is measured against, so leaving it out would be a bias and not a rounding.
double error_ms(const mp_latency_sample& s) {
    const double half_hop = 0.5 * MP_ANALYSIS_WAVEFORM_SAMPLES * k_channels * 4.0;
    return (static_cast<double>(s.audible_mixer_byte_pos - s.drawn_mixer_byte_pos) - half_hop) / k_bytes_per_ms;
}

double median(std::vector<double> values) {
    REQUIRE_FALSE(values.empty());
    std::sort(values.begin(), values.end());
    return values[values.size() / 2];
}

// The steady part of a run: the first samples are drawn before the history has anything in it to choose from,
// which is a real behaviour - the renderer draws the newest until it has an older one - and not the one these
// cases are about. Dropping the first third of a 700 ms phase leaves the selection with a full ring behind it,
// since the ring fills in about a third of a second at the rate the analysis publishes.
std::vector<double> steady_errors(const std::vector<mp_latency_sample>& samples) {
    std::vector<double> out;
    for (size_t i = samples.size() / 3; i < samples.size(); ++i) {
        if (samples[i].byte_rate > 0.0) {
            out.push_back(error_ms(samples[i]));
        }
    }
    return out;
}

} // namespace

TEST_CASE("the probe is off until it is asked for, and says so rather than answering nothing", "[latency][av-sync]") {
    latency_fixture fx;
    uint32_t count = 0;
    REQUIRE(mp_renderer_drain_latency(fx.renderer, nullptr, &count) == MP_E_STATE);
    REQUIRE(last_error().find("probe is off") != std::string::npos);

    fx.set_sync(MP_AV_SYNC_AUDIBLE, 0.0f, 64);
    REQUIRE(mp_renderer_drain_latency(fx.renderer, nullptr, &count) == MP_OK);

    // And off again, which also empties it: a capacity of zero is the switch and not a hint.
    fx.set_sync(MP_AV_SYNC_AUDIBLE, 0.0f, 0);
    REQUIRE(mp_renderer_drain_latency(fx.renderer, nullptr, &count) == MP_E_STATE);
}

TEST_CASE("a configuration the renderer cannot honour is refused and changes nothing", "[latency][av-sync]") {
    latency_fixture fx;
    fx.set_sync(MP_AV_SYNC_AUDIBLE, 0.0f, 32);

    mp_av_sync_config bad{};
    bad.struct_size = sizeof bad;
    bad.mode = 7;
    bad.probe_capacity = 32;
    REQUIRE(mp_renderer_set_av_sync(fx.renderer, &bad) == MP_E_INVALID_ARG);
    REQUIRE(last_error().find("mode 7") != std::string::npos);

    bad.mode = MP_AV_SYNC_AUDIBLE;
    bad.offset_ms = std::numeric_limits<float>::quiet_NaN();
    REQUIRE(mp_renderer_set_av_sync(fx.renderer, &bad) == MP_E_INVALID_ARG);
    REQUIRE(last_error().find("finite") != std::string::npos);

    // The probe the first call turned on is still on: a refused call left the renderer as it was.
    uint32_t count = 0;
    REQUIRE(mp_renderer_drain_latency(fx.renderer, nullptr, &count) == MP_OK);

    REQUIRE(mp_renderer_set_av_sync(fx.renderer, nullptr) == MP_E_INVALID_ARG);
}

// The criterion this file exists for (AC-331). Not "the picture is 50 ms late" - that would be a claim about
// this machine - but "asking for fifty milliseconds further back moves the picture fifty milliseconds of AUDIO
// further back", which is arithmetic the mixer's own byte axis settles and no amount of machine load changes.
//
// Both offsets are deliberately well inside the compensating regime. An offline engine has no output buffer,
// so at an offset of ZERO the target is the listener's own position, the newest frame is already behind it and
// there is nothing older to prefer - the selection is clamped at the newest and does not follow the offset at
// all. That clamp is correct behaviour and it is what the next case is about; measuring the arithmetic through
// it would be measuring the clamp.
TEST_CASE("an av-sync offset moves the drawn frame by that many milliseconds of audio", "[latency][av-sync]") {
    const auto measure = [](float offset_ms) {
        latency_fixture fx;
        fx.set_sync(MP_AV_SYNC_AUDIBLE, offset_ms, 1024);
        mp_track* track = fx.engine.open(mp::tests::write_sine_wav({}, "latency-offset"));
        REQUIRE(mp_engine_play(fx.engine.engine, track, 0) == MP_OK);
        const auto samples = fx.play_and_drain(700);
        mp_engine_stop(fx.engine.engine, MP_FADE_NONE);
        mp_track_close(track);
        INFO("offset " << offset_ms << " ms produced " << samples.size() << " samples");
        REQUIRE(samples.size() > 20);
        return median(steady_errors(samples));
    };

    const double nearer = measure(-50.0f);
    const double further = measure(-100.0f);
    INFO("median av_error_ms: " << nearer << " at offset -50, " << further << " at offset -100, difference "
                                << further - nearer);
    // One hop is 10.67 ms and the picture can only be drawn from a frame the render thread saw, so the
    // selection is quantised and the tolerance is a hop either side rather than a fraction of one.
    REQUIRE(further - nearer == Catch::Approx(50.0).margin(11.0));
}

// The other half of the same claim: with nothing to compensate against, ABI 0.17 draws exactly what 0.16 drew.
// An offline engine has no output buffer, so the listener is level with the mixer and there is no older frame
// to prefer - and this is the path every golden image and every headless test in the suite runs on.
TEST_CASE("with no output buffer, compensation picks the same frame the newest-frame rule does", "[latency][av-sync]") {
    const auto measure = [](mp_av_sync_mode mode) {
        latency_fixture fx;
        fx.set_sync(mode, 0.0f, 1024);
        mp_track* track = fx.engine.open(mp::tests::write_sine_wav({}, "latency-degrade"));
        REQUIRE(mp_engine_play(fx.engine.engine, track, 0) == MP_OK);
        const auto samples = fx.play_and_drain(700);
        mp_engine_stop(fx.engine.engine, MP_FADE_NONE);
        mp_track_close(track);
        REQUIRE(samples.size() > 20);
        for (const auto& s : samples) {
            // The definition depends on this being true offline, so it is asserted rather than assumed.
            REQUIRE(s.audible_mixer_byte_pos == s.mixer_byte_pos);
            REQUIRE(s.mode == static_cast<uint32_t>(mode));
        }
        return median(steady_errors(samples));
    };

    const double newest = measure(MP_AV_SYNC_NEWEST);
    const double audible = measure(MP_AV_SYNC_AUDIBLE);
    INFO("median av_error_ms: " << newest << " newest, " << audible << " audible");
    // Within a hop of each other: the same frame, or at most the neighbouring one where a poll landed
    // differently between the two runs.
    REQUIRE(audible == Catch::Approx(newest).margin(11.0));
}

TEST_CASE("a sample says where every edge of the pipeline was", "[latency][av-sync]") {
    latency_fixture fx;
    fx.set_sync(MP_AV_SYNC_AUDIBLE, 0.0f, 1024);
    mp_track* track = fx.engine.open(mp::tests::write_sine_wav({}, "latency-edges"));
    REQUIRE(mp_engine_play(fx.engine.engine, track, 0) == MP_OK);
    const auto samples = fx.play_and_drain(500);
    mp_engine_stop(fx.engine.engine, MP_FADE_NONE);
    mp_track_close(track);
    REQUIRE(samples.size() > 20);

    uint64_t previous_frame = samples.front().frame_index;
    uint32_t distinct_sequences = 0;
    uint32_t last_sequence = 0;
    for (size_t i = 0; i < samples.size(); ++i) {
        const mp_latency_sample& s = samples[i];
        INFO("sample " << i << " of " << samples.size() << ", frame " << s.frame_index);
        REQUIRE(s.struct_size == sizeof(mp_latency_sample));
        REQUIRE(s.qpc_frequency > 0);
        REQUIRE(s.byte_rate == Catch::Approx(static_cast<double>(k_rate) * k_channels * 4.0));
        REQUIRE(s.analysis_sequence > 0);
        // The pipeline runs forwards: the mixer finished the hop, then this thread saw it, then it presented.
        REQUIRE(s.first_seen_qpc >= s.analysis_qpc);
        REQUIRE(s.present_qpc >= s.first_seen_qpc);
        // Drained oldest first and the counter never goes backwards.
        if (i > 0) {
            REQUIRE(s.frame_index > previous_frame);
            REQUIRE(s.analysis_sequence >= last_sequence);
            // redrawn is exactly "the same analysis frame as the picture before", which is the thing that
            // makes a frame's own first_seen stamp older than the frame it belongs to.
            REQUIRE((s.redrawn != 0) == (s.analysis_sequence == last_sequence));
        }
        if (s.analysis_sequence != last_sequence) {
            ++distinct_sequences;
        }
        previous_frame = s.frame_index;
        last_sequence = s.analysis_sequence;
    }
    // Half a second is about forty-seven hops; asking for more than ten distinct ones only says the pictures
    // were not all of one frozen frame.
    INFO(distinct_sequences << " distinct analysis frames reached the screen over " << samples.size() << " pictures");
    REQUIRE(distinct_sequences > 10);
}

TEST_CASE("the drain is a ring and a caller that is behind loses the oldest samples", "[latency][av-sync]") {
    latency_fixture fx;
    fx.set_sync(MP_AV_SYNC_AUDIBLE, 0.0f, 8); // deliberately tiny
    mp_track* track = fx.engine.open(mp::tests::write_sine_wav({}, "latency-ring"));
    REQUIRE(mp_engine_play(fx.engine.engine, track, 0) == MP_OK);
    fx.play_for(300);
    mp_engine_stop(fx.engine.engine, MP_FADE_NONE);
    mp_track_close(track);

    uint32_t waiting = 0;
    REQUIRE(mp_renderer_drain_latency(fx.renderer, nullptr, &waiting) == MP_OK);
    REQUIRE(waiting <= 8); // never more than the capacity, however many frames were drawn

    std::vector<mp_latency_sample> out(8);
    for (auto& s : out) {
        s.struct_size = sizeof s;
    }
    uint32_t taken = 8;
    REQUIRE(mp_renderer_drain_latency(fx.renderer, out.data(), &taken) == MP_OK);
    REQUIRE(taken == waiting);
    // A drain empties: the ring is a handover and not a log.
    uint32_t after = 0;
    REQUIRE(mp_renderer_drain_latency(fx.renderer, nullptr, &after) == MP_OK);
    REQUIRE(after < waiting);
}

// The struct_size rule (T-140) over the two structs ABI 0.17 adds, because a rule only holds while every new
// export goes through the helpers rather than comparing sizes for itself.
TEST_CASE("an older caller's av-sync structs are served the prefix they carry", "[latency][av-sync][struct_size]") {
    latency_fixture fx;

    // In: a caller whose header ended at `mode`, so it never heard of offset_ms or probe_capacity. Its bytes
    // past struct_size are a sentinel this build must not read.
    struct short_config {
        uint32_t struct_size;
        uint32_t mode;
        uint32_t sentinel[4];
    };
    short_config in{};
    in.struct_size = offsetof(mp_av_sync_config, offset_ms);
    in.mode = MP_AV_SYNC_NEWEST;
    std::fill(std::begin(in.sentinel), std::end(in.sentinel), 0xDEADBEEFu);
    REQUIRE(mp_renderer_set_av_sync(fx.renderer, reinterpret_cast<const mp_av_sync_config*>(&in)) == MP_OK);
    // probe_capacity took its documented default of zero rather than the sentinel: the probe is off.
    uint32_t count = 0;
    REQUIRE(mp_renderer_drain_latency(fx.renderer, nullptr, &count) == MP_E_STATE);
    for (uint32_t word : in.sentinel) {
        REQUIRE(word == 0xDEADBEEFu);
    }

    // Out: an array whose element size is a prefix, so the stride is the caller's and not this build's.
    fx.set_sync(MP_AV_SYNC_AUDIBLE, 0.0f, 64);
    mp_track* track = fx.engine.open(mp::tests::write_sine_wav({}, "latency-prefix"));
    REQUIRE(mp_engine_play(fx.engine.engine, track, 0) == MP_OK);
    fx.play_for(200);
    mp_engine_stop(fx.engine.engine, MP_FADE_NONE);
    mp_track_close(track);

    constexpr uint32_t k_short = offsetof(mp_latency_sample, drawn_mixer_byte_pos);
    std::vector<std::byte> buffer(static_cast<size_t>(k_short) * 4 + sizeof(uint32_t), std::byte{0});
    const auto guard = reinterpret_cast<uint32_t*>(buffer.data() + static_cast<size_t>(k_short) * 4);
    *guard = 0xFEEDFACEu;
    auto* elements = reinterpret_cast<mp_latency_sample*>(buffer.data());
    elements->struct_size = k_short;
    uint32_t taken = 4;
    REQUIRE(mp_renderer_drain_latency(fx.renderer, elements, &taken) == MP_OK);
    REQUIRE(taken > 0);
    REQUIRE(*guard == 0xFEEDFACEu); // nothing was written past the caller's last element
    for (uint32_t i = 0; i < taken; ++i) {
        mp_latency_sample element{};
        std::memcpy(&element, buffer.data() + static_cast<size_t>(i) * k_short, k_short);
        INFO("element " << i);
        REQUIRE(element.struct_size == k_short); // it carries its own size, not this build's
        REQUIRE(element.analysis_sequence > 0);
        REQUIRE(element.frame_index > 0);
    }
}
