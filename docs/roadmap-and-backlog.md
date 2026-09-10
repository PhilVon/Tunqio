# Roadmap and Backlog

Milestones, epics and stories sized for transcription onto the kanban board. Every story traces to product-scope.md; every acceptance criterion is a **promise** (what a person can do or see) unless marked *hypothesis* (a claim about mechanism to verify when the code is read). Sizes: **S** ≤ 1 day, **M** 2–3 days, **L** 4–7 days, **XL** must be split before starting. Durations assume one developer (OQ-9).

## Transcribing to the board

- One task per epic (`E0`…`E8`) with the stories as subtasks (`kanban add "<title>" --parent T-<epic>`).
- Labels: `native` (C++ in `mpcore`), `audio`, `analysis`, `render`, `interop`, `core`, `library`, `ui`, `windows`, `infra`, `spike`, `perf`, `a11y`, `docs`. Milestone as label `M0`…`M5`.
- Priority: everything in M0–M1 is `P1`; M2–M3 `P2`; M4–M5 `P3` until the previous milestone closes.
- Status: all `Backlog` except E0 stories whose dependencies are met, which start in `Ready`. Promote others to `Ready` only when their `Depends on` list is Done.
- ADRs from decisions.md are on the board as `adr` docs (D-2…D-14) linked to T-1; all accepted on 2026-09-08. E0-S2 (ADR sign-off) **is** T-1 and is not created again.
- Spikes end with a `--kind spike` doc recording the finding, and may retire hypothesis criteria elsewhere.

Transcribed on 2026-09-08: epics and stories below exist on the board with the same titles; the board is the live status, this file is the design intent.

## Milestones

| Milestone | Name | Exit criterion (demo) | Epics | Est. (1 dev) |
|-----------|------|----------------------|-------|--------------|
| **M0** | Foundations | Mixed C++/C# solution builds in CI; ADRs signed off; two spikes answered; interop layer round-trips the ABI; a blank WinUI window with Mica opens in < 1 s | E0 | 3 weeks |
| **M1** | It plays | Open files from Explorer or drop; gapless playback of an album; transport, seek, volume; SMTC; queue persists across restart | E1, E2 | 5 weeks |
| **M2** | Library | Add folders; scan 10k files; browse Albums/Artists/Tracks; search; play from library; album art | E3 | 5 weeks |
| **M3** | It looks alive | Spectrum Bars and Waveform presets at 60 fps; audio-reactive theming; adaptive quality; latency harness reports p95 | E4 | 5 weeks |
| **M4** | Modes and polish | Discovery/Focus/Curation; playlists with M3U8; tag editing; settings; mini player; tray, toasts, jump list | E5, E6, E7 | 6 weeks |
| **M5** | Release | Signed MSIX with auto-update; perf targets verified; accessibility pass; soak test clean; docs and licences | E8 | 3 weeks |

Post-1.0 items are listed at the end and are not sized.

## Epic dependency graph

```
E0 ──► E1 ──► E2 ──► E5 ──► E8
 │      │      │      ▲
 │      └──► E4 ─────┘
 └──► E3 ──► E6 ──► E8
        └──► E7 ──► E8
```

E3 (library) depends only on E0 and can run in parallel with E1/E2 when there are two developers.

---

## E0 · Foundations (M0)

Goal: a repo, a build, signed-off decisions, and the two technical unknowns retired.

### E0-S1 · Solution scaffold and CI · **L** · `infra` `native`
Create the mixed solution per solution-structure.md: `mpcore.vcxproj` (C++20, `/W4 /WX`, clang-format), `mpcore.tests` (Catch2, plus an ASan configuration), the C# projects, central package versions, analyzers, `global.json`, `Directory.Build.props`, vendored or vcpkg native deps (decide and record), and the PR workflow (format, msbuild, native and managed tests, artifacts).
- [ ] `msbuild Tunqio.sln -p:Configuration=Release -p:Platform=x64` succeeds locally and in GitHub Actions on a clean runner
- [ ] Catch2 tests run in CI in both Release and ASan configurations, and a deliberate use-after-free in a scratch test is caught by ASan
- [ ] Architecture test fails the build when `Core` references `Interop` (verified by a deliberate temporary violation)
- [ ] Windows App SDK, .NET and MSVC toolset versions are pinned in one place each
- [ ] Unpackaged Debug F5 with mixed-mode debugging opens a window in under 1 s and can break on a native breakpoint

### E0-S2 · ADR sign-off · **S** · `docs`
This story is card **T-1** on the board (not a subtask of E0). Review decisions.md with the product owner; record Accepted/Rejected per ADR on the board.
- [ ] Every ADR in decisions.md has status Accepted, or a replacement ADR
- [ ] OQ-1…OQ-10 are answered on the board or explicitly deferred with a default

