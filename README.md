# Tunqio

A Windows desktop music player for people who own their music: gapless bit-perfect playback, a fast local
library, and a native visualizer whose visuals react within one display refresh of the audio.

The design lives in [docs/](docs/README.md); start with [decisions.md](docs/decisions.md) and
[product-scope.md](docs/product-scope.md). Work is planned on the kanban board (`kanban next`).

## Layout

| Path | What |
|------|------|
| `native/mpcore/` | C++20 core DLL: audio (BASS), analysis, rendering behind the C ABI in `include/mpcore.h` |
| `native/mpcore.tests/` | Catch2 tests for the core (Debug, Release and ASan configurations) |
| `native/third_party/` | Vendored pffft, nlohmann/json, Catch2 (see `THIRD-PARTY-NOTICES.md`) |
| `src/Tunqio.Core` | Contracts, PlayQueue, PlaybackSession. Plain `net8.0`, no Windows |
| `src/Tunqio.Interop` | `LibraryImport` bindings over `mpcore.h`; the only project that names the DLL |
| `src/Tunqio.Library` | SQLite library, scanner, tags, art cache |
| `src/Tunqio.App` | WinUI 3 shell, MSIX manifest |
| `tests/` | xUnit projects per layer |
| `tools/` | Build and verification scripts |

## Build

Requirements: Visual Studio 2026 with "Desktop development with C++" (MSVC v145, Windows SDK 10.0.26100),
.NET SDK 8.0.4xx (pinned in `global.json`). The Windows App SDK comes from NuGet.

```bash
msbuild Tunqio.sln -restore -p:Configuration=Release -p:Platform=x64
```

`dotnet build` does not build the C++ projects; drive the solution with `msbuild` and use `dotnet test`
afterwards:

```bash
artifacts/native/Release/x64/mpcore.tests.exe
dotnet test Tunqio.Managed.slnf -c Release -p:Platform=x64 --no-build
```

If your Visual Studio has only the C++ workload, `MSBuild.exe` cannot resolve the .NET SDK and the solution
build fails with NU1503. `tools/build.ps1 [-Configuration Release] [-Test]` then builds the native projects
with MSBuild.exe and the managed ones with `dotnet build Tunqio.Managed.slnf`, and runs every test suite
including the ASan proof (`tools/check-asan.ps1`).

`Debug` builds the app unpackaged (`WindowsPackageType=None`) for a fast F5 with mixed-mode debugging;
`Release` builds it as a single-project MSIX. Override with `-p:TunqioPackaged=true|false`.

All outputs land under `artifacts/`.

## Conventions

- Branch per kanban card: `T-<n>-<slug>`; `T-<n>` in commit subjects.
- C++: `/W4 /WX`, `clang-format` (`tools/check-format.ps1 -Fix`). C#: warnings as errors, `dotnet format`.
- Nothing on the audio path allocates or blocks after startup.
