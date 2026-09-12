# Build, Test and Release

How the code is built, what "tested" means for each layer, how performance claims are verified, and how a release is produced. Decisions: ADR-004 (native core), ADR-011 (MSIX).

## Toolchain

| Item | Version / choice |
|------|------------------|
| .NET SDK | 10.0.301 (`rollForward: latestFeature`), pinned in `global.json`. C# 14 (`LangVersion` in `Directory.Build.props`), which is what `[ObservableProperty]` on partial properties needs: the toolkit generates `field`-keyword accessors for them, and .NET 8's Roslyn compiles neither. The target framework stays `net8.0-windows` |
| Analysers | `AnalysisLevel` pinned to `8.0-recommended` in `Directory.Build.props` rather than `latest-recommended`, so the rule set is a decision rather than a side effect of whichever SDK is installed. Raising it is its own change |
| C++ | MSVC v145 (Visual Studio 2026; 14.51 at scaffold time), C++20, Windows SDK 10.0.26100. Pins: `TunqioPlatformToolset` and `TunqioWindowsSdkVersion` in `Directory.Build.props`. Workloads: "Desktop development with C++", ".NET desktop development", "WinUI application development" (the last two give MSBuild.exe its .NET SDK resolver) |
| Build driver | `msbuild Tunqio.sln -restore -p:Configuration=Release -p:Platform=x64` (mixed `.vcxproj` + `.csproj`; what CI runs). Without the .NET workloads in Visual Studio, `tools/build.ps1` drives the C++ projects through MSBuild.exe and the C# projects through `dotnet build Tunqio.Managed.slnf` (a solution filter of the `.csproj`s). `dotnet build` never builds the C++ projects |
| Windows App SDK | 1.8.260804001, pinned once in `Directory.Packages.props` (central package management) |
| Native deps | BASS 2.4.17+, bassmix, basswasapi, format add-ons: fetched by `tools/fetch-native.ps1` into `native/bass/` (headers, `.lib`, `.dll`), SHA-256 pinned, not committed. pffft, nlohmann/json, Catch2: **vendored** under `native/third_party/` (decided in E0-S1 over a vcpkg manifest: three small stable files, no bootstrap step; versions and hashes in `THIRD-PARTY-NOTICES.md`) |
| Shaders | HLSL compiled at runtime with `D3DCompile`; CI also compiles every preset with `dxc` as validation |
| Formatting | `dotnet format` and `clang-format --dry-run --Werror` enforced in CI |

## Build configurations

