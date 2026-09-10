// E1-S2 gapless-join spike, kept as the measurement. Each pair under tests/fixtures/gapless/ is one continuous
// chirp cut at 2.0 s and encoded as two files (tools/FixtureGen `gapless`). The engine plays a, has b preloaded,
// and is rendered headless across the seam; the output is then matched against the chirp regenerated here.
// Three numbers say what the join did: the lag (in output frames) at which the audio after the seam best matches
// the reference - 0 is sample-continuous, positive means samples were inserted (a gap), negative that samples
// were lost (a trimmed or overlapped join); the residual, the RMS error against the reference at that lag
// relative to the signal's RMS; and the same residual across the seam itself at lag 0. The table this prints is
// the one in docs/spikes/e1-s2-gapless-join.md.
#include "mpcore.h"

#include "offline_engine.h"
#include "source_root.h"

#include <algorithm>
#include <catch2/catch_amalgamated.hpp>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <string>
#include <vector>

namespace {

namespace fs = std::filesystem;
using mp::tests::k_channels;
using mp::tests::k_rate;
using mp::tests::last_error;
using mp::tests::offline_engine;

// The chirp (GaplessFixtureBuilder in FixtureGen): 200 Hz to 2000 Hz over 4 s at 0.25 full scale, defined in
// continuous time so a 44.1 kHz pair resampled by the mixer still matches it at the output rate.
constexpr double k_seconds = 4.0;
constexpr double k_split_s = 2.0;
constexpr double k_f0 = 200.0;
constexpr double k_f1 = 2000.0;
constexpr double k_amplitude = 0.25;

double ref_at(int64_t frame) {
    const double t = static_cast<double>(frame) / k_rate;
    const double phase = 6.283185307179586 * (k_f0 * t + (k_f1 - k_f0) * t * t / (2.0 * k_seconds));
    return k_amplitude * std::sin(phase);
}

float left(const std::vector<float>& out, int64_t frame) {
    return frame < 0 || static_cast<size_t>(frame) * k_channels >= out.size()
               ? 0.0f
               : out[static_cast<size_t>(frame) * k_channels];
}

// Cross-correlation of out[from, to) against the reference delayed by `lag`.
double score(const std::vector<float>& out, int64_t from, int64_t to, int64_t lag) {
    double s = 0;
    for (int64_t k = from; k < to; ++k) {
        s += static_cast<double>(left(out, k)) * ref_at(k - lag);
    }
    return s;
}

// The lag in [centre - range, centre + range] at which out[from, to) best matches the reference: a coarse pass over the
// first 4000 frames of the window, then the full window within 24 frames of the coarse winner.
int64_t best_lag(const std::vector<float>& out, int64_t from, int64_t to, int64_t range, int64_t centre = 0) {
    const int64_t coarse_to = std::min(to, from + 4000);
    int64_t best = centre;
    double best_score = -1e300;
    for (int64_t lag = centre - range; lag <= centre + range; ++lag) {
        const double s = score(out, from, coarse_to, lag);
        if (s > best_score) {
            best_score = s;
            best = lag;
        }
    }
    int64_t fine = best;
    best_score = -1e300;
    for (int64_t lag = best - 24; lag <= best + 24; ++lag) {
        const double s = score(out, from, to, lag);
        if (s > best_score) {
            best_score = s;
            fine = lag;
        }
    }
    return fine;
}

// RMS of (out - reference at lag) over [from, to), relative to the reference's RMS there.
double residual(const std::vector<float>& out, int64_t from, int64_t to, int64_t lag) {
    double err = 0;
    double sig = 0;
    for (int64_t k = from; k < to; ++k) {
        const double r = ref_at(k - lag);
        const double d = static_cast<double>(left(out, k)) - r;
        err += d * d;
        sig += r * r;
    }
    return sig == 0 ? 0.0 : std::sqrt(err / sig);
}

// Where out[from, to) runs out of audio: the start of the silent tail, or `to` if there is none. Only reported
// when a match fails, to say whether the window measured the signal or the end of it.
int64_t first_silent_frame(const std::vector<float>& out, int64_t from, int64_t to) {
    int64_t k = to;
    while (k > from && left(out, k - 1) == 0.0f) {
        --k;
    }
    return k;
}

double max_step_left(const std::vector<float>& out, int64_t from, int64_t to) {
    double worst = 0;
    for (int64_t k = from + 1; k < to; ++k) {
        worst = std::max(worst, static_cast<double>(std::fabs(left(out, k) - left(out, k - 1))));
    }
    return worst;
}

double max_step_ref(int64_t from, int64_t to) {
    double worst = 0;
    for (int64_t k = from + 1; k < to; ++k) {
        worst = std::max(worst, std::fabs(ref_at(k) - ref_at(k - 1)));
    }
    return worst;
}

struct event_log {
    int started = 0;
    int ended = 0;
    int64_t first_ended_b = -1; // a's end: the join position; b ends later on its own with 0
    int64_t last_ended_b = -1;
    int64_t started_b = -1;
    int64_t first_ended_a = 0; // the handles the join names: a ended, b started
    int64_t last_started_a = 0;
    int64_t started_at_frame = -1; // output frames rendered before the callback that fired it
    int64_t rendered = 0;
};

struct pair_result {
    std::string name;
    std::string codec;
    uint32_t rate = 0;
    int64_t a_frames = 0;
    int64_t b_frames = 0;
    int64_t expected_frames = 0;
    int64_t join_frame = -1; // from the event's mixer byte position
    int64_t pre_lag = 0;
    double pre_residual = 0;
    int64_t post_lag = 0;
    double post_residual = 0;
    double seam_residual = 0;
    double step_ratio = 0; // largest step across the seam over the chirp's own largest step there
    event_log events;
};

// What the spike measured (docs/spikes/e1-s2-gapless-join.md) and E1-S3 relies on: the pairs that join
// sample-continuously, with the seam residual each decoder is allowed (its coding error on the chirp, not the
// join's). AAC joined once the engine trimmed the priming itself (T-102). A pair not listed is best-effort - WMA
// through Media Foundation - and only has to join.
struct expectation {
    const char* name;
    double seam_bound;
};
constexpr expectation k_continuous[] = {
    {"wav", 0.001}, {"aiff", 0.001},   {"flac", 0.001}, {"alac", 0.001}, {"wv", 0.001}, {"flac-44k", 0.002},
    {"mp3", 0.05},  {"mp3-44k", 0.05}, {"ogg", 0.05},   {"opus", 0.05},  {"m4a", 0.05},
};

const expectation* expected_continuous(const std::string& name) {
    for (const expectation& e : k_continuous) {
        if (name == e.name) {
            return &e;
        }
    }
    return nullptr;
}

fs::path fixture_root() {
    const auto native = mp::tests::find_native_root();
    return native ? native->parent_path() / "tests" / "fixtures" / "gapless" : fs::path{};
}

fs::path track_file(const fs::path& dir, const char* stem) {
    for (const auto& entry : fs::directory_iterator{dir}) {
        if (entry.is_regular_file() && entry.path().stem() == stem) {
            return entry.path();
        }
    }
    return {};
}

std::string utf8(const fs::path& p) {
    const auto u8 = p.u8string();
    return std::string{u8.begin(), u8.end()};
}

pair_result measure(const fs::path& dir) {
    pair_result r;
    r.name = utf8(dir.filename());
    offline_engine fx;
    mp_track* a = fx.open(utf8(track_file(dir, "a")));
    mp_track* b = fx.open(utf8(track_file(dir, "b")));
    mp_track_info ia{};
    ia.struct_size = sizeof ia;
    mp_track_info ib{};
    ib.struct_size = sizeof ib;
    REQUIRE(mp_track_get_info(a, &ia) == MP_OK);
    REQUIRE(mp_track_get_info(b, &ib) == MP_OK);
    r.codec = ia.codec;
    r.rate = ia.sample_rate;
    r.a_frames = ia.total_frames;
    r.b_frames = ib.total_frames;
    r.expected_frames = static_cast<int64_t>(std::llround(k_split_s * ia.sample_rate));

    mp_engine_set_event_callback(
        fx.engine,
        [](const mp_event* ev, void* user) {
            auto* log = static_cast<event_log*>(user);
            if (ev->type == MP_EVENT_TRACK_STARTED) {
                ++log->started;
                log->started_b = ev->b;
                log->last_started_a = ev->a;
                log->started_at_frame = log->rendered;
            } else if (ev->type == MP_EVENT_TRACK_ENDED) {
                if (log->ended++ == 0) {
                    log->first_ended_b = ev->b;
                    log->first_ended_a = ev->a;
                }
                log->last_ended_b = ev->b;
            }
        },
        &r.events);

    REQUIRE(mp_engine_play(fx.engine, a, 0) == MP_OK);
    REQUIRE(mp_engine_preload_next(fx.engine, b) == MP_OK);

    // 479-frame pulls: 96000 is not a multiple, so the seam falls inside a buffer and a join that is only
    // buffer-accurate cannot pass by luck (480 would put it exactly on a boundary).
    constexpr int64_t k_pull = 479;
    const int64_t total = static_cast<int64_t>((k_seconds + 0.5) * k_rate);
    std::vector<float> out(static_cast<size_t>(total) * k_channels);
    while (r.events.rendered < total) {
        const auto n = static_cast<uint32_t>(std::min<int64_t>(k_pull, total - r.events.rendered));
        REQUIRE(mp_engine_render(fx.engine, out.data() + static_cast<size_t>(r.events.rendered) * k_channels, n) ==
                MP_OK);
        r.events.rendered += n;
    }
    mp_engine_set_event_callback(fx.engine, nullptr, nullptr);
    CHECK(r.events.first_ended_a == reinterpret_cast<int64_t>(a)); // E1-S3: the events name the tracks by handle
    CHECK(r.events.last_started_a == reinterpret_cast<int64_t>(b));

    const auto s = [](double seconds) { return static_cast<int64_t>(seconds * k_rate); };
    constexpr int64_t k_range = 6000; // covers MP3 (1105), AAC (2112 + 1024), Opus (312) and a WMA guess
    r.join_frame = r.events.started == 2 ? r.events.started_b / static_cast<int64_t>(sizeof(float) * k_channels) : -1;
    r.pre_lag = best_lag(out, s(1.0), s(1.9), k_range);
    r.pre_residual = residual(out, s(1.0), s(1.9), r.pre_lag);
    r.post_lag = best_lag(out, s(2.1), s(3.0), k_range);
    r.post_residual = residual(out, s(2.1), s(3.0), r.post_lag);
    r.seam_residual = residual(out, s(1.98), s(2.02), 0);
    r.step_ratio = max_step_left(out, s(1.95), s(2.05)) / max_step_ref(s(1.95), s(2.05));
    return r;
}

} // namespace

