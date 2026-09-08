// mp::spsc_ring: unit behaviour and a producer/consumer stress test (docs/build-test-release.md, mpcore/common).
#include "common/spsc_ring.h"

#include <atomic>
#include <catch2/catch_amalgamated.hpp>
#include <cstdint>
#include <thread>
#include <vector>

namespace {

struct frame {
    uint64_t seq;
    uint32_t payload[15]; // 64 bytes with seq
};

} // namespace

TEST_CASE("spsc_ring rounds capacity up to a power of two", "[common][ring]") {
    CHECK(mp::spsc_ring<int>{0}.capacity() == 2);
    CHECK(mp::spsc_ring<int>{1}.capacity() == 2);
    CHECK(mp::spsc_ring<int>{3}.capacity() == 4);
    CHECK(mp::spsc_ring<int>{1000}.capacity() == 1024);
    CHECK(mp::spsc_ring<int>{1024}.capacity() == 1024);
}

TEST_CASE("spsc_ring is FIFO and reports full and empty", "[common][ring]") {
    mp::spsc_ring<int> ring{4};
    int out = -1;
    CHECK(ring.empty());
    CHECK_FALSE(ring.try_pop(out));

    for (int i = 0; i < 4; ++i) {
        CHECK(ring.try_push(i));
    }
    CHECK(ring.size() == 4);
    CHECK_FALSE(ring.try_push(99)); // full

    for (int i = 0; i < 4; ++i) {
        REQUIRE(ring.try_pop(out));
        CHECK(out == i);
    }
    CHECK(ring.empty());
    CHECK_FALSE(ring.try_pop(out));
}

TEST_CASE("spsc_ring survives index wrap-around", "[common][ring]") {
    mp::spsc_ring<int> ring{2};
    int out = 0;
    for (int i = 0; i < 10'000; ++i) {
        REQUIRE(ring.try_push(i));
        REQUIRE(ring.try_push(i + 1));
        CHECK_FALSE(ring.try_push(-1));
        REQUIRE(ring.try_pop(out));
        CHECK(out == i);
        REQUIRE(ring.try_pop(out));
        CHECK(out == i + 1);
    }
}

TEST_CASE("spsc_ring never loses, duplicates or tears frames under stress", "[common][ring][stress]") {
    constexpr uint64_t k_frames = 2'000'000;
    mp::spsc_ring<frame> ring{256};
    std::atomic<bool> producer_done{false};

    std::thread producer{[&] {
        frame f{};
        for (uint64_t seq = 0; seq < k_frames; ++seq) {
            f.seq = seq;
            for (uint32_t& p : f.payload) {
                p = static_cast<uint32_t>(seq * 2654435761u); // detectably derived from seq
            }
            while (!ring.try_push(f)) {
                std::this_thread::yield();
            }
        }
        producer_done.store(true, std::memory_order_release);
    }};

    uint64_t expected = 0;
    uint64_t torn = 0;
    frame f{};
    while (true) {
        if (ring.try_pop(f)) {
            if (f.seq != expected) {
                FAIL("sequence break: expected " << expected << " got " << f.seq);
            }
            const auto want = static_cast<uint32_t>(f.seq * 2654435761u);
            for (uint32_t p : f.payload) {
                if (p != want) {
                    ++torn;
                }
            }
            ++expected;
        } else if (producer_done.load(std::memory_order_acquire) && ring.empty()) {
            break;
        } else {
            std::this_thread::yield();
        }
    }
    producer.join();
    CHECK(expected == k_frames);
    CHECK(torn == 0);
}
