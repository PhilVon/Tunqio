// E1-S8: the analysis tap. A DSP on the mixer hands the analysis thread fixed 512-frame hops through the
// lock-free ring, each carrying the mixer byte position of its first frame.
//
// Two things are worth proving and neither is about the ring in isolation (test_spsc_ring.cpp already stresses
// that): that the tap's arithmetic survives being written to in whatever sized pieces BASS chooses, so no frame
// is lost, duplicated or torn and no position drifts; and that it costs the audio thread almost nothing.
#include "mpcore.h"

#include "analysis/tap.h"
#include "audio/bass_engine.h"
#include "common/rt_guard.h"
#include "offline_engine.h"
#include "wav_fixture.h"

#include <atomic>
#include <catch2/catch_amalgamated.hpp>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <thread>
#include <vector>

namespace {

using mp::analysis::tap;
using mp::analysis::tap_block;

constexpr uint32_t k_channels = 2;
constexpr uint32_t k_frame_bytes = k_channels * static_cast<uint32_t>(sizeof(float));

// A sample value that says exactly which frame and channel it is, so a torn block is arithmetic, not a guess.
float sample_for(int64_t frame, uint32_t channel) {
    return static_cast<float>(frame) + static_cast<float>(channel) / 16.0f;
}

// `frames` frames of that, starting at absolute frame `first`.
std::vector<float> make_frames(int64_t first, uint32_t frames) {
    std::vector<float> buffer(static_cast<size_t>(frames) * k_channels);
    for (uint32_t f = 0; f < frames; ++f) {
        for (uint32_t c = 0; c < k_channels; ++c) {
            buffer[static_cast<size_t>(f) * k_channels + c] = sample_for(first + f, c);
        }
    }
    return buffer;
}

// Checks a block is exactly the frames its position claims, and returns the frame it started at.
int64_t check_block(const tap_block& block) {
    REQUIRE(block.frames == tap_block::k_frames);
    REQUIRE(block.channels == k_channels);
    REQUIRE(block.mixer_byte_pos % k_frame_bytes == 0);
    const int64_t first = block.mixer_byte_pos / k_frame_bytes;
    for (uint32_t f = 0; f < block.frames; ++f) {
        for (uint32_t c = 0; c < k_channels; ++c) {
            const float want = sample_for(first + f, c);
            const float got = block.samples[static_cast<size_t>(f) * k_channels + c];
            if (got != want) {
                FAIL("torn block at frame " << (first + f) << " channel " << c << ": want " << want << " got " << got);
            }
        }
    }
    return first;
}

} // namespace

TEST_CASE("the tap publishes whole hops wherever the writes fall", "[analysis][tap]") {
    tap t;
    t.reset(k_channels);
    CHECK(t.published() == 0);
    CHECK(t.byte_position() == 0);

    // Pieces that never divide the hop evenly: 100 frames at a time crosses every hop boundary mid-write, and
    // 21 of them is 2100 frames - four whole hops and a bit, so the last one is still being staged at the end.
    constexpr uint32_t k_piece = 100;
    constexpr int k_pieces = 21;
    constexpr int64_t k_total = static_cast<int64_t>(k_piece) * k_pieces;
    for (int i = 0; i < k_pieces; ++i) {
        const std::vector<float> buffer = make_frames(static_cast<int64_t>(i) * k_piece, k_piece);
        t.write(buffer.data(), k_piece, 0);
    }

    CHECK(t.byte_position() == k_total * k_frame_bytes);
    CHECK(t.published() == 4);
    CHECK(t.dropped() == 0);

    tap_block block;
    for (int64_t hop = 0; hop < 4; ++hop) {
        REQUIRE(t.try_read(block));
        CHECK(check_block(block) == hop * tap_block::k_frames);
    }
    CHECK_FALSE(t.try_read(block)); // the partial fifth hop is not published until it is whole
}