TEST_CASE("gapless join per format: a preloaded successor continues the chirp at mix time", "[gapless][spike]") {
    const fs::path root = fixture_root();
    if (root.empty() || !fs::exists(root)) {
        SKIP("tests/fixtures/gapless not found (run detached from the repository)");
    }
    std::vector<fs::path> dirs;
    for (const auto& entry : fs::directory_iterator{root}) {
        if (entry.is_directory()) {
            dirs.push_back(entry.path());
        }
    }
    std::sort(dirs.begin(), dirs.end());
    REQUIRE_FALSE(dirs.empty());

    std::vector<pair_result> rows;
    for (const fs::path& dir : dirs) {
        INFO("pair " << utf8(dir.filename()));
        pair_result r = measure(dir);
        // The join itself: a ends with a successor and b starts, both carrying the same mixer position; b then
        // ends on its own inside the 4.5 s rendered.
        CHECK(r.events.ended == 2);
        CHECK(r.events.started == 2);
        CHECK(r.events.first_ended_b == r.events.started_b);
        CHECK(r.events.started_b > 0);
        CHECK(r.events.last_ended_b == 0);
        if (const expectation* e = expected_continuous(r.name); e != nullptr) {
            CHECK(r.join_frame == static_cast<int64_t>(k_split_s * k_rate)); // the event names the exact frame
            CHECK(r.pre_lag == 0);
            CHECK(r.post_lag == 0);
            CHECK(r.post_residual < e->seam_bound);
            CHECK(r.seam_residual < e->seam_bound);
            CHECK(r.step_ratio < 1.6);
        }
        rows.push_back(r);
    }
    // Every pair the spike proved continuous is present: a missing fixture must not pass by absence.
    for (const expectation& e : k_continuous) {
        INFO("expected pair " << e.name);
        CHECK(std::any_of(rows.begin(), rows.end(), [&](const pair_result& r) { return r.name == e.name; }));
    }

    std::printf(
        "\n| Pair | Codec | Rate | a frames | b frames | Join at | Lag before | Lag after | Residual after | Seam "
        "residual | Step ratio |\n|---|---|---|---|---|---|---|---|---|---|---|\n");
    for (const pair_result& r : rows) {
        std::printf(
            "| %s | %s | %u | %lld (%+lld) | %lld (%+lld) | %lld (%+lld) | %lld | %lld | %.4f | %.4f | %.2f |\n",
            r.name.c_str(), r.codec.c_str(), r.rate, static_cast<long long>(r.a_frames),
            static_cast<long long>(r.a_frames - r.expected_frames), static_cast<long long>(r.b_frames),
            static_cast<long long>(r.b_frames - r.expected_frames), static_cast<long long>(r.join_frame),
            static_cast<long long>(r.join_frame - static_cast<int64_t>(k_split_s * k_rate)),
            static_cast<long long>(r.pre_lag), static_cast<long long>(r.post_lag), r.post_residual, r.seam_residual,
            r.step_ratio);
    }
    std::printf("\n");
    std::fflush(stdout);
}

