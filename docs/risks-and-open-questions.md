# Risks and Open Questions

Open questions were answered on kanban card T-1 on 2026-09-08. The table records the answer and where it landed. Risks have an owner story in roadmap-and-backlog.md.

## Open questions — resolved

| ID | Question | Answer (T-1) | Applied in |
|----|----------|--------------|------------|
| OQ-1 | Commercial or non-commercial distribution? | **Non-commercial.** BASS used under its free licence. | ADR-003; licence table |
| OQ-2 | C#-first or native C++ core? | **C++ core DLL from day one.** | ADR-004 revised; solution-structure.md; build-test-release.md; E0-S9 added |
| OQ-3 | Streaming or cloud sources ever? | **Maybe later.** | `track.source_kind` column reserved (library-and-data.md); ADR-008 |
| OQ-4 | Minimum Windows? | **Windows 10 2004+.** | ADR-001; E8-S6 test matrix keeps a Win10 VM |
| OQ-5 | Code signing? | **Self-signed for now**, real identity before 1.0-rc. | ADR-011; E8-S1 |
| OQ-6 | ARM64 in 1.0? | **x64 only.** | ADR-011 |
| OQ-7 | Write ratings to files by default? | **Opt-in, default off.** | E6-S7; `library.writeRatingsToFiles` |
| OQ-8 | Hover preview in 1.0? | **Keep.** Default off until the user enables it. | E5-S5; `ui.hoverPreview`; `mp_preview_*` in the ABI |
| OQ-9 | Team size? | **One developer.** Backlog sizing stands (about 27 weeks). | roadmap-and-backlog.md |
| OQ-10 | Product name? | **Tunqio** (via follow-up Q-11). | E0-S8; [identity.md](identity.md); all design docs |

## Risk register

Likelihood and impact: Low / Medium / High. Each risk has a mitigation story.

| ID | Risk | L | I | Mitigation | Story |
|----|------|---|---|-----------|-------|
| R-1 | BASS licence terms change or distribution becomes commercial | L | H | Non-commercial confirmed (OQ-1); `IAudioEngine` and the C ABI keep BASS contained in `mpcore/audio`; revisit ADR-003 before any commercial move | E0-S3 |
| R-2 | Sample-accurate gapless via bassmix mix-time sync misbehaves for some formats (MP3 encoder delay, AAC priming) | M | M | Spike with continuous-sine fixtures per format; apply LAME/iTunSMPB gapless info from tags; document best-effort formats | E1-S2 |
| R-3 | Exclusive-mode WASAPI fails on some drivers or fights other apps | H | M | Shared mode default; exclusive opt-in with automatic fallback and a clear `InfoBar`; Realtek, USB DAC and Bluetooth in the test matrix | E1-S6 |
| R-4 | Device change or unplug crashes or silently continues | M | H | `BASS_WASAPI_SetNotify` plus `IMMNotificationClient` in the core; flow 4 is an acceptance test | E1-S7 |
| R-5 | `SwapChainPanel` render thread and XAML composition contend on iGPU | M | M | Spike E0-S5 in C++ with a synthetic preset; adaptive quality from the start | E0-S5 |
| R-6 | Visual latency target unreachable through composition | M | L | ADR-012 restated measurably; look-ahead compensation; document the floor if p95 misses by one frame | E4-S8 |
| R-7 | 100k-track libraries stutter or search slows | M | M | Keyset paging, virtualised `ItemsRepeater`, FTS5 trigram, 100k fixture in PR gate | E3-S3, E3-S9 |
| R-8 | TagLibSharp edge cases stall the scanner | H | L | Per-file timeout, failure isolation, corrupt fixture, file-name fallback | E3-S4 |
| R-9 | Windows App SDK or MSVC toolset churn breaks the build between milestones | M | M | Pin versions centrally; one upgrade card per milestone; unpackaged path kept working | E0-S1, E8-S8 |
| R-10 | FileSystemWatcher misses or floods | H | L | Debounce/coalesce; `Error` → folder rescan; incremental scan on launch | E3-S6 |
| R-11 | Audio-reactive theming causes contrast failures or distraction | M | M | Background layer only; contrast unit test; smoothing and off switch; reduced-motion respected | E4-S6 |
| R-12 | MSIX activation edge cases (multi-select Open launches many instances) | M | M | Single-instance redirection; test with 50 files | E7-S1 |
| R-13 | Scope creep from the extensibility docs | H | M | ADR-008/009 defer; "Later" section; new ideas become board ideas, not cards | product-scope.md |
| R-14 | One developer, long runway | M | H | M1 "It plays" is small and demoable; every milestone ends with a usable build | roadmap |
| R-15 | Hover preview produces unexpected sound | M | L | Default off (Q-8), −12 dB, first-use prompt | E5-S5 |
| R-16 | Tag writing corrupts a user's file | L | H | Temp write, verify re-read, atomic replace; never write the active decode stream; ratings opt-in (Q-7) | E3-S10 |
| **R-17** | **Native/managed boundary bugs: callbacks on audio threads running managed code, handle lifetime races, a native crash taking the process down with no managed exception** | M | H | ABI rules in solution-structure.md (enqueue-only trampolines, drain-before-destroy, `SafeHandle`s); `Interop.Tests` stress create/destroy; SEH guards on every export; ASan in CI; minidump writer | E0-S9, E1-S1, E8-S5 |
| **R-18** | **Two toolchains slow the inner loop and CI (msbuild, mixed-mode debugging, ASan runs)** | M | L | Native tests run standalone in seconds; App F5 uses unpackaged mixed-mode; CI caches native deps; ASan job runs in parallel | E0-S1 |
| **R-19** | **Self-signed releases (Q-5) block testers who will not trust a certificate, and changing publisher identity later breaks in-place upgrades** | M | M | README trust instructions; move to a real identity before 1.0-rc; freeze the Tunqio publisher identity from the first signed build | E8-S1 |

## Assumptions

1. The listener owns local files; no DRM, no streaming (a `source_kind` column is the only concession).
2. One user profile per machine account.
3. Libraries up to about 150k tracks; beyond that we degrade gracefully.
4. A single output device at a time.
5. English UI for 1.0.
6. The visualizer is worth a dedicated native render thread; Ambient Glow (Composition-based, no D3D) is the fallback visual if E4 slips.
