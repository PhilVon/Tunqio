# Third-party notices

Components shipped in or used to build Tunqio, with their licences. NuGet packages are listed in
`Directory.Packages.props`; their licence texts are collected by the release pipeline (E8-S1). The BASS
attribution and licence text are added by E0-S3 together with `tools/fetch-native.ps1`.

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
