// Single-producer / many-consumer publication slot: the newest complete item, never a half-written one.
//
// The analysis thread produces one mp_analysis_frame per 512-frame hop (93.75 Hz at 48 kHz) and two unrelated
// consumers read it: the managed theming poll through mp_analysis_try_get_latest (~30 Hz) and, from E4-S3, the
// render thread in-process (up to the refresh rate). Neither may block the producer and neither may wait on the
// other, so the producer never takes a lock and a consumer never publishes anything - which rules out the
// swap-the-back-buffer triple buffer, whose consumer claims a slot and so admits only one of them.
//
// Three slots and a publication counter instead (docs/build-test-release.md: "the triple buffer front is always
// the newest complete frame"). Publication n lives in slot n % 3: the producer fills that slot, stamps it with n
// and then stores n into front_; a consumer reads front_, copies the slot that number names and re-reads the
// stamp, and only a stamp still equal to the number it asked for means the copy it took was whole.
//
// front_ holds the publication number and not the slot index, which is load-bearing rather than cosmetic. An
// index makes try_get read two atomics that can disagree: it loads the front index, and if the producer laps
// before the stamp load, the frame that comes back is newer than the front the consumer observed. A later call
// may then legally observe an older store to front_ - later in front_'s modification order, so read-read
// coherence permits it - and hand the same consumer a frame it has already moved past, breaking the promise the
// line above makes and E4-S3's render thread builds on (T-118; it was 4 regressions in 200 stress runs). Numbering
// the front makes the returned publication *be* the value loaded from front_, and successive loads of one atomic
// by one thread never move backwards in that atomic's modification order, so neither can the frames a consumer is
// given. Scanning all three stamps and taking the largest does not buy this: the three loads are not one instant,
// so a consumer descheduled after reading two stale slots can read the third mid-write and come away with a
// smaller maximum than its previous scan. Do not put the index back.
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
    // Attempts before try_get gives up. Each failure means the producer moved on while the consumer was reading:
    // either it lapped the copy, or it published once between the front load and the stamp that confirms it.
    static constexpr int k_read_attempts = 4;

    triple_buffer() = default;
    triple_buffer(const triple_buffer&) = delete;
    triple_buffer& operator=(const triple_buffer&) = delete;

    // Producer only. Publishes `value` as the new front.
    void publish(const T& value) noexcept {
        const uint64_t stamp = stamp_ + 1;
        slot& s = slots_[stamp % 3]; // never the front (stamp - 1) nor the one before it, so both stay readable
        // 0 means "being written": a consumer that lands here has nothing whole to take, and would rather be
        // told so than copy a mixture of two frames.
        s.stamp.store(0, std::memory_order_relaxed);
        std::atomic_thread_fence(std::memory_order_release);
        std::memcpy(&s.value, &value, sizeof(T));
        s.stamp.store(stamp, std::memory_order_release);
        front_.store(stamp, std::memory_order_release);
        stamp_ = stamp;
    }

    // Any consumer thread. Returns the publication front_ named when it was read, whole or not at all - so the
    // publications one thread is handed never go backwards. False when nothing has been published, or when the
    // producer moved on k_read_attempts times running (which has not been seen outside a test publishing flat out).
    bool try_get(T& out) const noexcept {
        for (int attempt = 0; attempt < k_read_attempts; ++attempt) {
            const uint64_t want = front_.load(std::memory_order_acquire);
            if (want == 0) {
                return false; // nothing published yet, and front_ only ever goes up, so retrying cannot help
            }
            const slot& s = slots_[want % 3];
            // A stamp that is not the number asked for means the producer has already lapped this slot, or is
            // inside the write that will (0). Either way what is there is not publication `want`; ask again.
            if (s.stamp.load(std::memory_order_acquire) != want) {
                continue;
            }
            std::memcpy(&out, &s.value, sizeof(T));
            std::atomic_thread_fence(std::memory_order_acquire);
            if (s.stamp.load(std::memory_order_relaxed) == want) {
                return true; // `out` is publication `want`, and `want` came from front_, so it never goes back
            }
        }
        return false;
    }

    // The front's publication number, which is how many have been published (0 = nothing yet). Readable from
    // either side; a diagnostic, not a synchronisation point.
    uint64_t published() const noexcept { return front_.load(std::memory_order_acquire); }

private:
    struct slot {
        std::atomic<uint64_t> stamp{0}; // 0 = never written or being written; otherwise the publication number
        T value{};
    };

    slot slots_[3];
    std::atomic<uint64_t> front_{0}; // the newest complete publication's number, 0 until the first publish
    uint64_t stamp_ = 0;             // producer only: the last number it published
};

} // namespace mp
