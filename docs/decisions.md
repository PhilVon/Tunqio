# Architecture Decision Records

This log resolves contradictions in the original foundation documents and records the decisions the rest of the design depends on. Each record has a status: **Proposed** (needs sign-off before the affected work starts), **Accepted**, **Rejected**, or **Superseded**. Where a record overrides a section of an earlier document, that document carries a banner pointing here.

Sign-off happened on kanban card T-1 on 2026-09-08 (questions Q-1 to Q-10). Board ADR docs D-2 to D-14 mirror the statuses below; this file is the source of truth for content.

| ID | Title | Status | Affects |
|----|-------|--------|---------|
| ADR-001 | .NET 8 + Windows App SDK / WinUI 3, Windows 10 2004+ | Accepted (Q-4: Win10 2004+) | all |
| ADR-002 | Render into a WinUI 3 SwapChainPanel, not WPF D3DImage | Accepted | visualization-engine.md |
| ADR-003 | BASS + bassmix + basswasapi only; no PortAudio, libsamplerate, IPP or FFTW | Accepted (Q-1: non-commercial, free BASS licence applies) | audio-engine.md, performance-optimization.md |
| ADR-004 | Native C++ core DLL (audio, analysis, rendering) with a C ABI; C# shell and library | **Accepted, revised** (Q-2 rejected the C#-first proposal) | solution-structure.md, build-test-release.md |
| ADR-005 | SQLite (Microsoft.Data.Sqlite + FTS5) for the library; TagLibSharp for tags | Accepted (Q-3: `source_kind` column reserved) | library-and-data.md |
| ADR-006 | SMTC is the media-key path; H.NotifyIcon for tray; WinUI-native toasts and jump list | Accepted | windows-integration.md |
| ADR-007 | CommunityToolkit.Mvvm + a single PlaybackSession store; Rx only for the analysis stream | Accepted | user-interface.md |
| ADR-008 | MVP is a local-library player; Windows Hello, cloud services, RGB lighting removed from scope | Accepted (Q-3: streaming "maybe later", Q-8: hover preview stays in 1.0) | product-scope.md |
| ADR-009 | Visualization extensibility is data-driven presets (HLSL + JSON); code plugins are post-1.0 | Accepted | extensibility-patterns.md |
| ADR-010 | Revised thread ownership: BASS owns decode/output, we own Analysis, Render, Library workers, UI | Accepted | architecture-overview.md |
| ADR-011 | Packaged MSIX, x64 only for 1.0, self-contained .NET | Accepted (Q-5: self-signed until 1.0-rc; Q-6: x64 only) | build-test-release.md |
| ADR-012 | Latency target restated as "visual reacts within one frame of audible audio", with measured compensation | Accepted | README.md, visualization-engine.md |

Product name: **Tunqio** (Q-11 on T-1, 2026-09-08). User-facing identity carries it (package, protocol `tunqio://`, data folder, alias); the native library keeps the internal name `mpcore` and the `mp_` ABI prefix. The full identity table, including what is frozen at 1.0, is [identity.md](identity.md).

---

## ADR-001: .NET 8 + Windows App SDK / WinUI 3

**Context.** README said ".NET 6+"; .NET 6 is out of support. The UI doc chooses WinUI 3 but the visualization doc still integrates with WPF.

**Decision.** .NET 8 LTS, C# 12, Windows App SDK 1.6 or later, WinUI 3 for the shell. Minimum OS Windows 10 version 2004 (build 19041), confirmed by Q-4; it is the floor for the SMTC and composition APIs we use. Windows 11 features (Mica, snap layouts) are progressive enhancements behind runtime checks.

**Consequences.** One UI framework across every doc. WPF references in visualization-engine.md and windows-integration.md are superseded (ADR-002, ADR-006). Windows 10 stays in the test matrix (E8-S6). Move to .NET 10 LTS when Windows App SDK supports it; a routine upgrade card.

---

## ADR-002: Render into a WinUI 3 SwapChainPanel

