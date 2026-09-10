// The MP4 gapless reader (T-102): the edit list ffmpeg writes, the iTunSMPB text iTunes writes, and the files
// that carry neither.
#include "audio/mp4_gapless.h"
#include "source_root.h"

#include <catch2/catch_amalgamated.hpp>
#include <cstring>
#include <filesystem>
#include <string>
#include <vector>

namespace {

namespace fs = std::filesystem;
using mp::audio::mp4_gapless;

std::filesystem::path gapless_fixture(const char* pair, const char* file) {
    const auto native = mp::tests::find_native_root();
    return native ? native->parent_path() / "tests" / "fixtures" / "gapless" / pair / file : fs::path{};
}

void put32(std::vector<uint8_t>& v, uint32_t x) {
    v.push_back(static_cast<uint8_t>(x >> 24));
    v.push_back(static_cast<uint8_t>(x >> 16));
    v.push_back(static_cast<uint8_t>(x >> 8));
    v.push_back(static_cast<uint8_t>(x));
}

std::vector<uint8_t> atom(const char* type, const std::vector<uint8_t>& payload) {
    std::vector<uint8_t> v;
    put32(v, static_cast<uint32_t>(8 + payload.size()));
    v.insert(v.end(), type, type + 4);
    v.insert(v.end(), payload.begin(), payload.end());
    return v;
}

std::vector<uint8_t> cat(std::initializer_list<std::vector<uint8_t>> parts) {
    std::vector<uint8_t> v;
    for (const auto& p : parts) {
        v.insert(v.end(), p.begin(), p.end());
    }
    return v;
}

// A minimal moov: mvhd (timescale 1000), one sound trak with mdhd (48 kHz, `duration` frames) and optional
// elst / iTunSMPB.
std::vector<uint8_t> moov(uint32_t duration, std::optional<std::pair<uint32_t, uint32_t>> elst_ms_and_media_time,
                          const char* itunsmpb) {
    std::vector<uint8_t> mvhd(4, 0); // version 0, flags
    put32(mvhd, 0);                  // creation
    put32(mvhd, 0);                  // modification
    put32(mvhd, 1000);               // timescale
    put32(mvhd, 0);                  // duration
    mvhd.resize(mvhd.size() + 80, 0);

    std::vector<uint8_t> mdhd(4, 0);
    put32(mdhd, 0);
    put32(mdhd, 0);
    put32(mdhd, 48000);
    put32(mdhd, duration);
    put32(mdhd, 0);

    std::vector<uint8_t> hdlr(4, 0);
    put32(hdlr, 0);
    hdlr.insert(hdlr.end(), {'s', 'o', 'u', 'n'});
    hdlr.resize(hdlr.size() + 13, 0);

    std::vector<uint8_t> trak_parts = atom("mdia", cat({atom("mdhd", mdhd), atom("hdlr", hdlr)}));
    if (elst_ms_and_media_time) {
        std::vector<uint8_t> elst(4, 0);
        put32(elst, 1);
        put32(elst, elst_ms_and_media_time->first);
        put32(elst, elst_ms_and_media_time->second);
        put32(elst, 0x00010000);
        trak_parts = cat({atom("edts", atom("elst", elst)), trak_parts});
    }
    std::vector<uint8_t> out = cat({atom("mvhd", mvhd), atom("trak", trak_parts)});
    if (itunsmpb != nullptr) {
        std::vector<uint8_t> mean(4, 0);
        const char* m = "com.apple.iTunes";
        mean.insert(mean.end(), m, m + std::strlen(m));
        std::vector<uint8_t> name(4, 0);
        name.insert(name.end(), {'i', 'T', 'u', 'n', 'S', 'M', 'P', 'B'});
        std::vector<uint8_t> data(8, 0);
        data[3] = 1; // text
        data.insert(data.end(), itunsmpb, itunsmpb + std::strlen(itunsmpb));
        std::vector<uint8_t> meta(4, 0);
        meta =
            cat({meta, atom("ilst", atom("----", cat({atom("mean", mean), atom("name", name), atom("data", data)})))});
        out = cat({out, atom("udta", atom("meta", meta))});
    }
    return out;
}

} // namespace