TEST_CASE("a hop is published only once it is complete", "[analysis][tap]") {
    tap t;
    t.reset(k_channels);
    const std::vector<float> nearly = make_frames(0, tap_block::k_frames - 1);
    t.write(nearly.data(), tap_block::k_frames - 1, 0);
    tap_block block;
    CHECK_FALSE(t.try_read(block));

    const std::vector<float> last = make_frames(tap_block::k_frames - 1, 1);
    t.write(last.data(), 1, 12345);
    REQUIRE(t.try_read(block));
    CHECK(check_block(block) == 0);
    CHECK(block.qpc_ticks == 12345);
}

TEST_CASE("reset puts the byte origin back with the mixer it belongs to", "[analysis][tap]") {
    tap t;
    t.reset(k_channels);
    const std::vector<float> buffer = make_frames(0, tap_block::k_frames * 2);
    t.write(buffer.data(), tap_block::k_frames * 2, 0);
    REQUIRE(t.published() == 2);

    // A new mixer: the old blocks are about audio that is no longer being made, and position 0 means this one.
    t.reset(k_channels);
    CHECK(t.byte_position() == 0);
    CHECK(t.published() == 0);
    tap_block block;
    CHECK_FALSE(t.try_read(block));

    t.write(buffer.data(), tap_block::k_frames, 0);
    REQUIRE(t.try_read(block));
    CHECK(block.mixer_byte_pos == 0);
}

TEST_CASE("a consumer that falls behind loses hops and is told how many", "[analysis][tap]") {
    tap t;
    t.reset(k_channels);
    // Fill the ring and then some, without reading: the tap must not block, grow or corrupt what is already in.
    const std::vector<float> buffer = make_frames(0, tap_block::k_frames);
    const auto hops = static_cast<int64_t>(tap::k_blocks) + 5;
    for (int64_t hop = 0; hop < hops; ++hop) {
        const std::vector<float> piece = make_frames(hop * tap_block::k_frames, tap_block::k_frames);
        t.write(piece.data(), tap_block::k_frames, 0);
    }
    CHECK(t.published() == tap::k_blocks);
    CHECK(t.dropped() == 5);

    // What did get through is intact and in order, and the byte position never stopped counting.
    tap_block block;
    for (size_t i = 0; i < tap::k_blocks; ++i) {
        REQUIRE(t.try_read(block));
        CHECK(check_block(block) == static_cast<int64_t>(i) * tap_block::k_frames);
    }
    CHECK(t.byte_position() == hops * tap_block::k_frames * k_frame_bytes);
}

TEST_CASE("a mixer with more channels than a block carries is narrowed, not torn", "[analysis][tap]") {
    tap t;
    constexpr uint32_t k_surround = tap_block::k_max_channels + 2;
    t.reset(k_surround);
    CHECK(t.source_channels() == k_surround);
    CHECK(t.block_channels() == tap_block::k_max_channels);

    std::vector<float> buffer(static_cast<size_t>(tap_block::k_frames) * k_surround);
    for (uint32_t f = 0; f < tap_block::k_frames; ++f) {
        for (uint32_t c = 0; c < k_surround; ++c) {
            buffer[static_cast<size_t>(f) * k_surround + c] = sample_for(f, c);
        }
    }
    t.write(buffer.data(), tap_block::k_frames, 0);

    tap_block block;
    REQUIRE(t.try_read(block));
    CHECK(block.channels == tap_block::k_max_channels);
    // The block's byte position still counts the mixer's own frames, which is what the clock counts.
    CHECK(block.mixer_byte_pos == 0);
    uint32_t wrong = 0;
    for (uint32_t f = 0; f < block.frames; ++f) {
        for (uint32_t c = 0; c < block.channels; ++c) {
            wrong += block.samples[static_cast<size_t>(f) * block.channels + c] != sample_for(f, c) ? 1 : 0;
        }
    }
    CHECK(wrong == 0); // one assertion, not 4096: a per-sample CHECK buries a real failure in its own output
}