TEST_CASE("preload_next is cleared by stop, by closing the track and by playing it by hand", "[gapless]") {
    const fs::path root = fixture_root();
    if (root.empty() || !fs::exists(root / "wav")) {
        SKIP("tests/fixtures/gapless not found (run detached from the repository)");
    }
    offline_engine fx;
    mp_track* a = fx.open(utf8(track_file(root / "wav", "a")));
    mp_track* b = fx.open(utf8(track_file(root / "wav", "b")));

    struct counts {
        int started = 0;
        int ended = 0;
    } seen;
    mp_engine_set_event_callback(
        fx.engine,
        [](const mp_event* ev, void* user) {
            auto* c = static_cast<counts*>(user);
            if (ev->type == MP_EVENT_TRACK_STARTED) {
                ++c->started;
            } else if (ev->type == MP_EVENT_TRACK_ENDED) {
                ++c->ended;
            }
        },
        &seen);

    SECTION("a preloaded track cannot be the one playing") {
        REQUIRE(mp_engine_play(fx.engine, a, 0) == MP_OK);
        CHECK(mp_engine_preload_next(fx.engine, a) == MP_E_INVALID_ARG);
        CHECK(mp_engine_preload_next(fx.engine, b) == MP_OK);
        CHECK(mp_engine_preload_next(fx.engine, nullptr) == MP_OK); // cleared: a ends alone
        fx.render(k_rate * 5 / 2);
        CHECK(seen.ended == 1);
        CHECK(seen.started == 1);
    }

    SECTION("stop drops the queue") {
        REQUIRE(mp_engine_play(fx.engine, a, 0) == MP_OK);
        REQUIRE(mp_engine_preload_next(fx.engine, b) == MP_OK);
        REQUIRE(mp_engine_stop(fx.engine, MP_FADE_NONE) == MP_OK);
        REQUIRE(mp_engine_play(fx.engine, a, 1900) == MP_OK);
        fx.render(k_rate / 2);
        CHECK(seen.ended == 1);
        CHECK(seen.started == 2);
        CHECK(mp::tests::all_zero(fx.render(480), 0));
    }

    SECTION("closing the queued track drops it") {
        REQUIRE(mp_engine_play(fx.engine, a, 1900) == MP_OK);
        REQUIRE(mp_engine_preload_next(fx.engine, b) == MP_OK);
        REQUIRE(mp_track_close(b) == MP_OK);
        fx.render(k_rate / 2);
        CHECK(seen.ended == 1);
        CHECK(seen.started == 1);
    }

    SECTION("playing the queued track by hand consumes it") {
        REQUIRE(mp_engine_play(fx.engine, a, 1900) == MP_OK);
        REQUIRE(mp_engine_preload_next(fx.engine, b) == MP_OK);
        REQUIRE(mp_engine_play(fx.engine, b, 1900) == MP_OK);
        fx.render(k_rate / 2);
        CHECK(seen.ended == 1); // b ended alone: nothing was queued behind it
        CHECK(seen.started == 2);
    }

    SECTION("the join carries the clock across: position restarts at b's origin") {
        REQUIRE(mp_engine_play(fx.engine, a, 1900) == MP_OK);
        REQUIRE(mp_engine_preload_next(fx.engine, b) == MP_OK);
        fx.render(k_rate / 4); // 100 ms of a, then 150 ms of b
        CHECK(seen.started == 2);
        CHECK(fx.position_ms() == Catch::Approx(150).margin(15));
    }
    mp_engine_set_event_callback(fx.engine, nullptr, nullptr);
}