**Context.** visualization-engine.md describes D3D11 → shared texture → D3D9Ex → WPF D3DImage. user-interface.md says WinUI 3 was chosen precisely to avoid that.

**Decision.** The visualization surface is a `SwapChainPanel`. The native core (ADR-004) receives the panel's `ISwapChainPanelNative` pointer from C#, creates a D3D11 device (feature level 11_0, WARP fallback) and a flip-model composition swap chain via `IDXGIFactory2::CreateSwapChainForComposition`, and calls `SetSwapChain`. Rendering runs on a dedicated native render thread paced by the waitable swap chain object. The D3D9Ex bridge and `D3DImage` are dropped.

Win2D is **not** used for the main visualizer; it remains an option for small decorative canvases in the C# shell.

**Consequences.** One composition hop, no cross-API sync. Resize and `CompositionScaleX/Y` changes are forwarded from C# to the core, which handles `ResizeBuffers` on the render thread. Audio-reactive UI colours (not the visualizer) flow through XAML brushes and Composition animations in C#.

---

## ADR-003: BASS + bassmix + basswasapi only

**Context.** audio-engine.md layers PortAudio, libsamplerate, Intel IPP or FFTW, and hand-written WASAPI clients on top of BASS. BASS already provides every one of these. FFTW is GPL.