### E0-S3 · BASS packages, licence attribution and engine boundary · **S** · `audio` `native` `docs`
Non-commercial use confirmed (Q-1). Add the native fetch script with hash verification; record attribution; confirm `mpcore/audio` is the only code that includes BASS headers.
- [ ] `tools/fetch-native.ps1` downloads pinned BASS core, bassmix, basswasapi and add-on packages (headers, `.lib`, `.dll`) and fails on hash mismatch
- [ ] BASS licence text and the non-commercial attribution appear in `THIRD-PARTY-NOTICES.md` and are wired to the About page placeholder
- [ ] A Catch2 include-grep test fails if any file outside `mpcore/src/audio/` includes a BASS header

### E0-S4 · Spike: BASS + bassmix + basswasapi hello world in mpcore · **M** · `spike` `audio` `native`
C++ console app linked against a first cut of `mpcore`: init no-sound device, create mixer, WASAPI shared output pulling from the mixer, play a WAV, print `BASS_WASAPI_GetInfo` latency. Drafts the first version of `mpcore.h` (create/destroy, open, play, clock).
- [ ] Audio plays through the default device with no audible glitch for 60 s
- [ ] Reported output latency is logged for shared mode at 10 ms buffer and for exclusive mode
- [ ] Spike doc records the exact BASS flag combinations used and any surprises in mixer position tracking
- [ ] `mpcore.h` draft checked in with the ABI rules from solution-structure.md applied

### E0-S5 · Spike: SwapChainPanel render thread on iGPU · **M** · `spike` `render` `native`
WinUI window whose `SwapChainPanel` native pointer is handed to a C++ renderer in `mpcore` (dedicated render thread, waitable composition swap chain) drawing 64 instanced quads whose heights animate. Proves the C#-to-native handoff of `ISwapChainPanelNative`.
- [ ] Sustained 60 fps at 1920×1080 on the reference iGPU with the window resized live
- [ ] Frame time histogram captured via DXGI frame statistics and attached to the spike doc
- [ ] Resize and DPI change do not tear or crash after 100 rapid resizes
- [ ] Spike doc states whether WARP fallback renders at ≥ 30 fps for the same scene

### E0-S6 · Logging, DI host, settings store · **S** · `infra`
Generic host in `App`, Serilog rolling file, `ISettingsStore` over `settings.json` (kept as the settings store by Q-15; the `setting` table is reserved).
- [ ] Log file appears under the documented path with session ID on first line
- [ ] A setting written before exit is read back on next launch

### E0-S7 · Fixture generator · **M** · `infra` `library`
`tools/FixtureGen` produces the fixture library (60 files, every format, edge cases) and the 100k database.
- [ ] Generated files have the tags described in library-and-data.md and play in another player
- [ ] Generation is deterministic (same output hash on two runs)

### E0-S8 · Product name and identity · **S** · `docs` `windows`
Name is **Tunqio** (Q-11 on T-1). Write [identity.md](identity.md) as the single source for display name, package identity and publisher, Application Id, `tunqio://` scheme and commands, `%LocalAppData%\Tunqio` folder, `tunqio.exe` alias, title and tooltip formats, and what is frozen at 1.0. The native library keeps the internal name `mpcore` and the `mp_` ABI prefix. The code-side checks live on the stories that create each surface: manifest and window title on E0-S1, data path on E0-S6, alias and scheme on E7-S1, tooltip on E7-S3.
- [ ] identity.md exists and lists every identifier with its location and freeze status
- [ ] Every design and foundation doc agrees with identity.md; no `MusicPlayer` / `musicplayer://` placeholder remains under docs/

### E0-S9 · Interop layer over the C ABI · **L** · `interop` `native`
Depends on: E0-S4. `Tunqio.Interop`: `LibraryImport` bindings for every export in `mpcore.h`, `SafeHandle`s, `[UnmanagedCallersOnly]` trampolines that only enqueue for the event pump thread, ABI version check, `mp_last_error` to `EngineEvent.Error` conversion, and `Interop.Tests` against the real DLL.
- [ ] Every export in `mpcore.h` has a binding and a round-trip test (the test enumerates the header and fails on an unbound export)
- [ ] A callback fired from a native thread reaches `IObservable<EngineEvent>` on the pump thread; no managed code runs on the native thread (asserted by thread id in a test)
- [ ] 1000 create/destroy cycles leave native handle counts stable and no callback arrives after destroy
- [ ] Loading a DLL with a bumped major ABI version is refused with a clear error
- [ ] `mp_engine_get_clock` and `mp_analysis_try_get_latest` bindings cost < 5 µs per call (BenchmarkDotNet)

---

