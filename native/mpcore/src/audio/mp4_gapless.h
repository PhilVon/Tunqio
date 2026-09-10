// The gapless description of an MP4 (AAC, ALAC) file: how many decoded frames are encoder priming and how many
// are the audio. Two places carry it: the iTunSMPB freeform atom iTunes, qaac and Nero write, and the edit list
// (elst) ffmpeg and most muxers write. Media Foundation applies neither (docs/spikes/e1-s2-gapless-join.md), so
// the engine trims the decoder's output itself (bass_engine.cpp, the trimming wrapper stream). No BASS here.
#pragma once

#include <cstddef>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace mp::audio {

struct mp4_gapless {
    enum class source { itunsmpb, edit_list };
    source from = source::edit_list;
    uint64_t priming_frames = 0; // decoded frames to drop at the start
    uint64_t valid_frames = 0;   // frames to keep after them (the tail past that is padding)
    uint32_t timescale = 0;      // the audio track's media timescale (its sample rate)
};

// The text of an iTunSMPB atom: " 00000000 00000840 000001C0 00000000000E6A00 ..." (hex fields: reserved,
// priming, padding, valid samples, ...). nullopt when it does not parse.
std::optional<mp4_gapless> parse_itunsmpb(std::string_view text);

// Walks a complete moov atom (the bytes after its own header). iTunSMPB wins over the edit list. nullopt when
// there is no audio track, no description, or the description trims nothing (priming 0 and the whole duration
// valid), in which case the file plays untrimmed.
std::optional<mp4_gapless> parse_mp4_gapless(const uint8_t* moov, size_t size);

// Reads the file's moov atom (skipping mdat without reading it) and parses it. nullopt for anything that is not
// an MP4 or cannot be read.
std::optional<mp4_gapless> read_mp4_gapless(const std::wstring& path);

} // namespace mp::audio