TEST_CASE("iTunSMPB text parses into priming and valid frames", "[gapless][mp4]") {
    const auto g = mp::audio::parse_itunsmpb(" 00000000 00000840 000001C0 00000000000E6A00 00000000 00000000");
    REQUIRE(g.has_value());
    CHECK(g->from == mp4_gapless::source::itunsmpb);
    CHECK(g->priming_frames == 2112);
    CHECK(g->valid_frames == 944640);
    CHECK_FALSE(mp::audio::parse_itunsmpb("not a tag").has_value());
    CHECK_FALSE(mp::audio::parse_itunsmpb("00000000 00000840").has_value());
}

TEST_CASE("the edit list gives the priming and the valid length in track frames", "[gapless][mp4]") {
    const auto m = moov(97024, std::pair{2000u, 1024u}, nullptr);
    const auto g = mp::audio::parse_mp4_gapless(m.data(), m.size());
    REQUIRE(g.has_value());
    CHECK(g->from == mp4_gapless::source::edit_list);
    CHECK(g->priming_frames == 1024);
    CHECK(g->valid_frames == 96000);
    CHECK(g->timescale == 48000);
}

TEST_CASE("iTunSMPB wins over the edit list and is clamped to what the track holds", "[gapless][mp4]") {
    const auto m = moov(97024, std::pair{2000u, 1024u}, " 00000000 00000840 000001C0 00000000000E6A00");
    const auto g = mp::audio::parse_mp4_gapless(m.data(), m.size());
    REQUIRE(g.has_value());
    CHECK(g->from == mp4_gapless::source::itunsmpb);
    CHECK(g->priming_frames == 2112);
    CHECK(g->valid_frames == 97024 - 2112); // the tag claims more than the 97 024-frame track has
}

TEST_CASE("a file with nothing to trim reports no description", "[gapless][mp4]") {
    const auto none = moov(96000, std::nullopt, nullptr);
    CHECK_FALSE(mp::audio::parse_mp4_gapless(none.data(), none.size()).has_value());
    const auto whole = moov(96000, std::pair{2000u, 0u}, nullptr); // ALAC: media time 0, the whole track
    CHECK_FALSE(mp::audio::parse_mp4_gapless(whole.data(), whole.size()).has_value());
    const auto padding_only = moov(97000, std::pair{2000u, 0u}, nullptr);
    const auto g = mp::audio::parse_mp4_gapless(padding_only.data(), padding_only.size());
    REQUIRE(g.has_value());
    CHECK(g->priming_frames == 0);
    CHECK(g->valid_frames == 96000);
}

TEST_CASE("the fixture files read from disk: AAC has 1024 priming frames, ALAC nothing, a WAV is not an MP4",
          "[gapless][mp4]") {
    const fs::path aac = gapless_fixture("m4a", "a.m4a");
    if (aac.empty() || !fs::exists(aac)) {
        SKIP("tests/fixtures/gapless not found (run detached from the repository)");
    }
    const auto g = mp::audio::read_mp4_gapless(aac.wstring());
    REQUIRE(g.has_value());
    CHECK(g->from == mp4_gapless::source::edit_list);
    CHECK(g->priming_frames == 1024);
    CHECK(g->valid_frames == 96000);
    CHECK(g->timescale == 48000);
    CHECK_FALSE(mp::audio::read_mp4_gapless(gapless_fixture("alac", "a.m4a").wstring()).has_value());
    CHECK_FALSE(mp::audio::read_mp4_gapless(gapless_fixture("wav", "a.wav").wstring()).has_value());
    CHECK_FALSE(mp::audio::read_mp4_gapless(L"C:\\does\\not\\exist.m4a").has_value());
}