TEST_CASE("an AAC track seeks and pauses through the trimming wrapper", "[gapless][mp4]") {
    const fs::path root = fixture_root();
    if (root.empty() || !fs::exists(root / "m4a")) {
        SKIP("tests/fixtures/gapless not found (run detached from the repository)");
    }
    offline_engine fx;
    mp_track* a = fx.open(utf8(track_file(root / "m4a", "a")));
    mp_track_info info{};
    info.struct_size = sizeof info;
    REQUIRE(mp_track_get_info(a, &info) == MP_OK);
    CHECK(info.total_frames == 96000); // the valid frames, not the decoder's 97 280
    CHECK(info.duration_ms == 2000);

    REQUIRE(mp_engine_play(fx.engine, a, 0) == MP_OK);
    fx.render(k_rate / 2);
    {
        const mp_result sr = mp_engine_seek(fx.engine, 1000);
        INFO(last_error());
        REQUIRE(sr == MP_OK);
    }
    std::vector<float> out(static_cast<size_t>(k_rate) * k_channels);
    // Render 1 s after the seek and match it against the chirp: the audio comes from about 1.0 s in (a Media
    // Foundation seek lands near, not on, the frame) and the clock follows the wrapper's position.
    const std::vector<float> after = fx.render(k_rate);
    const int64_t expected_lag = -static_cast<int64_t>(k_rate); // output frame k holds chirp frame k + 48000
    // The seek's inexactness bounds the measurement at both ends. The lag may miss by k_seek_slack; and because
    // the seek is at the track's midpoint, leaving exactly the 1 s being rendered, a seek that lands that much
    // late leaves that many frames of the render past the track's end. Measuring the whole render left 100
    // frames of margin against a seek documented as accurate only to 100 ms, and CI found the edge: on
    // windows-2025-vs2026 Media Foundation landed about one AAC frame (1024) later than it does here, so the
    // window's last 923 frames were the silence after the track and the residual read 0.143 - which is
    // sqrt(coding^2 + 923/45400) exactly, the end of the audio rather than a bad join. So measure only what the
    // seek guarantees. The bound stays where it was: a real misalignment still fails it.
    constexpr int64_t k_seek_slack = k_rate / 10; // 100 ms
    {
        const int64_t from = 100 + mp::tests::k_fade_frames;
        const int64_t to = k_rate - 100 - k_seek_slack;
        const int64_t lag = best_lag(after, from, to, 6000, expected_lag);
        INFO("lag " << lag << " (expected " << expected_lag << "), audio to frame "
                    << first_silent_frame(after, from, to) << " of " << to);
        CHECK(std::llabs(lag - expected_lag) < k_seek_slack); // within 100 ms: inexact, as documented
        CHECK(residual(after, from, to, lag) < 0.05);
    }
    CHECK(fx.position_ms() == Catch::Approx(2000).margin(150));

    // The wrapper ends at the valid count: the track ends exactly when its 96 000 frames are out.
    REQUIRE(mp_engine_play(fx.engine, a, 1900) == MP_OK);
    int ended = 0;
    mp_engine_set_event_callback(
        fx.engine,
        [](const mp_event* ev, void* user) {
            if (ev->type == MP_EVENT_TRACK_ENDED) {
                ++*static_cast<int*>(user);
            }
        },
        &ended);
    fx.render(k_rate / 2);
    CHECK(ended == 1);
    mp_engine_set_event_callback(fx.engine, nullptr, nullptr);
    (void)out;
}