## E1 · Audio engine and playback core (M1)

Goal: `mpcore/audio` (C++) exposed through the ABI, `IAudioEngine` (Interop) and `PlaybackSession` (C# Core) complete and tested; no UI beyond a debug window.

### E1-S1 · Engine skeleton: init, open, play, pause, stop, seek, volume · **L** · `audio` `native`
Depends on: E0-S4, E0-S9. Implement the engine in `mpcore/audio` per ADR-003 (mixer, WASAPI shared output, RAII handles, SEH-guarded exports) and the matching `IAudioEngine` methods in Interop; `mp_event` stream; `mp_engine_get_clock`.
- [ ] Every fixture format opens and reports duration within 50 ms of the tag duration
- [ ] Seek lands within one frame (verified with a fixture whose sample value encodes position)
- [ ] Pause and resume are click-free (guard fade applied; verified by decode-to-buffer test showing no step > 1e-3)
- [ ] Volume is applied in the mixer, logarithmic taper, and mute is instantaneous
- [ ] No allocation in the WASAPI proc or DSP callbacks (`RT_ASSERT_NO_ALLOC` hook in Debug Catch2 tests)
- [ ] Every export returns `MP_E_INVALID_ARG` on null or wrong `struct_size` and never throws across the boundary (ABI contract tests)

### E1-S2 · Spike: gapless join per format · **M** · `spike` `audio` `native`
Depends on: E1-S1. Prove mix-time END sync plus `StreamAddChannelEx` gives sample-continuous joins; measure per format with continuous-sine fixtures.
- [ ] Table of formats × join discontinuity magnitude in the spike doc
- [ ] For MP3 and AAC, encoder delay/padding from LAME/iTunSMPB tags is applied and the join is continuous
- [ ] Formats where gapless is best-effort are listed and reflected in product-scope.md

### E1-S3 · Gapless and preload · **M** · `audio` `native`
Depends on: E1-S2. `PreloadNextAsync` opens and prescans the next track; join at mix time; `TrackStarted` event carries the exact position.
- [ ] Playing two consecutive fixture tracks produces one continuous tone with no gap or click
- [ ] Now-playing metadata (via `TrackStarted`) changes within 100 ms of the audible boundary
- [ ] Changing the "next" track after preload discards the preloaded stream without leaking handles (*hypothesis*: handle count stable over 1000 changes)

### E1-S4 · Crossfade and guard fades · **S** · `audio` `native`
User crossfade 0–12 s as a per-source equal-power envelope run by the mixer (attribute slides do not advance inside a mixer, E1-S5); guard fade on stop/seek/manual skip (E1-S1). The join mode is chosen per boundary by the caller (`mp_engine_preload_next_ex`, `CrossfadePolicy`).
- [x] Crossfade of 5 s overlaps tracks with equal-power curve (verified by RMS across the overlap staying within 3 dB: measured within 0.5 dB, `[crossfade]`)
- [x] Crossfade is not applied at gapless album boundaries when gapless is on and the tracks are from the same album (`CrossfadePolicy.Resolve`; a `MP_JOIN_GAPLESS` join ignores the crossfade setting, measured)

### E1-S5 · ReplayGain · **S** · `audio` `native`
Apply track or album gain and peak with preamp and clipping prevention.
- [ ] A fixture tagged −6 dB plays 6 dB quieter than untagged (measured)
- [ ] Peak limiting prevents clipping when gain + preamp would exceed 0 dBFS

### E1-S6 · Exclusive mode and device selection · **M** · `audio` `native`
Device enumeration, output init at the device's native rate (both modes, not only exclusive), fallback to shared on failure. `OutputPolicy` turns the stored `output.*` settings into the `OutputConfig` the engine opens; the Settings › Output page that edits them is E6-S3.
- [x] User can choose a device and mode: the persisted `output.deviceId`/`output.mode`/`output.bufferMs` resolve to an `OutputConfig` the engine opens, and a device that is gone falls back to the default (`OutputPolicy`, remembered by endpoint id so an index shift does not move it)
- [ ] Exclusive mode on the reference Realtek and a USB DAC plays bit-perfect (verified by loopback capture comparing to the source) — on the reference machine, so assumed passing until that hardware exists and verified in E8-S2
- [x] When exclusive init fails, playback continues in shared mode and an `EngineEvent.Error` explains why (forced through a test seam in the `MP_STATIC` build: the output starts, `exclusive` reads 0 and the message carries the device and the BASS error)
- [x] Mixer rate follows the device rate so no resampling occurs for matching sources — and in shared mode as well as exclusive, which the hypothesis missed: BASSWASAPI honours a differing rate in shared mode and Windows resamples for it (measured: a 44.1 kHz device was being driven at the mixer's 48 kHz)

### E1-S7 · Device change handling · **M** · `audio` `native`
`BASS_WASAPI_SetNotify` in `mpcore`: default device change, device removal and device failure surfaced as `mp_event`s. No separate `IMMNotificationClient` — BASSWASAPI's notification callback *is* one, and a second would be the same events twice. The callback only enqueues; a watch thread does the work under the control mutex, because reopening a device from inside a driver's notification thread is how that thread deadlocks against the audio thread it is stopping.
- [x] Unplugging the active device pauses within 500 ms and raises `DeviceLost` (measured at 1 ms; the output is closed and the track stays loaded at its position, so reopening and resuming carries on)
- [x] Re-plugging raises `DeviceChanged` with the device ID so the UI can offer to switch back (`B = 0` = offered; the engine never switches on its own, per the "no silent continuation on the wrong device" flow)
- [x] Changing the Windows default device while on "default" migrates playback within 1 s without a crash (measured at 10 ms; `DeviceChanged` with `B = 1`. A caller that named a device is left alone.)
- [x] Soak: 200 simulated device changes leave handle counts stable (225 → 229 across 200 notifications including 50 real close/reopen cycles)

### E1-S8 · Analysis tap and lock-free ring buffer · **M** · `audio` `analysis` `native`
`mpcore/common` SPSC ring buffer (cache-line-aligned atomics, per performance-optimization.md); `mpcore/analysis/tap.h`, a DSP on the mixer that stages mixed float frames into fixed 512-frame hops (ADR-010) and publishes each with the mixer byte position of its first frame. On the mixer rather than after the engine's own envelope, so the visualization follows the music and not the volume slider. `mp_analysis_try_get_latest` stays E4-S1's: turning a hop into an `mp_analysis_frame` is the analysis, and half a frame would be worse than none.
- [x] Ring buffer stress test (Catch2, multi-threaded): no lost or duplicated frames, no torn positions (the ring in `test_spsc_ring.cpp`, and the tap over it in `test_analysis_tap.cpp` — 2000 hops written in ragged 1–997-frame pieces, every sample verified against the position the block claims)
- [x] Tap adds < 0.2 ms to the DSP callback at 48 kHz stereo (benchmark) (measured 0.0008 ms per 480-frame callback, and `rt_guard` proves it allocates nothing)

### E1-S9 · PlayQueue model · **M** · `core`
Immutable `PlayQueue` per library-and-data.md.
- [ ] Shuffle on then off restores original order with the current track in place
- [ ] Repeat One: natural end replays; manual Next advances
- [ ] Repeat All: end of queue wraps; Off: stops
- [ ] Same track twice in the queue behaves as two items
- [ ] 100% branch coverage on `PlayQueue`

### E1-S10 · PlaybackSession · **L** · `core`
Depends on: E1-S1, E1-S3, E1-S9. C# single owner of state; drives `IAudioEngine`; snapshots at 10 Hz; queue persistence; history events.
- [ ] Sequence play/next/previous/seek/pause through a fake engine matches a scripted expectation table
- [ ] Previous within 3 s restarts, otherwise goes back
- [ ] Queue and position are captured on stop and restored on start (integration test with real engine paused)
- [ ] A `play_event` is emitted with correct heard time and `completed` flag per the 50%/4-minute rule

### E1-S11 · Soak runner · **S** · `perf` `audio`
`tools/SoakRunner` (C# over Interop) loops the fixture library and logs `mp_engine_stats.underruns`.
- [ ] 1-hour soak on the dev machine completes with zero underruns (24 h run is E8)

---

## E2 · Shell, Now Playing and transport UI (M1)

Goal: a usable player window over E1.

### E2-S1 · Shell layout and breakpoints · **M** · `ui`
Three-panel grid with compact (< 800) and medium (< 1200) breakpoints; Mica on Win11; theme switching.
- [ ] Layout matches user-interface.md proportions at 1600 px and collapses correctly at 700 px
- [ ] Theme follows system and can be overridden; switching is instant with no white flash

### E2-S2 · Transport controls panel · **M** · `ui`
Play/pause, previous, next, progress with hover tooltip and drag seek, elapsed/remaining toggle, volume with mute, shuffle, repeat.
- [ ] Every control works with mouse, keyboard and Narrator
- [ ] Seek drag shows the target time and commits on release; position updates at 10 Hz without jitter
- [ ] Shuffle and repeat state icons reflect `PlaybackSnapshot`

### E2-S3 · Now Playing panel (metadata and art) · **M** · `ui`
Art, title, artists, album, year, format badge, placeholder art derived from title hash.
- [ ] Art up to 1000 px loads without blocking the UI thread (decoded off-thread)
- [ ] Missing art shows a deterministic colour placeholder with the album initial

### E2-S4 · Open files and drop · **S** · `ui` `windows`
File picker, folder picker, drag-and-drop of files/folders onto the window; builds a queue in file-name order.
- [ ] Dropping a folder of 200 files starts playback of the first within 500 ms and enqueues the rest

### E2-S5 · Queue panel · **M** · `ui`
Now-playing pinned, upcoming with drag reorder, remove, clear upcoming, remaining time.
- [ ] Drag reorder updates the engine's preloaded next track when the item after current changes

### E2-S6 · Keyboard accelerators · **S** · `ui` `a11y`
Shell-level accelerators from ui-screens-and-flows.md with text-box opt-out.
- [ ] Space toggles playback unless a text box has focus
- [ ] All transport shortcuts work in every view

### E2-S7 · Error surfaces · **S** · `ui`
Transient and sticky `InfoBar`s wired to `EngineEvent.Error`, `DeviceLost`, scan reports.
- [ ] Device unplug shows the sticky bar with a working "Use default device" action (flow 4)

### E2-S8 · Debug/diagnostics overlay · **S** · `ui` `perf`
Toggle (Ctrl+Shift+D) showing output latency, underruns, engine state, frame time (once E4 exists).
- [ ] Overlay values update live and can be copied to clipboard

---

## E3 · Library and data (M2)

Goal: library-and-data.md implemented end to end.

### E3-S1 · Database, migrations, connection pool · **M** · `library`
Schema v1, WAL, migration runner, corruption handling.
- [ ] Fresh database created on first launch; migrations table records v1
- [ ] A deliberately corrupted file is renamed aside and a fresh database created with a user-visible notice
- [ ] Migration test runs every migration from an empty database and from each prior version fixture

### E3-S2 · Repositories: tracks, albums, artists, genres · **L** · `library`
Query builder for `TrackQuery`, keyset paging, DTOs, `UpsertBatchAsync`.
- [ ] Every filter/sort combination in `TrackQuery` has a test; paging returns each row exactly once across pages
- [ ] Upsert of 500 tracks in one transaction completes in < 150 ms on the 100k database

### E3-S3 · Virtualised list and grid infrastructure · **M** · `ui` `perf`
`ItemsRepeater`-based grid and `ListView` with incremental loading over `StreamAsync`.
- [ ] Scrolling the 100k Tracks view end to end never drops below 50 fps on the reference machine
- [ ] Memory grows by < 50 MB while scrolling the full 100k list (items are recycled)

### E3-S4 · Tag reader · **M** · `library`
TagLibSharp wrapper with timeout, isolation, fallbacks, artist splitting rules, compilation detection, gapless info, ReplayGain, MusicBrainz IDs.
- [ ] Every fixture file yields the expected `ScannedTrack` (snapshot test)
- [ ] The corrupt-tag fixture is imported with file-name metadata and appears in the scan report
- [ ] A file that hangs the reader (simulated) is skipped after 5 s and the scan continues

### E3-S5 · Scanner pipeline · **L** · `library`
Enumerate → Diff → ReadTags → ExtractArt → Upsert with channels, progress, cancellation, missing-marking.
- [ ] 10k fixture files scan in < 90 s on the reference machine; a second scan with no changes completes in < 10 s
- [ ] Cancelling mid-scan leaves a consistent database (every batch fully applied or not at all)
- [ ] Files removed from disk are marked missing, hidden from views, and reappear when restored

### E3-S6 · File system watcher · **M** · `library`
Per-folder watcher, debounce, coalesce, rename handling, overflow → rescan.
- [ ] Adding a file to a watched folder shows it in Albums within 5 s without a manual rescan
- [ ] Copying 5k files at once results in a complete library with no duplicates (overflow path exercised)

### E3-S7 · Album art cache and palette · **M** · `library`
Hash-keyed cache, three sizes, palette extraction, folder image fallback.
- [ ] An album with only `folder.jpg` shows art; one with embedded art prefers the front cover
- [ ] Palette JSON has five colours with luminance; used by E4-S6
- [ ] Regenerating the cache from scratch for 1k albums takes < 60 s

### E3-S8 · Library views: Albums, Artists, Tracks, Genres, Folders, Recent, Most played · **L** · `ui`
Depends on: E3-S2, E3-S3, E3-S7. Sidebar pages, detail pages, sort/filter chips, context menus, multi-select.
- [ ] Each view in ui-screens-and-flows.md exists with its listed actions
- [ ] Album detail groups by disc and plays the album gaplessly from any track
- [ ] Keyboard-only: navigate to an album and play it without a mouse

### E3-S9 · Search · **M** · `library` `ui`
FTS5 trigram table maintained in-transaction; `ISearchService`; search UI with grouped results and keyboard flow.
- [x] 3-character query on the 100k database returns in < 50 ms p95 (benchmark in PR gate)
- [x] Typing continuously never shows stale results (cancellation verified by test)
- [x] Rebuild search index action repairs a deliberately desynchronised FTS table

### E3-S10 · Tag writer and editor · **L** · `library` `ui`
Single and batch tag editing, temp-write-verify-replace, undo stack, active-track deferral.
- [ ] Editing a FLAC and an MP3 writes tags readable by another tagger; file plays afterwards
- [ ] A simulated write failure leaves the original file byte-identical
- [ ] Batch edit of 12 tracks shows progress and can be undone in the session (flow 8)

### E3-S11 · Play history and counts · **S** · `library`
Record events from `PlaybackSession`, update counts, Recently/Most played views.
- [ ] Skipping a track after 10 s does not increment play count; listening past half does

### E3-S12 · Library settings page · **S** · `ui` `library`
Folders CRUD, scan status, split artists, purge missing, rebuild index, import/export playlists.
- [x] Every action in ui-screens-and-flows.md Settings › Library is present and works (import/export playlists follows the playlist store, E6-S1/E6-S2)

### E3-S13 · Library benchmarks and 100k fixture in CI · **S** · `perf` `infra`
- [ ] PR gate runs open-time and search benchmarks against the 100k database with thresholds

---

## E4 · Analysis and visualization (M3)

Goal: the visualizer and reactive UI.

### E4-S1 · Analysis thread and AnalysisFrame · **M** · `analysis` `native`
Depends on: E1-S8. `mpcore/analysis` thread consumes the tap at 512-frame hops, runs pffft (2048-point, Hann), produces spectrum, waveform, RMS, peak; publishes to the triple buffer; `mp_analysis_try_get_latest` export and the Interop `IAnalysisFrameSource`.
- [ ] Frames arrive at ~94 Hz while playing and stop within 100 ms of pause
- [ ] Zero allocations per hop (`RT_ASSERT_NO_ALLOC` in Debug Catch2 test)
- [ ] pffft output matches a naive DFT reference to 1e-4 on a fixture block
- [ ] The triple-buffer front is never torn under a Catch2 stress test; the managed `TryGetLatest` copy costs < 5 µs

### E4-S2 · Feature extraction · **M** · `analysis` `native`
Spectral centroid, harmonic ratio, six octave bands, onset detection (spectral flux with adaptive threshold) in `mpcore/analysis`.
- [ ] Synthetic signal tests: 1 kHz sine centroid within 2%; noise vs. sine harmonic ratio separated by > 0.4; click train onsets detected with < 10 ms error and no false positives on sustained tones
- [ ] Extraction < 4 ms per hop on the reference CPU (Catch2 benchmark)

### E4-S3 · Render host and preset loader · **L** · `render` `native`
Depends on: E0-S5, E0-S9. `mpcore/render`: D3D11 device, composition swap chain attached to the `SwapChainPanel` pointer passed over the ABI, render thread, `preset.json` (nlohmann/json) + HLSL loading with error reporting, constant buffer contract, resize/DPI; `mp_renderer_*` exports and the Interop `IVisualizationHost`.
- [ ] A preset with a shader compile error shows the error in the preset switcher and falls back to the previous preset
- [ ] Toggling the Now Playing panel visibility stops and restarts rendering without leaking device resources
- [ ] WARP fallback renders at ≥ 30 fps when no hardware adapter is available

### E4-S4 · Built-in presets: Spectrum Bars, Waveform · **M** · `render` `native`
- [ ] Both render at 60 fps at 1080p on the reference iGPU
- [ ] Golden-image tests pass for a fixed `AnalysisFrame`
- [ ] Parameters (bar count, smoothing, colour source) are exposed in Settings › Visualization

### E4-S5 · Built-in presets: Radial Spectrum, Ambient Glow · **M** · `render` `native`
- [ ] Same criteria as E4-S4
- [ ] Ambient Glow uses palette colours from album art when available

### E4-S6 · Audio-reactive theming · **M** · `ui` `analysis`
Depends on: E3-S7, E4-S2. C# side: poll `IAnalysisFrameSource` at 30 Hz, HSL mapping with EMA smoothing, art palette blend, Composition gradient animations, contrast guarantee, reduced-motion/high-contrast off switches; pass palette to the renderer via `SetThemeColors`.
- [ ] Background gradient follows the music with the configured smoothing and can be turned off
- [ ] Contrast unit test: foreground text on every reactive surface stays ≥ 4.5:1 across the full colour range
- [ ] Enabling Windows reduced motion stops the reactive theming within one second

### E4-S7 · Adaptive quality · **M** · `render` `perf` `native`
Quality controller in `mpcore/render` with 2 s hysteresis stepping render scale, update rate and preset complexity; `mp_renderer_set_quality` and `mp_render_stats`.
- [ ] Forcing a slow GPU (WARP) drops quality to Low within 4 s and shows it in the diagnostics overlay
- [ ] Restoring headroom returns to High within 10 s without oscillating

### E4-S8 · Latency compensation and harness · **M** · `analysis` `render` `perf` `native`
Depends on: E4-S1, E4-S3. Implement ADR-012 look-ahead in `mpcore/render` using mixer position, WASAPI latency and DXGI present statistics; `tools/LatencyHarness` (C# over Interop) measuring p50/p95/p99.
- [ ] Harness runs unattended and produces a report with the three percentiles
- [ ] p95 within one refresh interval on the reference machine, or a spike doc explaining the floor

### E4-S9 · Preset switcher and visualization settings · **S** · `ui`
- [ ] Switching presets takes < 200 ms with no black frame
- [ ] User presets dropped into `%LocalAppData%\Tunqio\presets\` appear after a refresh

---

## E5 · Modes: Discovery, Focus, Curation (M4)

### E5-S1 · ShellState and mode switching · **M** · `ui`
Segmented control, shortcuts, persisted mode, Composition transitions honouring reduced motion.
- [ ] Switching modes never interrupts playback or resets sidebar navigation
- [ ] Esc leaves Focus to the previous mode with the layout restored (flow 7)

### E5-S2 · Focus mode · **M** · `ui`
Full-width Now Playing, controls auto-hide after 3 s, edge-peek queue, live-region announcements.
- [ ] Controls reappear on pointer move or any key within 100 ms
- [ ] Narrator announces track changes politely

### E5-S3 · Discovery mode layout · **S** · `ui`
Albums grid default, Ambient Glow behind art.
- [ ] Discovery matches the mode table in ui-screens-and-flows.md

### E5-S4 · Curation mode dual pane · **L** · `ui`
Source pane (library or playlist) and target playlist pane, drag between panes, batch action bar, undo/redo stack in `PlaylistEditor`.
- [ ] Flow 6 passes end to end including Ctrl+Z
- [ ] Dragging 500 selected tracks completes in < 1 s

### E5-S5 · Hover preview (Discovery) · **M** · `ui` `audio` `native`
Kept in 1.0 (Q-8). `mp_preview_start/stop` in `mpcore/audio`: second mixer channel at −12 dB, ducking, fade in/out, single-preview rule, bypassed by the analysis tap; C# hover behaviour and the `ui.hoverPreview` opt-in.
- [ ] Hovering an album tile for 500 ms starts a preview; leaving fades it in 200 ms; the visualizer keeps following the main track
- [ ] Preview is off by default on first run until the user enables it (per R-15)

### E5-S6 · Mini player window · **M** · `ui` `windows`
Second window, always-on-top, corner snap, marquee, transport.
- [ ] Mini player controls playback and reflects state; closing it returns to the main window

---

## E6 · Playlists, settings and remaining UI (M4)

### E6-S1 · Playlists CRUD and detail view · **M** · `library` `ui`
- [ ] Create, rename, delete (with confirm), reorder, add/remove; totals shown

### E6-S2 · M3U8 import/export and auto-export · **S** · `library`
- [ ] Exported file opens in another player with correct paths; importing it recreates the playlist; every change auto-exports within 5 s

### E6-S3 · Settings shell and Playback/Output/Appearance pages · **M** · `ui`
- [ ] Every setting key in solution-structure.md has a control; changes apply live where the design says so; "test tone" plays through the selected device

### E6-S4 · Shortcuts settings page · **S** · `ui` `a11y`
- [ ] Rebinding detects conflicts; reset restores defaults; bindings persist

### E6-S5 · About and Diagnostics page · **S** · `ui`
- [ ] Licences listed from `THIRD-PARTY-NOTICES.md`; export diagnostics zip contains logs, settings (redacted paths optional) and system info

### E6-S6 · First-run welcome · **S** · `ui`
- [ ] Flow 1 completes from a fresh profile to first sound with no dead ends

### E6-S7 · Ratings · **S** · `library` `ui`
- [ ] Rate from Now Playing, Tracks view and shortcuts; optional write-to-file per OQ-7

---

## E7 · Windows integration (M4)

### E7-S1 · MSIX manifest, file associations, protocol, single instance · **M** · `windows`
Depends on: E0-S8. Associations for all formats, `tunqio://`, `AppInstance` redirection, `CommandRouter`.
- [ ] Double-clicking a FLAC while running plays it in the existing window (flow 2)
- [ ] Selecting 50 files in Explorer and pressing Enter results in one instance with a 50-item queue
- [ ] `tunqio://play?path=...` and `tunqio://toggle` work from a browser and the command line

### E7-S2 · System Media Transport Controls · **M** · `windows`
- [ ] Media keys work with the app in the background; the Windows volume flyout shows art, title, artist, album and a moving timeline
- [ ] Hardware Next/Previous and the flyout buttons drive `PlaybackSession`

### E7-S3 · Tray icon · **S** · `windows`
- [ ] Tray menu offers play/pause, next, previous, show, exit; minimise/close-to-tray settings work; tooltip shows the current track

### E7-S4 · Toast notifications · **S** · `windows`
- [ ] Opt-in toast on track change with working Previous/Play-Pause/Next buttons; suppressed in Focus mode and when the window is in the foreground

### E7-S5 · Jump list · **S** · `windows`
- [ ] Ten recent tracks and pinned playlists appear in the taskbar jump list and launch correctly

### E7-S6 · Windows 11 enhancements · **S** · `windows` `ui`
Mica, snap layout hints, rounded corners verified; graceful fallback on Windows 10.
- [ ] App looks correct on Windows 10 2004 VM and Windows 11 reference machine

---

## E8 · Release engineering, verification and hardening (M5)

### E8-S1 · Code signing and release pipeline · **M** · `infra`
Self-signed until 1.0-rc (Q-5). `release.yml`, CI self-signed cert as a secret, README trust instructions, `.appinstaller`, SBOM (NuGet plus native list), notes, `mpcore.pdb` symbol archive; a decision card for the real signing identity before rc.
- [ ] Tagging `v1.0.0-rc.1` produces a signed MSIX that installs on a clean Windows 11 machine (after trusting the cert per the README) and auto-updates to `rc.2`
- [ ] Publisher string is frozen and documented once the product name (Q-11) is applied

### E8-S2 · Performance verification pass · **L** · `perf`
Run every harness in build-test-release.md on the reference machine; fix or document.
- [ ] All targets in product-scope.md met, or an accepted deviation recorded on the board with a reason

### E8-S3 · 24-hour soak · **S** · `perf` `audio`
- [ ] Zero underruns and stable memory (< 5% growth) over 24 h in shared mode

### E8-S4 · Accessibility pass · **M** · `a11y`
Axe.Windows scan, Narrator walkthrough of every flow, high-contrast and 200% text checks.
- [ ] No critical Axe violations; all ten flows completable with keyboard and Narrator

### E8-S5 · Crash reporting (opt-in) · **M** · `infra` `native`
Minidump writer (`MiniDumpWriteDump`, covers native crashes in `mpcore`) + last 200 log lines queued, user shown contents, upload endpoint configurable.
- [ ] A forced crash produces a report on next launch that the user can inspect and decline

### E8-S6 · Test matrix run · **M** · `infra`
Windows 10 2004 VM, Windows 11, Realtek, USB DAC, Bluetooth headset, 100% / 150% / 200% scaling, multi-monitor mixed DPI.
- [ ] Matrix results recorded; blockers fixed

### E8-S7 · Documentation and licences · **S** · `docs`
User-facing README, install and trust-cert instructions, `THIRD-PARTY-NOTICES.md`, updated architecture docs reflecting what shipped.
- [ ] A new developer can clone, fetch natives, build and run within 30 minutes following the docs

### E8-S8 · Windows App SDK / .NET upgrade card · **S** · `infra`
Dedicated card per milestone to bump pinned versions and re-run the gate.
- [ ] Versions current at release; changelog reviewed for breaking changes

---

## Post-1.0 backlog (unsized)

1.1: smart playlists; 10-band EQ and DSP chain; EBU R128 scanning; lyrics (embedded + .lrc); cue sheets; folder rescan improvements; Last.fm scrobbling (uses `play_event`); Microsoft Store submission.

Later: ASIO output; visualization code plugins as native DLLs against a versioned C ABI (ADR-009); MusicBrainz/Cover Art Archive lookup; RGB lighting; ARM64; portable build; `WM_APPCOMMAND` fallback if SMTC proves insufficient on some hardware; a real code-signing identity before 1.0-rc (Q-5).

## Estimate summary (one developer, per Q-9)

| Milestone | Stories | Rough weeks |
|-----------|---------|-------------|
| M0 | 9 (E0-S2 is T-1) | 3 |
| M1 | 19 | 5 |
| M2 | 13 | 5 |
| M3 | 9 | 5 |
| M4 | 19 | 6 |
| M5 | 8 | 3 |
| **Total** | **77** | **27** |

About six months to 1.0 for one developer at full time, with a demoable build at the end of every milestone. The native core adds roughly a week over the C#-first estimate (interop layer and two toolchains), offset by no later "port the hot path" work.
