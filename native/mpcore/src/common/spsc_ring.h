// Single-producer / single-consumer lock-free ring of trivially copyable items.
//
// The audio tap (producer, WASAPI thread) hands PCM blocks to the analysis thread (consumer) through
// one of these (docs/audio-engine.md, docs/architecture-overview.md ADR-010). Rules that make it
// real-time safe: fixed capacity chosen at construction, no allocation after construction, no locks,
// no blocking. try_push/try_pop return false instead of waiting.
//
// Correctness argument: head_ is written only by the consumer, tail_ only by the producer. The
// producer publishes an item with a release store of tail_ after the copy; the consumer acquires
// tail_ before reading, so it sees the completed copy. Symmetrically for head_. Indices are
// monotonically increasing 64-bit counters masked into the buffer, so full/empty are unambiguous.
#pragma once

#include <atomic>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <new>
#include <type_traits>

namespace mp {

// C4324: "structure was padded due to alignment specifier" - the padding is the point (one cache line per index).
#pragma warning(push)
#pragma warning(disable : 4324)

template <typename T> class spsc_ring {
    static_assert(std::is_trivially_copyable_v<T>, "spsc_ring items are memcpy'd across threads");

public:
    // capacity is rounded up to a power of two (minimum 2).
    explicit spsc_ring(size_t capacity)
        : capacity_(std::bit_ceil(capacity < 2 ? size_t{2} : capacity)), mask_(capacity_ - 1),
          slots_(std::make_unique<T[]>(capacity_)) {}

    spsc_ring(const spsc_ring&) = delete;
    spsc_ring& operator=(const spsc_ring&) = delete;

    size_t capacity() const noexcept { return capacity_; }

    // Producer only.
    bool try_push(const T& item) noexcept {
        const uint64_t tail = tail_.load(std::memory_order_relaxed);
        const uint64_t head = head_.load(std::memory_order_acquire);
        if (tail - head == capacity_) {
            return false; // full
        }
        slots_[tail & mask_] = item;
        tail_.store(tail + 1, std::memory_order_release);
        return true;
    }

    // Consumer only.
    bool try_pop(T& out) noexcept {
        const uint64_t head = head_.load(std::memory_order_relaxed);
        const uint64_t tail = tail_.load(std::memory_order_acquire);
        if (head == tail) {
            return false; // empty
        }
        out = slots_[head & mask_];
        head_.store(head + 1, std::memory_order_release);
        return true;
    }

    // Approximate from either side; exact from the calling side's point of view.
    size_t size() const noexcept {
        const uint64_t tail = tail_.load(std::memory_order_acquire);
        const uint64_t head = head_.load(std::memory_order_acquire);
        return static_cast<size_t>(tail - head);
    }
    bool empty() const noexcept { return size() == 0; }

private:
    static constexpr size_t k_cache_line = std::hardware_destructive_interference_size;

    const size_t capacity_;
    const size_t mask_;
    std::unique_ptr<T[]> slots_;
    alignas(k_cache_line) std::atomic<uint64_t> head_{0};
    alignas(k_cache_line) std::atomic<uint64_t> tail_{0};
};

#pragma warning(pop)

} // namespace mp