- **Debug (unpackaged):** `WindowsPackageType=None`; `mpcore.dll` and BASS copied next to the exe; fast F5 with mixed-mode debugging enabled (native + managed) in the App project.
- **Debug (packaged):** MSIX deploy for activation, SMTC, toasts and file associations.
- **Release:** unpackaged by default, so the solution build CI runs the managed tests against carries no Windows App SDK deployment auto-initializer (the packaged assembly's one throws `Class not registered` inside the unpackaged xunit host; T-101). Packaged, self-contained, `ReadyToRun` on, trimming off when a package is generated (`GenerateAppxPackageOnBuild=true`, or `-p:TunqioPackaged=true`), which is what the CI package step and the release build pass; `mpcore` at `/O2 /GL /LTCG` with PDBs archived per release.
- **ASan:** a fourth configuration for `mpcore.tests` only (`/fsanitize=address`, unoptimised, release dynamic CRT `/MD` with the ASan runtime DLL copied next to the binary; the debug CRT's heap fills hide use-after-free from ASan, and `/sdl` rewrites a deleted pointer to `0x8123`, so the proof test reads through an alias), run in CI. The tests compile the `mpcore` sources in directly (`MP_STATIC`) so internals are testable and fully instrumented. `tools/check-asan.ps1` proves detection with a tagged use-after-free test.

## Test strategy

| Layer | Kind | Tooling | What is asserted |
|-------|------|---------|------------------|
| mpcore/common | Unit + stress | Catch2 v3 | Ring buffer never loses, duplicates or tears frames under a producer/consumer stress test; SPSC triple buffer front is always the newest complete frame, and never hands one consumer a frame older than one it has already been given (`front_` carries the publication number, not the slot index; T-118) |
| mpcore/audio | Integration | Catch2 against the BASS "no sound" device with decode-to-buffer | Gapless join is sample-continuous (`[gapless]`: chirp pairs in `tests/fixtures/gapless`, lag 0 against the regenerated chirp and the exact join frame in the event, per-format residual bound); seek within one frame; guard fade click-free; crossfade equal-power; ReplayGain within 0.1 dB; every fixture format opens with correct duration; device enumeration; handle counts stable over 1000 open/close cycles |
| mpcore/analysis | Unit + property | Catch2 | Synthetic signals: 1 kHz sine centroid within 2%; noise vs. sine harmonic ratio separated by > 0.4; click-train onsets within 10 ms, no false positives on sustained tones; pffft output matches a naive DFT reference to 1e-4 |
| mpcore/render | Golden image | Catch2 on WARP, readback compare | `native/mpcore.tests/src/test_preset_golden.cpp`, tag `[golden]`. Every preset the repo ships compiles and renders a fixed `mp_analysis_frame` to within tolerance of a checked-in PNG under `native/mpcore.tests/fixtures/golden`; a luminance-flash measurement (tag `[a11y]`) holds the < 3 Hz full-field change the accessibility contract states. The input is fixed by `renderer::set_analysis_override` and read back whole by `renderer::capture_frame`, both diagnostics off the ABI, and both presets ignore the clock while something is playing — so the image is a function of (preset, parameters, frame, size) and nothing else. 640×360 on WARP; the goldens are byte-identical in Debug, Release and ASan. Tolerance is 6 of 255 on any channel and a mean of 0.5, sized against measured perturbations rather than guessed: a 1% bar-height change moves a channel by 246, a 2.4% change to one colour ramp stop by 9, and a change too small to move a pixel centre by 0 (see the comment on `k_golden_width`). Re-record with `MPCORE_GOLDEN_UPDATE=1`, and read the diff. E4-S3 built the first half of this (`renderer::capture_pixel` on solid-colour preset fixtures under `native/mpcore.tests/fixtures/presets`), which is what a *loader* needs to prove: which preset is on the target, and that a preset that will not compile does not replace it |
| mpcore/abi | Contract | Catch2 | Every export rejects null and wrong `struct_size`; `mp_last_error` populated; no callback after destroy (stress with 1000 create/destroy cycles) |
| Interop | Integration | xUnit against the real `mpcore` | Every binding round-trips; callbacks arrive on the channel; `SafeHandle` finalisation frees native handles; ABI version mismatch is refused |
| Core | Unit | xUnit, FluentAssertions | `PlayQueue` semantics; `PlaybackSession` state machine via a fake engine; settings serialisation |
| Library | Integration | xUnit, `:memory:` SQLite, fixture library | Scanner add/update/missing; incremental rescan touches only changed files; artist splitting; compilation detection; FTS; keyset paging completeness; migrations from every prior version |
| App | View-model unit | xUnit with fakes | Command enablement; search debounce and cancellation; mode switching |
| App | UI smoke (tagged `[UI]`, nightly) | WinAppDriver | Launch, add fixture folder, play first album, mode switch, quit |
| Architecture | Unit | NetArchTest + a Catch2 include-grep test | Dependency rules from solution-structure.md |
| Accessibility | Nightly | Axe.Windows CLI | No critical violations per screen in light/dark/high-contrast |

Coverage target: 80% line coverage on `mpcore` (via `OpenCppCoverage`), Core, Interop and Library; none on XAML.

### Performance verification

| Claim | Harness | Runs |
|-------|---------|------|
| No allocation on audio/analysis path | Debug `RT_ASSERT_NO_ALLOC` hook in Catch2 tests; Release `mp_engine_stats.callback_max_us` | PR gate |
| Feature extraction < 4 ms per hop | Catch2 benchmark (`BENCHMARK`) with threshold | PR gate (20% slack on CI hardware) |
| Interop call overhead < 5 µs for clock and frame reads | BenchmarkDotNet | PR gate |
| Search < 50 ms p95 on 100k | BenchmarkDotNet against `library-100k.db` | PR gate |
| Library open < 100 ms | BenchmarkDotNet against `library-100k.db` | PR gate |
| Scan 10k files < 90 s | `FixtureGen` + timed scan | Nightly |
| 60 fps at 1080p on iGPU | `mp_render_stats` during UI smoke; `LatencyHarness` frame-time distribution | Nightly on reference machine |
| Visual latency p95 ≤ 1 refresh | `tools/LatencyHarness` (ADR-012) | Nightly on reference machine |
| 24 h no dropouts | `tools/SoakRunner` logs `mp_engine_stats.underruns` | Weekly on reference machine |
| Cold start < 1.5 s | WPA trace, `OnLaunched → first Present` | Nightly |
| Memory 200 MB / 500 MB | `dotnet-counters` + native heap via `mp_engine_stats` | Nightly |

The reference machine is a self-hosted GitHub runner; results are posted as a check with trends kept in the wiki.

**A latency budget is asserted where the measurement owns the machine.** `dotnet test Tunqio.Managed.slnf` runs
four test projects at once, each parallelising over the same eight cores, and a p95 taken there is the
scheduler's tail rather than the query's: the search gate measured p95 15–22 ms alone and 36–58 ms during a
full run of the suite, on identical code, failing about half the time (2026-09-10). Percentiles above the
median therefore belong to the BenchmarkDotNet gate, which runs alone as its own CI step:

```
dotnet run -c Release -p:Platform=x64 --project tests/Tunqio.Benchmarks -- --gate --filter '*SearchBenchmarks*'
```

It samples one invocation per iteration, so the percentile is of a single call rather than of a batch mean;
it keeps the outliers, because discarding them would discard the claim; and it exits non-zero when a
`[Budget]` is missed. A timing assertion that stays in `dotnet test` holds the claim's budget against a
statistic contention cannot move (`Library.Tests` asserts the search *median* against the same 50 ms), so a
regression still fails the ordinary run.

**A budget in the source is the claim; `TUNQIO_PERF_SLACK` is how a weaker machine checks it.** The numbers on
the `[Budget]` attributes are measured on the reference machine. A shared GitHub runner is not that machine and
does not hold still between runs: the same `UpsertBenchmarks.UpsertBatchAsync` on identical code came back with
medians of 50.5, 63.7 and 173.8 ms across three `windows-2025-vs2026` runs, a 3.4× spread that is the disk
rather than the code (`docs/spikes/wal-checkpoint-during-scan.md`). Widening the attribute to survive that
would weaken the claim everywhere it is read, so the slack lives with the weaker machine instead: `ci.yml` sets
`TUNQIO_PERF_SLACK: '4'` on the benchmark step and nothing else sets it, so a local `--gate` still checks the
real budgets. The gate prints both numbers whenever a slack is in force — `p95 120.4 ms against 600 ms (a
budget of 150 ms ×4)` — so a p95 that has crept past the claim itself is readable off a green CI run. A value
that is not a positive number, or is above 10×, fails the step rather than falling back to 1: every way of
getting it wrong would otherwise read as green.

## Continuous integration

GitHub Actions, `windows-2025-vs2026` runners (Visual Studio 2026 with MSVC v145, Windows SDK 10.0.26100 and LLVM, matching the local pins).

**On pull request** (`ci.yml`, target < 15 minutes):
1. Checkout; cache NuGet and `native/bass` (by hash).
2. `tools/fetch-native.ps1`.
3. `dotnet format --verify-no-changes`; `clang-format --dry-run --Werror` on `native/`.
4. `msbuild Tunqio.sln -p:Configuration=Release -p:Platform=x64 -warnaserror` (also builds `mpcore.tests`), then `tools/check-presets.ps1` and `tools/check-package.ps1` over the built app.
5. Run `mpcore.tests` (Release) and `mpcore.tests` (ASan); upload Catch2 JUnit output.
6. `dotnet test` for Core, Interop, Library, App view-model tests with Coverlet; `OpenCppCoverage` for native.
7. Gated benchmarks: `Tunqio.Benchmarks --gate` fails the build when a `[Budget]` is missed (E3-S9's search p95 today; E3-S13 adds library open, and Catch2's gated benchmarks join with their own stories).
8. `dxc` validation of every preset shader.
9. Build unpackaged Debug and packaged Release MSIX (self-signed CI cert) as artifacts, with `mpcore.pdb`, then run `tools/check-package.ps1` and `tools/check-presets.ps1` over the `.msix` itself before it is uploaded.

**Nightly** (`nightly.yml`): everything above plus UI smoke, accessibility scan, perf harnesses on the self-hosted runner, and a native-version check that opens an issue when un4seen publishes a new BASS build.

**Release** (`release.yml`, on tag `v*`): build, test, sign, produce `Tunqio_<ver>_x64.msix`, `.appinstaller` manifest, SBOM (CycloneDX for NuGet plus a hand-maintained native list), release notes from conventional commits, GitHub Release with assets and symbol archive.

## Packaging and distribution

- Single-project MSIX. `Package.appxmanifest` declares file type associations for every supported extension, the protocol, `runFullTrust`, and an `appExecutionAlias`. `mpcore.dll`, BASS and add-ons are package content.
- **The engine, BASS and the licence texts are placed by the build, and checked (T-128).** They say "package content" above, and for the whole of development they were not: `TunqioCopyNativeCore` and `TunqioCopyLicenses` were `AfterTargets="Build"` `<Copy>` targets into `$(OutDir)`, the appx layout is computed from item groups, and so every MSIX built before T-128 contained the managed assemblies, the assets and nothing else — no `mpcore.dll`, no BASS, no `licenses/`. A packaged Tunqio had no audio engine, and shipped none of the attribution ADR-003 makes a condition of using BASS. The unpackaged output was correct throughout, which is why nobody hit it. The fix is one `TunqioNativePayload` target in `Tunqio.App.csproj` that adds these as `Content` items `BeforeTargets="AssignTargetPaths"` — the same item-group mechanism T-125 used for presets, populated at execution time because `mpcore.dll` does not exist at evaluation time. One declaration now feeds the copy-to-output pass and the appx payload alike, so the two shapes cannot drift apart again; a packaged build with no native output is an error rather than a warning. `tools/check-package.ps1` asserts `mpcore.dll`, one `<name>.dll` and one `licenses/<name>.txt` per package in `tools/native-deps.json`, and `licenses/THIRD-PARTY-NOTICES.md`, over the built app and over the `.msix` read as the zip it is. Expectations come from the pinned manifest rather than from `native/bass/`, which is gitignored and would make the check expect nothing on a machine that has not fetched.
- **The preset root is placed by the build, and checked (T-125).** The core scans `presets/` beside `mpcore.dll` (`MPCORE_PRESET_ROOT` overrides it), and until T-125 nothing put such a directory in any build output: the app fell back to the preset compiled into the core and said nothing about why the list was short. The repo's `presets/` is committed source and reaches the output as `Content` items with `LinkBase` in `Tunqio.App.csproj` — an item group, not another `AfterTargets` copy, because the MSIX payload is computed from item groups and a file copied into `$(OutDir)` after the build never reaches it. `tools/check-presets.ps1` asserts the shape of the artifact on disk (`presets/` beside `mpcore.dll`, holding every preset the repo's `presets/` declares) and **never reads `MPCORE_PRESET_ROOT`**: a developer with that variable set sees every preset whatever the build did, which is how the defect stayed invisible, and a check that consulted it would pass for the same reason. Its expectations come from the source tree, so E4-S4 and E4-S5 are covered the moment they add their directories.
- Auto-update via App Installer (`.appinstaller` on a static HTTPS host, check every 8 hours). Store submission is 1.1.
- **Signing (Q-5):** self-signed certificate until 1.0-rc. CI generates and stores it as a secret; the README tells testers how to trust it. Before 1.0-rc a real identity (Azure Trusted Signing preferred) is chosen; the publisher string in the manifest is set for Tunqio and is then frozen.
- Version scheme: SemVer in `Directory.Build.props`; MSIX version `major.minor.patch.0` from the tag; `mpcore` reports the same version through `mp_version()`.

## Third-party components and licences

| Component | Licence | Notes |
|-----------|---------|-------|
| BASS, bassmix, basswasapi, bassflac, bassopus, basswv, bass_ape | Proprietary; **free for non-commercial use** (Q-1: product is non-commercial) | Attribution in About; DLLs fetched by `tools/fetch-native.ps1` (hash-pinned in `tools/native-deps.json`), not committed; licence texts shipped under `licenses/`; revisit ADR-003 if distribution ever becomes commercial |
| BASS_AAC | **GPL** | **Not used.** AAC/M4A/ALAC/WMA decode through BASS's Media Foundation support on Windows |
| bassasio | Separate licence | Post-1.0 |
| pffft | BSD-style (FFTPACK licence) | Vendored |
| nlohmann/json | MIT | Vendored, header-only |
| Catch2 v3 | Boost Software License 1.0 | Test only |
| TagLibSharp | LGPL 2.1 | Dynamically linked NuGet; keep as separate assembly; include licence text |
| Microsoft.Data.Sqlite + SQLitePCLRaw | MIT / Apache 2 / Public domain | |
| CommunityToolkit.Mvvm, CommunityToolkit.WinUI | MIT | |
| Microsoft.Windows.CsWin32 | MIT | Source generator in App only |
| H.NotifyIcon.WinUI | MIT | |
| System.Reactive | MIT | |
| Serilog + sinks | Apache 2 | |
| Microsoft.Extensions.* | MIT | |
| Windows App SDK / WinUI 3 | MIT (SDK) + Microsoft binaries licence | |
| SixLabors.ImageSharp | Six Labors Split | Test dependency only |
| BenchmarkDotNet, xUnit, FluentAssertions, NetArchTest | MIT / Apache 2 | Test only |

The About page lists these; a CI step regenerates `THIRD-PARTY-NOTICES.md` from the NuGet graph plus the native list and fails on an unknown or GPL licence.

## Definition of Done (per card)

1. Acceptance criteria on the card are checked, with promise-type criteria demonstrated in the app.
2. Tests exist at the layer the table above prescribes; native changes have Catch2 coverage and pass under ASan.
3. PR gate green, including benchmarks touching the changed layer.
4. No new analyzer or `/analyze` warnings; no new strings outside `.resw`.
5. `mpcore.h` changes bump the ABI minor (or major if breaking) and update `Interop` in the same PR.
6. Docs updated when a contract, ABI, or schema changed (with a migration).
7. UI cards: keyboard-only walkthrough done and Narrator names present.

## Branching and commits

Trunk-based: `main` always releasable; branches `T-<n>-<slug>` per kanban card; squash merge with `T-<n>` in the subject so `kanban git link` associates commits. Conventional commit prefixes feed release notes.