TEST_CASE("the tap loses, duplicates and tears nothing across threads", "[analysis][tap][stress]") {
    tap t;
    t.reset(k_channels);
    // Ragged writes from one thread while another reads: the shape of the real thing, where BASS hands over
    // whatever a mixer read produced and the analysis thread is scheduled when it is scheduled. The producer
    // waits rather than overruns the ring, because AC-56 is about what the ring does to frames it accepted -
    // the drop path when the consumer really is too slow is the "falls behind" case above, and is counted there.
    constexpr int64_t k_hops = 2000;
    constexpr int64_t k_total_frames = k_hops * tap_block::k_frames;
    std::atomic<bool> producer_done{false};

    std::thread producer{[&] {
        int64_t first = 0;
        uint32_t piece = 1;
        while (first < k_total_frames) {
            // Back off instead of dropping, leaving room for everything one write can publish: a piece of up to
            // 997 frames on top of 511 already staged completes two hops, so one free slot is not enough.
            while (t.pending() + 2 > tap::k_blocks) {
                std::this_thread::yield();
            }
            const auto n = static_cast<uint32_t>(std::min<int64_t>(piece, k_total_frames - first));
            const std::vector<float> buffer = make_frames(first, n);
            t.write(buffer.data(), n, 0);
            first += n;
            piece = piece % 997 + 1; // every size from 1 to 997, so every alignment against the 512-frame hop
        }
        producer_done.store(true, std::memory_order_release);
    }};

    int64_t expected = 0; // the frame the next block must start at
    int64_t read = 0;
    tap_block block;
    while (true) {
        if (t.try_read(block)) {
            const int64_t first = check_block(block);
            if (first != expected) {
                FAIL("block out of sequence: expected " << expected << " got " << first);
            }
            expected = first + tap_block::k_frames;
            ++read;
        } else if (producer_done.load(std::memory_order_acquire) && t.pending() == 0) {
            break;
        } else {
            std::this_thread::yield();
        }
    }
    producer.join();

    INFO("read " << read << " of " << k_hops << " hops, " << t.dropped() << " dropped");
    CHECK(t.byte_position() == k_total_frames * k_frame_bytes);
    CHECK(read == k_hops); // every hop, exactly once, in order, with every sample where it belongs
    CHECK(t.dropped() == 0);
}

TEST_CASE("the tap costs the audio thread well under a buffer", "[analysis][tap][benchmark]") {
    // AC-57: under 0.2 ms per DSP callback at 48 kHz stereo. A shared-mode period is 480 frames (10 ms), so the
    // budget is 2% of the buffer the callback is filling.
    tap t;
    t.reset(k_channels);
    constexpr uint32_t k_period_frames = 480;
    constexpr int k_iterations = 20000; // 200 s of audio, far more than a timer's resolution needs
    const std::vector<float> buffer = make_frames(0, k_period_frames);

    // Warm: the first calls touch pages the ring only just allocated. Drained as we go, or the ring fills and
    // the warm-up measures the drop path instead of the publish path.
    tap_block block;
    for (int i = 0; i < 100; ++i) {
        t.write(buffer.data(), k_period_frames, 0);
        while (t.try_read(block)) {
        }
    }
    t.reset(k_channels);

    const auto start = std::chrono::steady_clock::now();
    for (int i = 0; i < k_iterations; ++i) {
        t.write(buffer.data(), k_period_frames, 0);
        // Drain as the analysis thread would, so the measurement is of a tap that is publishing, not one that
        // fills up after sixteen hops and then only counts drops.
        while (t.try_read(block)) {
        }
    }
    const auto elapsed = std::chrono::steady_clock::now() - start;
    const double per_call_ms =
        std::chrono::duration<double, std::milli>(elapsed).count() / static_cast<double>(k_iterations);

    INFO("tap: " << per_call_ms << " ms per 480-frame callback (budget 0.2 ms)");
    CHECK(per_call_ms < 0.2);
    CHECK(t.dropped() == 0);
}

#if defined(MP_DEBUG) && MP_DEBUG
TEST_CASE("the tap allocates nothing on the audio path", "[analysis][tap][rt]") {
    tap t;
    t.reset(k_channels);
    const std::vector<float> buffer = make_frames(0, 480);
    mp::rt::reset_violations();
    {
        mp::rt::scope inside;
        for (int i = 0; i < 200; ++i) {
            t.write(buffer.data(), 480, 0);
        }
    }
    CHECK(mp::rt::violations() == 0);
}
#endif

