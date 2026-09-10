#include "audio/mp4_gapless.h"

#include <cstdio>
#include <cstring>
#include <vector>

namespace mp::audio {
namespace {

struct box {
    std::string_view type;
    const uint8_t* payload = nullptr;
    size_t size = 0; // payload bytes
};

uint32_t be32(const uint8_t* p) {
    return (uint32_t{p[0]} << 24) | (uint32_t{p[1]} << 16) | (uint32_t{p[2]} << 8) | uint32_t{p[3]};
}

uint64_t be64(const uint8_t* p) {
    return (uint64_t{be32(p)} << 32) | be32(p + 4);
}

// Calls `visit(box)` for each child box in [data, data + size); stops on a malformed size.
template <typename F> void each_box(const uint8_t* data, size_t size, F&& visit) {
    size_t off = 0;
    while (off + 8 <= size) {
        uint64_t box_size = be32(data + off);
        size_t header = 8;
        if (box_size == 1) {
            if (off + 16 > size) {
                return;
            }
            box_size = be64(data + off + 8);
            header = 16;
        } else if (box_size == 0) {
            box_size = size - off;
        }
        if (box_size < header || box_size > size - off) {
            return;
        }
        visit(box{std::string_view{reinterpret_cast<const char*>(data + off + 4), 4}, data + off + header,
                  static_cast<size_t>(box_size - header)});
        off += static_cast<size_t>(box_size);
    }
}

std::optional<box> find_box(const uint8_t* data, size_t size, std::string_view type) {
    std::optional<box> found;
    each_box(data, size, [&](const box& b) {
        if (!found && b.type == type) {
            found = b;
        }
    });
    return found;
}

// A "full box" (version + flags) payload.
struct full_box {
    uint8_t version = 0;
    const uint8_t* body = nullptr;
    size_t size = 0;
};

std::optional<full_box> full(const box& b) {
    if (b.size < 4) {
        return std::nullopt;
    }
    return full_box{b.payload[0], b.payload + 4, b.size - 4};
}

struct track_info {
    uint32_t timescale = 0;
    uint64_t duration = 0;
    bool audio = false;
    bool has_edit = false;
    int64_t media_time = -1;       // media timescale
    uint64_t segment_duration = 0; // movie timescale
};

std::optional<track_info> read_trak(const box& trak) {
    track_info info;
    const auto mdia = find_box(trak.payload, trak.size, "mdia");
    if (!mdia) {
        return std::nullopt;
    }
    if (const auto hdlr = find_box(mdia->payload, mdia->size, "hdlr")) {
        if (const auto f = full(*hdlr); f && f->size >= 8) {
            info.audio = std::memcmp(f->body + 4, "soun", 4) == 0;
        }
    }
    if (const auto mdhd = find_box(mdia->payload, mdia->size, "mdhd")) {
        if (const auto f = full(*mdhd)) {
            if (f->version == 1 && f->size >= 28) {
                info.timescale = be32(f->body + 16);
                info.duration = be64(f->body + 20);
            } else if (f->version == 0 && f->size >= 16) {
                info.timescale = be32(f->body + 8);
                info.duration = be32(f->body + 12);
            }
        }
    }
    if (const auto edts = find_box(trak.payload, trak.size, "edts")) {
        if (const auto elst = find_box(edts->payload, edts->size, "elst")) {
            if (const auto f = full(*elst); f && f->size >= 4) {
                const uint32_t count = be32(f->body);
                const size_t entry = f->version == 1 ? 20 : 12;
                const uint8_t* p = f->body + 4;
                for (uint32_t i = 0; i < count && (p - f->body) + entry <= f->size; ++i, p += entry) {
                    uint64_t seg;
                    int64_t media;
                    if (f->version == 1) {
                        seg = be64(p);
                        media = static_cast<int64_t>(be64(p + 8));
                    } else {
                        seg = be32(p);
                        media = static_cast<int32_t>(be32(p + 4));
                    }
                    if (media >= 0) { // the first real edit; an empty edit (-1) is a delay, not priming
                        info.has_edit = true;
                        info.media_time = media;
                        info.segment_duration = seg;
                        break;
                    }
                }
            }
        }
    }
    return info;
}

// The iTunSMPB freeform item, wherever the ilst is (moov/udta/meta/ilst or moov/meta/ilst).
std::optional<std::string> find_itunsmpb(const uint8_t* moov, size_t size) {
    std::optional<std::string> text;
    const auto scan_ilst = [&](const box& ilst) {
        each_box(ilst.payload, ilst.size, [&](const box& item) {
            if (text || item.type != "----") {
                return;
            }
            bool named = false;
            each_box(item.payload, item.size, [&](const box& part) {
                if (part.type == "name" && part.size >= 4 + 8 && std::memcmp(part.payload + 4, "iTunSMPB", 8) == 0) {
                    named = true;
                }
            });
            if (!named) {
                return;
            }
            each_box(item.payload, item.size, [&](const box& part) {
                if (part.type == "data" && part.size > 8) {
                    text = std::string{reinterpret_cast<const char*>(part.payload + 8), part.size - 8};
                }
            });
        });
    };
    const auto scan_meta = [&](const box& meta) {
        // meta is a full box: its children start after version/flags.
        if (meta.size > 4) {
            if (const auto ilst = find_box(meta.payload + 4, meta.size - 4, "ilst")) {
                scan_ilst(*ilst);
            }
        }
    };
    if (const auto udta = find_box(moov, size, "udta")) {
        if (const auto meta = find_box(udta->payload, udta->size, "meta")) {
            scan_meta(*meta);
        }
    }
    if (!text) {
        if (const auto meta = find_box(moov, size, "meta")) {
            scan_meta(*meta);
        }
    }
    return text;
}

bool hex_field(std::string_view s, size_t& pos, uint64_t& out) {
    while (pos < s.size() && (s[pos] == ' ' || s[pos] == '\t')) {
        ++pos;
    }
    const size_t start = pos;
    uint64_t v = 0;
    while (pos < s.size()) {
        const char c = s[pos];
        int d;
        if (c >= '0' && c <= '9') {
            d = c - '0';
        } else if (c >= 'a' && c <= 'f') {
            d = c - 'a' + 10;
        } else if (c >= 'A' && c <= 'F') {
            d = c - 'A' + 10;
        } else {
            break;
        }
        v = (v << 4) | static_cast<uint64_t>(d);
        ++pos;
    }
    if (pos == start || pos - start > 16) {
        return false;
    }
    out = v;
    return true;
}

} // namespace

std::optional<mp4_gapless> parse_itunsmpb(std::string_view text) {
    size_t pos = 0;
    uint64_t fields[4];
    for (uint64_t& f : fields) {
        if (!hex_field(text, pos, f)) {
            return std::nullopt;
        }
    }
    mp4_gapless g;
    g.from = mp4_gapless::source::itunsmpb;
    g.priming_frames = fields[1];
    g.valid_frames = fields[3];
    return g;
}

std::optional<mp4_gapless> parse_mp4_gapless(const uint8_t* moov, size_t size) {
    uint32_t movie_timescale = 0;
    if (const auto mvhd = find_box(moov, size, "mvhd")) {
        if (const auto f = full(*mvhd)) {
            if (f->version == 1 && f->size >= 20) {
                movie_timescale = be32(f->body + 16);
            } else if (f->version == 0 && f->size >= 12) {
                movie_timescale = be32(f->body + 8);
            }
        }
    }
    std::optional<track_info> audio;
    each_box(moov, size, [&](const box& b) {
        if (audio || b.type != "trak") {
            return;
        }
        if (auto t = read_trak(b); t && t->audio) {
            audio = t;
        }
    });
    if (!audio || audio->timescale == 0) {
        return std::nullopt;
    }

    std::optional<mp4_gapless> g;
    if (const auto text = find_itunsmpb(moov, size)) {
        g = parse_itunsmpb(*text);
    }
    if (!g && audio->has_edit && movie_timescale != 0) {
        mp4_gapless e;
        e.from = mp4_gapless::source::edit_list;
        e.priming_frames = static_cast<uint64_t>(audio->media_time);
        // The edit's length is in movie units; scale to the track's rate and round.
        e.valid_frames = (audio->segment_duration * audio->timescale + movie_timescale / 2) / movie_timescale;
        g = e;
    }
    if (!g) {
        return std::nullopt;
    }
    g->timescale = audio->timescale;
    if (audio->duration != 0) {
        // Never claim more audio than the track holds.
        const uint64_t available = audio->duration > g->priming_frames ? audio->duration - g->priming_frames : 0;
        if (g->valid_frames == 0 || g->valid_frames > available) {
            g->valid_frames = available;
        }
    }
    if (g->priming_frames == 0 && (audio->duration == 0 || g->valid_frames >= audio->duration)) {
        return std::nullopt; // nothing to trim
    }
    return g;
}

std::optional<mp4_gapless> read_mp4_gapless(const std::wstring& path) {
    FILE* f = nullptr;
    if (_wfopen_s(&f, path.c_str(), L"rb") != 0 || f == nullptr) {
        return std::nullopt;
    }
    std::optional<mp4_gapless> result;
    bool is_mp4 = false;
    uint64_t offset = 0;
    constexpr uint64_t k_max_moov = 64u << 20;
    for (int i = 0; i < 64; ++i) {
        uint8_t header[16];
        if (_fseeki64(f, static_cast<int64_t>(offset), SEEK_SET) != 0 || std::fread(header, 1, 8, f) != 8) {
            break;
        }
        uint64_t box_size = be32(header);
        size_t header_size = 8;
        if (box_size == 1) {
            if (std::fread(header + 8, 1, 8, f) != 8) {
                break;
            }
            box_size = be64(header + 8);
            header_size = 16;
        } else if (box_size == 0) {
            box_size = ~0ull; // to end of file: only meaningful for a trailing mdat
        }
        if (box_size < header_size) {
            break;
        }
        const std::string_view type{reinterpret_cast<const char*>(header + 4), 4};
        if (i == 0 && type != "ftyp") {
            break;
        }
        if (type == "ftyp") {
            is_mp4 = true;
        } else if (type == "moov" && is_mp4) {
            const uint64_t payload = box_size - header_size;
            if (payload > k_max_moov) {
                break;
            }
            std::vector<uint8_t> moov(static_cast<size_t>(payload));
            if (std::fread(moov.data(), 1, moov.size(), f) == moov.size()) {
                result = parse_mp4_gapless(moov.data(), moov.size());
            }
            break;
        }
        if (box_size == ~0ull) {
            break;
        }
        offset += box_size;
    }
    std::fclose(f);
    return result;
}

} // namespace mp::audio
