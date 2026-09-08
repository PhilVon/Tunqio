# Third-party notices

Components shipped in or used to build Tunqio, with their licences. NuGet packages are listed in
`Directory.Packages.props`; their licence texts are collected by the release pipeline (E8-S1).

## BASS audio library (proprietary, free for non-commercial use)

Audio playback uses the BASS audio library and its add-ons (BASSmix, BASSWASAPI, BASSFLAC, BASSOPUS, BASSWV, BASS_APE) by Un4seen Developments, www.un4seen.com, under the free non-commercial licence.

Tunqio is a non-commercial product (decision Q-1 on card T-1), so BASS and its add-ons are used under the
free non-commercial licence stated in each package's text file. The About page shows the attribution above
and the licence texts, which `tools/fetch-native.ps1` extracts into `native/bass/licenses/` and the build
copies next to the executable under `licenses/`. Should distribution ever become commercial, ADR-003 has to be
revisited and a BASS licence purchased before release.

| Package | Version | Source | Licence | Licence text |
|---------|---------|--------|---------|--------------|
| `bass` | 2.4 | https://www.un4seen.com/files/bass24.zip | Free for non-commercial use (Un4seen Developments Ltd.) | `licenses/bass.txt` |
| `bassmix` | 2.4 | https://www.un4seen.com/files/bassmix24.zip | Free to use with BASS | `licenses/bassmix.txt` |
| `basswasapi` | 2.4 | https://www.un4seen.com/files/basswasapi24.zip | Free to use with BASS | `licenses/basswasapi.txt` |
| `bassflac` | 2.4 | https://www.un4seen.com/files/bassflac24.zip | Free to use with BASS; FLAC decoding based on libFLAC (BSD) | `licenses/bassflac.txt` |
| `bassopus` | 2.4 | https://www.un4seen.com/files/bassopus24.zip | Free to use with BASS; Opus decoding based on libopus (BSD) | `licenses/bassopus.txt` |
| `basswv` | 2.4 | https://www.un4seen.com/files/basswv24.zip | Free to use with BASS; WavPack decoding based on libwavpack (BSD) | `licenses/basswv.txt` |
| `bass_ape` | 2.4 | https://www.un4seen.com/files/z/2/bass_ape24.zip | Third-party add-on by Sebastian Andersson; Monkey's Audio SDK by Matthew T. Ashland | `licenses/bass_ape.txt` |

The exact SHA-256 of every package is pinned in `tools/native-deps.json`; the fetch script refuses a
package whose hash differs. Binaries are not committed (`native/bass/` is gitignored).

**BASS_AAC is not used.** The Windows BASS_AAC add-on ships under the GPL (its archive carries `gpl.txt`),
which the build's licence policy excludes. AAC, M4A, ALAC and WMA decode through BASS's built-in Media
Foundation codec support on Windows 10 and 11 instead, so no add-on is needed for them.

## Vendored native sources (`native/third_party/`)

| Component | Version | Source | Licence | Files | SHA-256 |
|-----------|---------|--------|---------|-------|---------|
| Catch2 | v3.16.0 | https://github.com/catchorg/Catch2 (`extras/catch_amalgamated.*`) | Boost Software License 1.0 (`catch2/LICENSE.txt`) | `catch2/catch_amalgamated.hpp` | `d4cc143ea76ae212204363922d8adf376d66a1fda5a33ac73f93a7d1c119f4e0` |
| | | | | `catch2/catch_amalgamated.cpp` | `1fe7f10334e0ae5494419cfa84c270f15235eada0fc02bdb493d1def537601f3` |
| nlohmann/json | v3.12.0 | https://github.com/nlohmann/json (`single_include/nlohmann/json.hpp`) | MIT (`nlohmann/LICENSE.MIT`) | `nlohmann/json.hpp` | `aaf127c04cb31c406e5b04a63f1ae89369fccde6d8fa7cdda1ed4f32dfc5de63` |
| pffft | commit `0aec0327a6912e1a0ec5326eef737c2ce19bc836` (2026-08-14) | https://bitbucket.org/jpommier/pffft | FFTPACK licence (BSD-style; text at the top of `pffft.h`) | `pffft/pffft.h` | `d6ac7f26f7c3f87ed2ad7f0264c09d72285526d937b4dccc5fc1c97645a0d55d` |
| | | | | `pffft/pffft.c` | `485f2c641b9bc9434720757307e825c2f694b682438da9052959ab1e445e1f16` |

Vendoring was chosen over a vcpkg manifest (E0-S1 "decide and record"): three small, stable dependencies,
no bootstrap step on CI or a fresh clone, and the exact bytes are hash-pinned here. Update by replacing the
files and the hashes in the same commit.

Catch2 is a test-only dependency and is not part of the shipped package.
