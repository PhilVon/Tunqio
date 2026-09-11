// Single-producer / many-consumer publication slot: the newest complete item, never a half-written one.
//
// The analysis thread produces one mp_analysis_frame per 512-frame hop (93.75 Hz at 48 kHz) and two unrelated
// consumers read it: the managed theming poll through mp_analysis_try_get_latest (~30 Hz) and, from E4-S3, the
// render thread in-process (up to the refresh rate). Neither may block the producer and neither may wait on the
// other, so the producer never takes a lock and a consumer never publishes anything - which rules out the
// swap-the-back-buffer triple buffer, whose consumer claims a slot and so admits only one of them.
//
// Three slots and a stamp instead (docs/build-test-release.md: "the triple buffer front is always the newest
// complete frame"). The producer writes into the slot after the front, stamps it and moves the front; a consumer
// copies the front and re-reads the stamp, and only a stamp that did not move means the copy it took was whole.
//
// Why three and not two: the producer must never write the slot a consumer is copying, and with the front plus
// the slot being filled both spoken for, a third is what leaves the consumer somewhere safe. A consumer only
// loses the race if the producer laps it - three publications, 32 ms of audio, while a 6 KB memcpy runs - so the
// retry is a correctness argument, not a hot path. try_get gives up after a few attempts rather than spinning:
// the caller wanted the newest frame, and a caller that cannot have one is better told so than parked.
//
// The copy out of a slot the producer may be writing is the one place this is not a data race by construction,
// and the stamp is what makes it observable rather than silent. Slots are trivially copyable and copied with
// memcpy, so a torn read is arithmetic on floats and never a broken invariant; the stamp check then discards it.
#pragma once

#include <atomic>
#include <cstdint>
#include <cstring>
#include <type_traits>

namespace mp {

template <typename T> class triple_buffer {
    static_assert(std::is_trivially_copyable_v<T>, "triple_buffer slots are memcpy'd between threads");

public:
    // Attempts before try_get gives up. Each failure means the producer published three times during one copy.
    static constexpr int k_read_attempts = 4;

    triple_buffer() = default;
    triple_buffer(const triple_buffer&) = delete;
    triple_buffer& operator=(const triple_buffer&) = delete;

    // Producer only. Publishes `value` as the new front.
    void publish(const T& value) noexcept {
        slot& s = slots_[next_];
        // 0 means "being written": a consumer that lands here has nothing whole to take, and would rather be
        // told so than copy a mixture of two frames.
        s.stamp.store(0, std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_release);
        std::memcpy(&s.value, &value, sizeof(T));
        s.stamp.store(++stamp_, std::memory_order_release);
        front_.store(next_, std::memory_order_release);
        next_ = static_cast<uint32_t>((next_ + 1) % 3);
    }

    // Any consumer thread. False when nothing has been published, or when the producer lapped the copy
    // k_read_attempts times running (which has not been seen outside a test that publishes flat out).
    bool try_get(T& out) const noexcept {
        for (int attempt = 0; attempt < k_read_attempts; ++attempt) {
            const slot& s = slots_[front_.load(std::memory_order_acquire)];
            const uint64_t before = s.stamp.load(std::memory_order_acquire);
            if (before == 0) {
                continue; // being written right now
            }
            std::memcpy(&out, &s.value, sizeof(T));
            std::atomic_thread_fence(std::memory_order_acquire);
            if (s.stamp.load(std::memory_order_relaxed) == before) {
                return true;
            }
        }
        return false;
    }

    // The front's publication number, which is how many have been published (0 = nothing yet). Readable from
    // either side; a diagnostic, not a synchronisation point.
    uint64_t published() const noexcept {
        return slots_[front_.load(std::memory_order_acquire)].stamp.load(std::memory_order_acquire);
    }

private:
    struct slot {
        std::atomic<uint64_t> stamp{0}; // 0 = never written or being written; otherwise the publication number
        T value{};
    };

    slot slots_[3];
    std::atomic<uint32_t> front_{0};
    uint32_t next_ = 1;  // producer only: the slot to fill next, never the front
    uint64_t stamp_ = 0; // producer only
};

} // namespace mp
