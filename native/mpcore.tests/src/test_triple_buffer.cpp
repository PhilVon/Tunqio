// E4-S1: the triple buffer the analysis thread publishes frames through (AC-114, the tearing half).
//
// What has to be true is narrow and worth stating: a consumer either gets a frame exactly as one publish left it
// or gets told there is none. Never a mixture of two. The stress test is the proof, and it is written so that a
// mixture is arithmetic rather than a judgement - each published item is internally consistent by construction
// (every element is the same number), so a torn read is a single comparison and names the publication it came
// from. It runs two consumers as well as one, because that is the case the swap-the-back-buffer triple buffer
// cannot do and the reason this one is built the way it is.
#include "common/triple_buffer.h"

#include <atomic>
#include <catch2/catch_amalgamated.hpp>
#include <cstdint>
#include <thread>
#include <vector>

namespace {

// Big enough that a copy is not instantaneous: the whole point is to give the producer room to overwrite what a
// consumer is reading. An mp_analysis_frame is about this size.
struct payload {
    static constexpr size_t k_words = 1536;
    uint64_t words[k_words];
};

payload make(uint64_t value) {
    payload p{};
    for (size_t i = 0; i < payload::k_words; ++i) {
        p.words[i] = value;
    }
    return p;
}

// The publication a payload claims to be, or 0 if it is a mixture of two.
uint64_t whole(const payload& p) {
    const uint64_t first = p.words[0];
    for (size_t i = 1; i < payload::k_words; ++i) {
        if (p.words[i] != first) {
            return 0;
        }
    }
    return first;
}

} // namespace

TEST_CASE("a triple buffer with nothing in it has nothing to give", "[triple][analysis]") {
    mp::triple_buffer<payload> buffer;
    payload out{};
    CHECK(buffer.published() == 0);
    CHECK_FALSE(buffer.try_get(out));
}

TEST_CASE("the front is the newest thing published", "[triple][analysis]") {
    mp::triple_buffer<payload> buffer;
    payload out{};
    for (uint64_t i = 1; i <= 10; ++i) {
        buffer.publish(make(i));
        REQUIRE(buffer.try_get(out));
        CHECK(whole(out) == i);
    }
    CHECK(buffer.published() == 10);

    // Reading does not consume: the same front is there until something replaces it, which is what a 30 Hz poll
    // over a 94 Hz producer needs and what distinguishes this from the ring.
    REQUIRE(buffer.try_get(out));
    CHECK(whole(out) == 10);
    CHECK(buffer.published() == 10);
}

TEST_CASE("the front is never torn under a writer and two readers", "[triple][analysis][stress]") {
    mp::triple_buffer<payload> buffer;
    std::atomic<bool> stop{false};
    std::atomic<uint64_t> torn{0};
    std::atomic<uint64_t> went_backwards{0};
    std::atomic<uint64_t> reads{0};
    std::atomic<uint64_t> refusals{0};

    // Flat out, not at 94 Hz: at the real rate the producer could not reach a slot a consumer is copying inside a
    // day of testing, so the race that matters would never be run.
    constexpr uint64_t k_publications = 400000;
    std::thread producer{[&] {
        for (uint64_t i = 1; i <= k_publications; ++i) {
            buffer.publish(make(i));
        }
        stop.store(true, std::memory_order_release);
    }};

    const auto consume = [&] {
        uint64_t newest = 0;
        payload out{};
        while (!stop.load(std::memory_order_acquire)) {
            if (!buffer.try_get(out)) {
                refusals.fetch_add(1, std::memory_order_relaxed);
                continue;
            }
            reads.fetch_add(1, std::memory_order_relaxed);
            const uint64_t got = whole(out);
            if (got == 0) {
                torn.fetch_add(1, std::memory_order_relaxed);
                continue;
            }
            // The front only ever moves forwards, so a consumer that has seen publication n must never be handed
            // something older: that would be a stale slot presented as the front.
            if (got < newest) {
                went_backwards.fetch_add(1, std::memory_order_relaxed);
            }
            newest = got;
        }
    };

    std::thread reader_a{consume};
    std::thread reader_b{consume};
    producer.join();
    reader_a.join();
    reader_b.join();

    INFO("reads " << reads.load() << ", refusals " << refusals.load() << " of " << k_publications
                  << " publications");
    CHECK(reads.load() > 1000); // the readers really did run against a moving producer
    CHECK(torn.load() == 0);
    CHECK(went_backwards.load() == 0);
}

TEST_CASE("a consumer lapped mid-copy is refused rather than given a mixture", "[triple][analysis]") {
    // The refusal path is the one that is invisible in the stress test above (it shows up only as a count), so
    // it is worth reaching on purpose: a publish that leaves a slot marked in-flight must not be readable.
    mp::triple_buffer<payload> buffer;
    payload out{};
    buffer.publish(make(1));
    REQUIRE(buffer.try_get(out));

    std::atomic<bool> stop{false};
    std::atomic<uint64_t> torn{0};
    std::thread producer{[&] {
        for (uint64_t i = 2; !stop.load(std::memory_order_acquire); ++i) {
            buffer.publish(make(i));
        }
    }};

    uint64_t taken = 0;
    uint64_t refused = 0;
    for (int i = 0; i < 200000; ++i) {
        payload got{};
        if (buffer.try_get(got)) {
            ++taken;
            if (whole(got) == 0) {
                torn.fetch_add(1, std::memory_order_relaxed);
            }
        } else {
            ++refused;
        }
    }
    stop.store(true, std::memory_order_release);
    producer.join();

    INFO("took " << taken << ", refused " << refused);
    CHECK(torn.load() == 0); // whatever came back was whole, however many times it refused
}