**Decision.** Audio stack is BASS 2.4, bassmix, basswasapi, plus format add-ons (bassflac, bassopus, basswv, bass_ape; AAC/M4A/ALAC and WMA decode through BASS's Media Foundation support, because the BASS_AAC add-on is GPL and excluded by the licence policy) loaded lazily, called directly from the native core through the BASS C API (no ManagedBass; see ADR-004). Pipeline:

1. Each track is a decoding stream: `BASS_STREAM_DECODE | BASS_SAMPLE_FLOAT | BASS_STREAM_PRESCAN`.
2. Streams are added to one mixer (`BASS_Mixer_StreamCreate(48000, 2, BASS_SAMPLE_FLOAT | BASS_STREAM_DECODE | BASS_MIXER_NONSTOP)`) with `BASS_MIXER_CHAN_NORAMPIN` and `BASS_MIXER_CHAN_BUFFER`. Mixer rate follows the output device rate in exclusive mode.
3. Output is a basswasapi device whose `WASAPIPROC` pulls from the mixer with `BASS_ChannelGetData`. BASS is initialised with the "no sound" device.
4. Gapless: `BASS_Mixer_ChannelSetSync(current, BASS_SYNC_END | BASS_SYNC_MIXTIME, ...)` fires at mix time and the pre-opened next stream is added at that position with `BASS_Mixer_StreamAddChannelEx`.
5. User crossfade (0–12 s) via `BASS_ChannelSlideAttribute(BASS_ATTRIB_VOL)`; a 50 ms guard fade on hard stops, seeks and manual skips.
6. Analysis tap: a `BASS_ChannelSetDSP` callback on the mixer copies float frames plus the mixer byte position into a lock-free ring buffer. The native analysis thread runs a real FFT (2048-point, Hann window, 512-frame hop) using **pffft** (BSD-style licence, SIMD) on that data. No IPP, no FFTW. The interim "use BASS's built-in FFT" idea from the first draft is dropped now that the core is native.

ASIO (bassasio) is post-1.0.

**Licence (Q-1).** The product is non-commercial, so BASS and its add-ons are used under the free non-commercial licence. Attribution in About; DLLs fetched by script, not committed. If distribution ever becomes commercial this ADR must be revisited before release.

---

## ADR-004: Native C++ core DLL with a C ABI (revised)

**Context.** The first draft proposed C# end to end with native code only after a measured miss. Q-2 rejected that: the audio, analysis and rendering core is C++ from day one, as the foundation documents assume.

**Decision.** The solution has two languages with one boundary:

- **`mpcore.dll`** (C++20, MSVC v143, `/W4 /permissive- /utf-8`, `/O2` in Release) owns everything the foundation docs describe in C++: the BASS-based audio engine, the DSP tap and lock-free ring buffer, analysis (pffft FFT, feature extraction, onset detection), the triple-buffered `AnalysisFrame` store, the D3D11 renderer, preset loading (nlohmann/json) and shader compilation, the quality controller, and the latency compensator. It links BASS via import libraries and loads add-ons with `BASS_PluginLoad`.
- **C ABI header `mpcore.h`**: `extern "C"` functions with `MP_API` calling convention, opaque handles (`mp_engine*`), plain-old-data structs with explicit sizes and a leading `uint32_t struct_size` for versioning, `mp_result` error codes plus `mp_last_error_message()`, and callbacks as function pointer + `void* user`. No C++ types, exceptions or STL cross the boundary. `mpcore_abi_version()` is checked by the C# side at load.
- **`Tunqio.Interop`** (C#): `[LibraryImport]` source-generated bindings over `mpcore.h`, `SafeHandle` wrappers, `[UnmanagedCallersOnly]` callback trampolines that only enqueue (a lock-free queue drained by a dedicated pump thread into `IObservable<EngineEvent>`; no managed logic runs on a native audio thread), and implementations of the C# `Core` contracts `IAudioEngine`, `IAnalysisFrameSource`, `IVisualizationHost` over the bindings.
- **C# stays**: WinUI 3 shell, view models, `PlaybackSession` and `PlayQueue` (orchestration, not real time), the SQLite library, TagLibSharp, Windows integration. The Win32 calls the shell itself needs (window handle, SMTC interop) use CsWin32.

Hot-path rules from performance-optimization.md apply as written (C++): no allocation or locking after `mp_engine_start`, `alignas(64)` atomics, pre-allocated pools, `std::atomic` with explicit memory orders.

**Rejected alternative.** C#-first with ManagedBass and Vortice.Windows (kept in the board as D-5, rejected). Reason recorded on Q-2: the maintainer wants the DSP and rendering in C++.

**Consequences.** Two toolchains and a mixed solution (`msbuild` builds the `.sln`; `dotnet build` alone cannot build `.vcxproj`). Native unit tests use Catch2 with ASan in CI. A native crash takes down the process, so `mpcore` has structured-exception guards at every export, and the shell installs a minidump writer (E8-S5). ManagedBass and Vortice.Windows leave the dependency list; pffft, nlohmann/json and Catch2 join it. Interop is its own story (E0-S9) and spike E0-S4 is written in C++ against the C ABI from the start.

---

## ADR-005: SQLite for the library, TagLibSharp for tags

**Context.** No document covered persistence.

**Decision.** One SQLite database per user profile at `%LocalAppData%\Tunqio\library.db`, accessed with Microsoft.Data.Sqlite and hand-written SQL through a small repository layer (no EF Core). Full-text search via an FTS5 virtual table with the trigram tokenizer. Numbered SQL migrations at startup. TagLibSharp (LGPL 2.1, dynamically linked) for tag read and write. Album art thumbnails are files on disk keyed by content hash. Per Q-3 ("maybe later" for streaming or cloud sources), `track` carries a `source_kind` column defaulting to `'local'` so a remote source can be added without a schema break; nothing in 1.0 reads it.

**Consequences.** Library of 100k tracks must open in under 500 ms and search under 50 ms. LGPL recorded in the licensing table.

---

## ADR-006: SMTC for media keys, H.NotifyIcon for the tray

**Context.** windows-integration.md uses WinForms `NotifyIcon`, WPF `HwndSource`, `WindowInteropHelper` and a `WM_APPCOMMAND` hook. None exist in WinUI 3.

**Decision.**
- Media keys: `SystemMediaTransportControls` via `ISystemMediaTransportControlsInterop.GetForWindow(hwnd)`. `WM_APPCOMMAND` is an unscheduled fallback card.
- Tray: H.NotifyIcon.WinUI (MIT); menu is a XAML `MenuFlyout`.
- Toasts: `Microsoft.Windows.AppNotifications`.
- Jump list: `Windows.UI.StartScreen.JumpList`.
- File associations and the protocol are declared in `Package.appxmanifest`; no registry writes. Activation through `AppInstance.GetCurrent().GetActivatedEventArgs()` with `RedirectActivationToAsync` for single instance.
- Window handle via `WinRT.Interop.WindowNative.GetWindowHandle`; Mica via `MicaBackdrop`.

---

## ADR-007: CommunityToolkit.Mvvm and a single PlaybackSession store

**Decision.** View models use CommunityToolkit.Mvvm. Playback state lives in one C# `PlaybackSession` service that owns the queue, current track, position, volume, repeat and shuffle, exposes `IObservable<PlaybackSnapshot>` and is the only caller of `IAudioEngine`. UI mode is a property on `ShellState`. Undo/redo is scoped to Curation-mode playlist edits. System.Reactive is used for the analysis stream on the C# side (theming) and nothing else.

---

## ADR-008: MVP scope is a local-library player

**Decision.** 1.0 plays local files from watched folders. Removed entirely: Windows Hello, premium features, private playlists. Deferred with interfaces reserved: Last.fm scrobbling, cloud storage import, RGB lighting, ASIO, EQ/DSP chain, lyrics. Streaming is "maybe later" (Q-3): only the `source_kind` column is added now. Hover preview in Discovery mode stays in 1.0 (Q-8), default off until the user enables it.

---

## ADR-009: Data-driven visualization presets; code plugins post-1.0

**Decision.** Visualizations in 1.0 are presets: `preset.json` plus HLSL compiled at load by the native core with `D3DCompile`; fixed constant-buffer contract. Built-in: Spectrum Bars, Waveform, Radial Spectrum, Ambient Glow. Post-1.0 code plugins would be native DLLs against a versioned C ABI (consistent with ADR-004) or .NET assemblies for non-real-time extension points such as metadata providers.

---

## ADR-010: Revised thread ownership

| Thread | Owner | Priority | Pacing | Budget |
|--------|-------|----------|--------|--------|
| WASAPI output proc | BASS (basswasapi) | Time critical (set by BASS) | Device period, 10 ms shared / 3–5 ms exclusive | Pull from mixer; DSP tap copies frames to ring buffer, no allocation |
| BASS update thread | BASS | Above normal | `BASS_CONFIG_UPDATEPERIOD` 10 ms | Decodes into channel buffers |
| Analysis | mpcore | Above normal via `AvSetMmThreadCharacteristics("Pro Audio")` | Semaphore from the tap, 512-frame hop (93.75 Hz at 48 kHz) | < 4 ms per hop; publishes to triple buffer |
| Render | mpcore | Normal via `AvSetMmThreadCharacteristics("Games")` | Waitable swap chain, display refresh | < 6 ms CPU |
| Event pump | Interop (C#) | Normal | Drains the native event channel | Marshals to `PlaybackSession` |
| Library workers | .NET ThreadPool via `Parallel.ForEachAsync` | Below normal | Demand driven | Bounded degree = max(2, cores/2) |
| UI | WinUI dispatcher | Normal | Input and layout | Never blocks on the engine, database or file system |

CPU affinity pinning is a diagnostic toggle only.

---

## ADR-011: Packaged MSIX, x64, self-contained

**Decision.** Single-project MSIX, self-contained .NET runtime, x64 only (Q-6). `mpcore.dll`, BASS and add-ons ship inside the package. Sideload installer produced by CI; Store submission is 1.1. Per Q-5, releases are **self-signed until 1.0-rc**; the README documents trusting the certificate. Publisher identity is set with the name Tunqio and must not change afterwards. ARM64 is not planned.

---

## ADR-012: Latency target restated

**Decision.** *A transient audible at time T is reflected in the presented frame whose scan-out begins no later than T + one display refresh interval.* The native analysis thread stamps each frame with the mixer byte position; the renderer compares against `BASS_WASAPI_GetInfo` latency plus DXGI frame statistics and picks the frame that will be audible when shown. `tools/LatencyHarness` measures p50/p95/p99. Target: p95 within one refresh, p99 within two.