TEST_CASE("the engine's tap follows the mixer and lines up with the clock", "[analysis][tap][engine]") {
    mp::tests::offline_engine fx;
    auto* core = reinterpret_cast<mp::audio::engine*>(fx.engine);
    tap& engine_tap = core->analysis_tap();

    // The engine's tap is not an unread tap any more: create_mixer starts the analysis thread on it (E4-S1) and
    // that thread is the ring's consumer. This test is about the tap's own arithmetic against the clock, and to
    // ask the tap anything it has to BE the consumer, so the analyzer is stopped first. Two reasons, both hard
    // rather than tidy: a draining consumer frees slots the producer immediately refills, so how many hops are
    // published becomes a scheduling outcome and not a number; and try_read below would be a second consumer on
    // a ring whose head belongs to exactly one, which is a race and not merely a slow path. Nothing restarts it
    // here - create_mixer is the only thing that does, and no mixer is rebuilt in this test - and stopping it
    // twice is legal, which is what the engine's destructor will go on to do.
    core->analysis_thread().stop();
    REQUIRE_FALSE(core->analysis_thread().running());
    // Stopped before the first render, so the counters the assertions below read start where reset() left them.
    REQUIRE(engine_tap.published() == 0);
    REQUIRE(engine_tap.dropped() == 0);
    CHECK(engine_tap.source_channels() == mp::tests::k_channels);

    mp::tests::wav_spec spec;
    spec.seconds = 1.0;
    mp_track* track = fx.open(mp::tests::write_sine_wav(spec, "tap-engine"));
    REQUIRE(mp_engine_play(fx.engine, track, 0) == MP_OK);

    std::vector<float> out(static_cast<size_t>(mp::tests::k_buffer_frames) * mp::tests::k_channels);
    for (int i = 0; i < 50; ++i) { // 500 ms
        REQUIRE(mp_engine_render(fx.engine, out.data(), mp::tests::k_buffer_frames) == MP_OK);
    }

    // The tap has seen exactly what the mixer produced, which is what the clock reports.
    mp_clock clock{};
    clock.struct_size = sizeof clock;
    REQUIRE(mp_engine_get_clock(fx.engine, &clock) == MP_OK);
    CHECK(engine_tap.byte_position() == clock.mixer_byte_pos);
    // Every hop is accounted for: published plus dropped is exactly what the mixer's byte position says was
    // produced. This one holds whether or not anything is reading, because both counters belong to the producer.
    CHECK(engine_tap.published() + engine_tap.dropped() ==
          static_cast<uint64_t>(clock.mixer_byte_pos) / (tap_block::k_frames * mp::tests::k_channels * 4));
    // 500 ms is 46 hops, and with the analyzer stopped above nothing has read any of them, so the ring filled
    // once and the other 30 were dropped: that is the design. The number is k_blocks only because this test made
    // itself the sole consumer - on a live engine the analysis thread drains as the tap fills and publishes far
    // more than the ring holds, at a count that is scheduling and not arithmetic. Do not re-pin it there.
    CHECK(engine_tap.published() == mp::analysis::tap::k_blocks);

    // And a block's position is a real place in that stream, with audible audio in it. Reading the engine's own
    // tap is only allowed because the analyzer above is stopped; with it running this would be the second
    // consumer on a single-consumer ring, and the block it returned could be one the producer was overwriting.
    tap_block block;
    REQUIRE(engine_tap.try_read(block));
    CHECK(block.mixer_byte_pos >= 0);
    CHECK(block.mixer_byte_pos < clock.mixer_byte_pos);
    CHECK(block.channels == mp::tests::k_channels);
    double sum = 0;
    for (uint32_t i = 0; i < block.frames * block.channels; ++i) {
        sum += static_cast<double>(block.samples[i]) * block.samples[i];
    }
    CHECK(std::sqrt(sum / (block.frames * block.channels)) > 0.0);
}