// T-108, found by the soak runner (E1-S11): every gapless join whose source rate differs from the mixer's counted
// one underrun on a real device, and the continuity assertions above cannot see it - they check the samples, and
// this is a short read whose tail is zero-filled. Rendered headless, so it is measured at the same pull the device
// makes without needing one. The same-rate pair is the control: whatever the 44.1 kHz pair does, the 48 kHz pair
// must not do, or the count is measuring the harness rather than the join.
TEST_CASE("a join into a source at a different rate to the mixer does not count an underrun", "[gapless]") {
    const fs::path root = fixture_root();
    if (root.empty() || !fs::exists(root)) {
        SKIP("tests/fixtures/gapless not found (run detached from the repository)");
    }

    const auto underruns_across_a_join = [&](const char* pair, uint32_t pull) -> uint64_t {
        const fs::path dir = root / pair;
        if (!fs::exists(dir)) {
            SKIP(std::string{"tests/fixtures/gapless/"} + pair + " not found");
        }
        offline_engine fx;
        mp_track* a = fx.open(utf8(track_file(dir, "a")));
        mp_track* b = fx.open(utf8(track_file(dir, "b")));
        REQUIRE(mp_engine_play(fx.engine, a, 0) == MP_OK);
        REQUIRE(mp_engine_preload_next(fx.engine, b) == MP_OK);
        // Past the 2.0 s seam and short of b's end, so the only boundary crossed is the join itself.
        const int64_t total = k_rate * 3;
        std::vector<float> out(static_cast<size_t>(pull) * k_channels);
        for (int64_t done = 0; done < total; done += pull) {
            REQUIRE(mp_engine_render(fx.engine, out.data(), pull) == MP_OK);
        }
        mp_engine_stats stats{};
        stats.struct_size = sizeof stats;
        REQUIRE(mp_engine_get_stats(fx.engine, &stats) == MP_OK);
        return stats.underruns;
    };

    // 96000 frames in: a multiple of 480 and not of 479, so the same pair joins on a pull boundary and inside a
    // pull. That is the whole difference between these two numbers.
    const uint64_t on_a_boundary = underruns_across_a_join("flac", 480);
    const uint64_t inside_a_pull = underruns_across_a_join("flac", 479);
    const uint64_t resampled = underruns_across_a_join("flac-44k", 480);
    INFO("48 kHz on a pull boundary: " << on_a_boundary << "; inside a pull: " << inside_a_pull
                                       << "; 44.1 kHz: " << resampled);
    CHECK(inside_a_pull == 0);
    CHECK(resampled == 0);
    CHECK(on_a_boundary == 0);
}
