// The analysis tap (E1-S8): what the audio path hands the analysis thread.
//
// A DSP on the mixer sees every frame the mixer produces, in whatever sized pieces BASS reads it in. The
// analysis thread wants fixed hops (ADR-010: 512 frames, 93.75 Hz at 48 kHz), so the tap accumulates into one
// staging block and publishes a whole hop at a time through an spsc_ring - lock-free, fixed capacity, no
// allocation after construction, which is what makes it legal on the WASAPI output thread (rt_guard asserts it).
//
// The position a block carries is the mixer byte position of its FIRST frame, in the units of
// mp_clock.mixer_byte_pos, so a consumer can line a block up with what the listener is hearing exactly as the
// gapless join events are lined up (mp_clock.mixer_byte_pos minus output_buffered_bytes). It is counted here
// rather than read back from BASS: the tap runs inside the mixer's own BASS_ChannelGetData, where asking the
// mixer where it is would be a question about the read in progress. Counting every byte the DSP is shown gives
// the same number by construction, and reset() puts it back to zero with the mixer it belongs to.
//
// A consumer that falls behind costs hops, not correctness: a full ring makes write() count a drop and carry on.
// The newest hop is the one lost rather than the oldest, because evicting the oldest would mean the producer
// moving the ring's head, which belongs to the consumer alone - that is the whole basis of the ring being
// lock-free. It costs nothing in practice: the ring holds ~170 ms and the analysis thread's budget is under 4 ms
// per 10.7 ms hop, so it drains faster than the tap fills and a stall is made up within a few hops. Dropping is
// the producer's only failure mode; it never blocks and never waits.
#pragma once

#include "common/spsc_ring.h"

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>

namespace mp::analysis {

// One hop of mixed PCM as the tap publishes it.
struct tap_block {
    // Frames per hop, and the widest mixer the tap carries whole (a device with more channels is truncated to
    // this many, which analysis does not miss: the spectrum of a 7.1 mix is not the spectrum of eight of them).
    static constexpr uint32_t k_frames = 512;
    static constexpr uint32_t k_max_channels = 8;

    int64_t mixer_byte_pos = 0; // mixer bytes produced before this block's first frame
    int64_t qpc_ticks = 0;      // QueryPerformanceCounter when the block was completed
    uint32_t frames = 0;        // always k_frames for a published block
    uint32_t channels = 0;      // interleaved channel count in samples
    float samples[static_cast<size_t>(k_frames) * k_max_channels] = {};
};

class tap {
public:
    // Blocks the ring holds: ~170 ms at 48 kHz, enough that an analysis thread descheduled for a whole quantum
    // still finds its work waiting.
    static constexpr size_t k_blocks = 16;

    tap() : ring_(k_blocks) {}

    tap(const tap&) = delete;
    tap& operator=(const tap&) = delete;

    // Control thread, with both the audio thread and the analysis thread stopped (draining the ring here is a
    // consumer's move): a new mixer means a new byte origin and possibly a new channel count, and anything still
    // in the ring belongs to the old one.
    void reset(uint32_t channels) noexcept {
        channels_ = std::min(channels, tap_block::k_max_channels);
        source_channels_ = std::max(1u, channels);
        bytes_ = 0;
        staged_ = 0;
        dropped_.store(0, std::memory_order_relaxed);
        published_.store(0, std::memory_order_relaxed);
        tap_block discard;
        while (ring_.try_pop(discard)) {
        }
    }

    // Audio thread. `interleaved` is `frames` frames of the mixer's own channel count; `qpc_ticks` is now.
    // Real-time: a bounded memcpy per call and, at most once per k_frames frames, one ring push.
    void write(const float* interleaved, uint32_t frames, int64_t qpc_ticks) noexcept {
        if (interleaved == nullptr || channels_ == 0) {
            bytes_ += static_cast<int64_t>(frames) * source_channels_ * static_cast<int64_t>(sizeof(float));
            return;
        }
        uint32_t done = 0;
        while (done < frames) {
            const uint32_t take = std::min(tap_block::k_frames - staged_, frames - done);
            if (staged_ == 0) {
                // The hop starts here: remember where the mixer was before its first frame.
                staging_.mixer_byte_pos =
                    bytes_ + static_cast<int64_t>(done) * source_channels_ * static_cast<int64_t>(sizeof(float));
            }
            copy_frames(interleaved + static_cast<size_t>(done) * source_channels_, take);
            staged_ += take;
            done += take;
            if (staged_ == tap_block::k_frames) {
                staging_.frames = tap_block::k_frames;
                staging_.channels = channels_;
                staging_.qpc_ticks = qpc_ticks;
                publish();
                staged_ = 0;
            }
        }
        bytes_ += static_cast<int64_t>(frames) * source_channels_ * static_cast<int64_t>(sizeof(float));
    }

    // What a DSP is actually handed: a byte count. Frames come from the channel count reset() was given, so the
    // audio thread never has to read a field the control thread owns.
    void write_bytes(const float* interleaved, uint32_t bytes, int64_t qpc_ticks) noexcept {
        write(interleaved, bytes / (source_channels_ * static_cast<uint32_t>(sizeof(float))), qpc_ticks);
    }

    // Analysis thread. False when nothing has been published since the last read.
    bool try_read(tap_block& out) noexcept { return ring_.try_pop(out); }

    // The mixer's channel count as reset() was told it, and the count a block carries (the same unless the mixer
    // has more channels than a block holds).
    uint32_t source_channels() const noexcept { return source_channels_; }
    uint32_t block_channels() const noexcept { return channels_; }

    // Blocks the consumer never saw because it fell behind, and blocks the tap has produced. Both monotonic
    // since the last reset; for diagnostics and the tests.
    uint64_t dropped() const noexcept { return dropped_.load(std::memory_order_relaxed); }
    uint64_t published() const noexcept { return published_.load(std::memory_order_relaxed); }

    // Hops published but not yet read: how far behind the analysis thread is, in hops. Readable from either side
    // (it is a difference of two atomics), so it is a diagnostic, not a synchronisation point.
    size_t pending() const noexcept { return ring_.size(); }

    // The mixer byte position the tap has been shown, which is where the mixer is. Audio thread's own value.
    int64_t byte_position() const noexcept { return bytes_; }

private:
    // Copies `frames` frames, narrowing the mixer's channel count to what a block carries.
    void copy_frames(const float* src, uint32_t frames) noexcept {
        float* dst = staging_.samples + static_cast<size_t>(staged_) * channels_;
        if (channels_ == source_channels_) {
            std::memcpy(dst, src, static_cast<size_t>(frames) * channels_ * sizeof(float));
            return;
        }
        for (uint32_t f = 0; f < frames; ++f) {
            std::memcpy(dst + static_cast<size_t>(f) * channels_, src + static_cast<size_t>(f) * source_channels_,
                        static_cast<size_t>(channels_) * sizeof(float));
        }
    }

    void publish() noexcept {
        if (!ring_.try_push(staging_)) {
            dropped_.fetch_add(1, std::memory_order_relaxed); // the consumer is behind; this hop is gone
            return;
        }
        published_.fetch_add(1, std::memory_order_relaxed);
    }

    mp::spsc_ring<tap_block> ring_;
    tap_block staging_{};   // audio thread only
    uint32_t staged_ = 0;   // frames already in staging_
    uint32_t channels_ = 0; // channels a block carries (0 until reset)
    uint32_t source_channels_ = 1;
    int64_t bytes_ = 0;
    std::atomic<uint64_t> dropped_{0};
    std::atomic<uint64_t> published_{0};
};

} // namespace mp::analysis
