# Solution Structure and Module Boundaries

This document defines the projects, the native/managed boundary, the interfaces between layers, dependency rules, and how the app starts up and shuts down. Decisions: ADR-004 (native C++ core with a C ABI), ADR-007 (state management), ADR-010 (threads).

## Repository layout

```
tunqio/
  Tunqio.sln                  Mixed solution: .vcxproj + .csproj. Build with msbuild (not `dotnet build`).
  Tunqio.Managed.slnf              Solution filter of the .csproj projects, for `dotnet build/test/format` (tools/build.ps1)
  Directory.Build.props            Version and toolchain pins; C#: TFM net8.0-windows10.0.19041.0, nullable, warnings as errors, analyzers
  Directory.Build.targets          Layering guard: fails the build on a forbidden ProjectReference (InitialTargets)
  Directory.Packages.props         Central NuGet versions (Windows App SDK pinned here)
  global.json                      .NET SDK pin
  .editorconfig                    C# style and naming; C++ indentation
  THIRD-PARTY-NOTICES.md           Vendored components with versions, licences and SHA-256
  artifacts/                       All build output (bin/, obj/, native/<Config>/<Platform>/, msix/); not committed
  native/
    mpcore/                        C++20 DLL project (mpcore.vcxproj)
      include/mpcore.h             The C ABI. Only header the managed side reads.
      src/audio/                   BASS engine: streams, mixer, WASAPI output, gapless, DSP tap, devices
      src/analysis/                tap.h (E1-S8 hop staging), analyzer (pffft FFT and the analysis thread), features, onset
      src/render/                  D3D11 device, composition swap chain, preset loader, shaders, quality, latency compensator
      src/abi/                     extern "C" exports, SEH guards, error strings, ABI version
      src/common/                  Lock-free primitives, thread utilities (AvSetMmThreadCharacteristics), logging sink
    mpcore.tests/                  Catch2 v3 unit and integration tests (mpcore.tests.vcxproj); compiles mpcore sources in (MP_STATIC); ASan configuration
      abi_stub/                    mpcore_abi_stub.dll reporting ABI major + 1, for the interop loader's refusal test
    spikes/bass_hello/             E0-S4 console spike over the ABI (needs an output device; not run in CI)
    tunqio.native.props            Shared C++ compiler/linker settings (/W4 /WX, C++20, per-configuration CRT)
    tunqio.native.targets          Copies the BASS runtime next to every native binary
    .clang-format                  LLVM base, 4 spaces, 120 columns
    third_party/                   pffft (BSD), nlohmann/json (MIT), Catch2 (Boost): vendored sources, hash-pinned in THIRD-PARTY-NOTICES.md
    bass/                          BASS + add-on headers, import libs and DLLs (x64). Fetched by tools/fetch-native.ps1, not committed.
  src/
    Tunqio.Core/              C#: PlayQueue, PlaybackSession, DTOs, contracts (IAudioEngine, IAnalysisFrameSource, IVisualizationHost, ILibraryService). No I/O, no Windows.
    Tunqio.Interop/           C#: LibraryImport bindings for mpcore.h, SafeHandles, callback trampolines, event pump; implements the Core contracts over mpcore.
    Tunqio.Library/           C#: SQLite repositories, scanner, TagLibSharp reader/writer, art cache, playlists, history.
    Tunqio.App/               C#: WinUI 3 shell, views, view models, Windows integration, DI wiring, MSIX manifest.
  presets/                         Built-in visualization presets (preset.json + HLSL), copied to output.
  tests/
    Tunqio.Core.Tests/
    Tunqio.Interop.Tests/     Round-trips every ABI call against the real mpcore with the BASS "no sound" device
    Tunqio.Library.Tests/     In-memory SQLite + fixture library
    Tunqio.App.Tests/         View-model tests; UI automation smoke (tagged)
    Tunqio.Benchmarks/        BenchmarkDotNet: query builder, search, interop call overhead
    fixtures/
  tools/
    LatencyHarness/                C# console over Interop; measures audio-to-frame latency (ADR-012)
    SoakRunner/                    C# console over Interop; 24 h playback soak with dropout logging
    FixtureGen/                    Generates the fixture library and 100k database
    fetch-native.ps1               Downloads BASS packages into native/bass/ with hash verification
    build.ps1                      Two-step local build (MSBuild.exe for native, dotnet for managed) with -Test
    check-format.ps1               clang-format --dry-run --Werror over native/ (-Fix rewrites)
    check-asan.ps1                 Runs the tagged use-after-free test on the ASan build and requires ASan to report it
  docs/
  .github/workflows/
```

## The native/managed boundary

```
┌──────────────────────── C# ─────────────────────────┐        ┌────────────── C++ (mpcore.dll) ──────────────┐
│ App (WinUI 3) ──► Core (PlaybackSession, PlayQueue)  │        │ abi/  ──► audio/  (BASS, mixer, WASAPI, tap)   │
│      │                 ▲                             │ C ABI  │       ──► analysis/ (ring, pffft, features)    │
│      └──► Library      │                             │◄──────►│       ──► render/  (D3D11, presets, quality)   │
│                 Interop (bindings, event pump) ──────┘        │ common/ (lock-free, threads, logging)          │
└──────────────────────────────────────────────────────┘        └───────────────────────────────────────────────┘
```

**ABI rules** (`native/mpcore/include/mpcore.h`):
- Every export is `extern "C" MP_API mp_result mp_xxx(...)`, `__cdecl`. Handles are opaque pointers (`mp_engine`, `mp_track`, `mp_renderer`). Structs are POD with `uint32_t struct_size` first; the callee rejects unknown sizes so fields can be appended later.
- Strings are UTF-8 `const char*` in, and out via caller-supplied buffers with length. Paths are converted to UTF-16 inside the core for Win32 and BASS (`BASS_UNICODE`).
- Errors: `mp_result` enum (`MP_OK`, `MP_E_INVALID_ARG`, `MP_E_BASS`, `MP_E_DEVICE`, `MP_E_D3D`, `MP_E_STATE`, `MP_E_INTERNAL`) and `mp_last_error(char* buf, size_t len)` per thread. No exceptions cross; every export is wrapped in `__try/__except` plus `catch(...)` that converts to `MP_E_INTERNAL` and logs.
- Callbacks: `typedef void (MP_CALL *mp_event_cb)(const mp_event* ev, void* user)`. Invoked on native threads; the managed trampoline must only enqueue. Callbacks are never invoked after `mp_engine_destroy` returns (the core drains and joins first).
- Threads: the core creates and owns Analysis and Render threads. The managed side owns nothing native except the event pump thread.
- Versioning: `mpcore_abi_version()` returns `MAJOR<<16 | MINOR`; Interop refuses to load on a major mismatch.

**Export surface** (abridged; full list is the header):

```c
uint32_t  mpcore_abi_version(void);
mp_result mp_engine_create(const mp_engine_config*, mp_engine**);          // BASS no-sound init, mixer, plugin loading
mp_result mp_engine_destroy(mp_engine*);
mp_result mp_engine_set_output(mp_engine*, const mp_output_config*);      // device id, shared|exclusive, buffer ms
mp_result mp_engine_enum_devices(mp_engine*, mp_device_info* out, uint32_t* count);
mp_result mp_engine_set_event_callback(mp_engine*, mp_event_cb, void* user);
mp_result mp_track_open(mp_engine*, const char* utf8_path, mp_track**);   // decode stream, prescan, gapless info
mp_result mp_track_close(mp_track*);
mp_result mp_track_get_info(mp_track*, mp_track_info*);                     // duration, rate, channels, bits, codec
mp_result mp_engine_play(mp_engine*, mp_track*, int64_t start_ms);
mp_result mp_engine_preload_next(mp_engine*, mp_track*);                   // NULL clears; the gapless join
mp_result mp_engine_preload_next_ex(mp_engine*, mp_track*, mp_join_mode);  // MP_JOIN_GAPLESS | MP_JOIN_CROSSFADE, chosen per boundary by the caller
mp_result mp_engine_pause(mp_engine*); mp_result mp_engine_resume(mp_engine*);
mp_result mp_engine_stop(mp_engine*, mp_fade_mode);
mp_result mp_engine_seek(mp_engine*, int64_t position_ms);
mp_result mp_engine_set_volume(mp_engine*, float linear);
mp_result mp_track_set_replaygain(mp_track*, float gain_db, float peak); // per track: tag gain + preamp, tagged peak (<= 0 unknown); limited to 1/peak; exact at a gapless join
mp_result mp_engine_set_crossfade(mp_engine*, uint32_t ms);                // 0 = off, clamped to 12 000; equal-power, per-source envelope
mp_result mp_engine_get_clock(mp_engine*, mp_clock*);                      // position ms, mixer byte pos, output latency ms; lock-free
mp_result mp_engine_get_stats(mp_engine*, mp_engine_stats*);               // underruns, callback max µs
mp_result mp_preview_start(mp_engine*, mp_track*, float gain_db);          // Discovery hover preview (E5-S5)
mp_result mp_preview_stop(mp_engine*);
mp_result mp_analysis_try_get_latest(mp_engine*, mp_analysis_frame*);      // copies the triple-buffer front; returns MP_E_STATE if none
mp_result mp_renderer_create(mp_engine*, void* swap_chain_panel_native, const mp_renderer_config*, mp_renderer**);
mp_result mp_renderer_destroy(mp_renderer*);
mp_result mp_renderer_resize(mp_renderer*, uint32_t w, uint32_t h, float scale_x, float scale_y);
mp_result mp_renderer_set_visible(mp_renderer*, bool);                     // stops the render loop when hidden
mp_result mp_renderer_enum_presets(mp_renderer*, mp_preset_info*, uint32_t*);
mp_result mp_renderer_set_preset(mp_renderer*, const char* id);
mp_result mp_renderer_set_param(mp_renderer*, const char* name, float);
mp_result mp_renderer_set_theme(mp_renderer*, const mp_theme_colors*);     // art palette / accent for presets
mp_result mp_renderer_set_quality(mp_renderer*, mp_quality_policy);
mp_result mp_renderer_get_stats(mp_renderer*, mp_render_stats*);
mp_result mp_log_set_sink(mp_log_cb, void* user, mp_log_level);
```

`mp_analysis_frame` is a fixed-size POD (1024 spectrum floats, 512 waveform floats, scalars, `mixer_byte_pos`, `qpc_ticks`) so the managed copy is one `memcpy` into a pinned struct; the C# theming consumer samples it at 30 Hz. The renderer reads the triple buffer in-process and never crosses the boundary per frame.

## Dependency rules

```
App ──► Interop ──► Core
 │
 └────► Library ──► Core
Interop ──► mpcore.dll (native, via LibraryImport)
```

- `Core` references nothing but the BCL and System.Reactive.
- `Interop` is the only C# project with `DllImport`/`LibraryImport` of `mpcore`; nothing else names the DLL.
- `Library` references `Core` and its own NuGet packages; it never touches the engine.
- `App` is the only project that references WinUI, Windows App SDK, H.NotifyIcon and CsWin32, and the only place with `DispatcherQueue` calls.
- Inside `mpcore`, `render/` may include `analysis/` headers (frame store); `audio/` and `analysis/` never include `render/`. Enforced by include-path layout and a Catch2 test that greps includes.
- Managed rules are enforced by a NetArchTest test in `Core.Tests`.

## Key managed contracts (in Core)

```csharp
public interface IAudioEngine : IAsyncDisposable
{
    Task InitializeAsync(OutputConfig config, CancellationToken ct);
    Task<TrackHandle> OpenAsync(string path, CancellationToken ct);
    Task PlayAsync(TrackHandle track, TimeSpan? startAt, CancellationToken ct);
    Task PreloadNextAsync(TrackHandle? next, CancellationToken ct);
    Task PauseAsync(); Task ResumeAsync(); Task StopAsync(FadeMode fade);
    Task SeekAsync(TimeSpan position);
    void SetVolume(float linear); void SetReplayGain(TrackHandle track, float gainDb, float peak);  // ReplayGainPolicy.Resolve gives the pair
    void SetCrossfade(TimeSpan duration);
    Task StartPreviewAsync(TrackHandle track, float gainDb); Task StopPreviewAsync();
    IObservable<EngineEvent> Events { get; }   // TrackStarted, TrackEnded(natural), DeviceLost, DeviceChanged, Underrun, Error
    PlaybackClock Clock { get; }               // reads mp_engine_get_clock; lock-free
    IReadOnlyList<OutputDevice> EnumerateDevices();
}

public sealed class PlaybackSession   // single owner of playback state; only caller of IAudioEngine
{
    IObservable<PlaybackSnapshot> Snapshots { get; }
    Task PlayNowAsync(IEnumerable<long> trackIds); Task PlayNextAsync(...); Task EnqueueAsync(...);
    Task TogglePlayPauseAsync(); Task NextAsync(); Task PreviousAsync();
    Task SeekAsync(TimeSpan); void SetVolume(float); void SetShuffle(bool); void SetRepeat(RepeatMode);
    Task RemoveFromQueueAsync(Guid); Task MoveInQueueAsync(Guid, int);
    Task RestoreAsync(QueueState saved); QueueState Capture();
}

public readonly record struct AnalysisFrame(uint Sequence, long MixerBytePosition, long TimestampTicks,
    ReadOnlyMemory<float> Spectrum, ReadOnlyMemory<float> Waveform,
    float Rms, float Peak, float SpectralCentroidHz, float HarmonicRatio, ReadOnlyMemory<float> Bands, bool Onset);
// E4-S1 as built: `Sequence` is how a poller knows what it holds is new (the native side publishes at 93.75 Hz
// and every consumer samples more slowly), and `Bands` is the same shape as Spectrum and Waveform rather than a
// named OctaveBands type - E4-S2 is what fills it, and it should be the story that names its shape.

public interface IAnalysisFrameSource
{
    bool TryGetLatest(out AnalysisFrame frame);     // mp_analysis_try_get_latest
    IObservable<AnalysisFrame> Frames { get; }       // polled at 30 Hz for UI theming
}

public interface IVisualizationHost : IDisposable
{
    Task AttachAsync(nint swapChainPanelNative, RendererConfig config);   // the panel's IUnknown, not the panel
    void Detach(); void Resize(int width, int height, float scaleX, float scaleY); void SetVisible(bool visible);
    bool IsAttached { get; }
    IReadOnlyList<PresetInfo> Presets { get; }  string? ActivePresetId { get; }
    Task SetPresetAsync(string id); void SetParameter(string name, float value);
    void SetThemeColors(ThemeColors colors); void SetQualityPolicy(QualityPolicy policy);
    IObservable<RenderStats> Stats { get; }
}
// E4-S3 as built. The sketch here took the SwapChainPanel itself and had Interop extract ISwapChainPanelNative,
// but the dependency rule above forbids Interop from referencing WinUI and something had to give: the shell holds
// the WinUI reference and already knows how to produce the pointer, so it produces it. AttachAsync also takes the
// RendererConfig, because the renderer is created here and needs the panel's pixel size and composition scale.
// SetPresetAsync throws PresetCompilationException carrying the shader compiler's diagnostic; the preset that was
// running is still running when it does.

public interface ILibraryService { ITrackRepository Tracks { get; } IAlbumRepository Albums { get; } IArtistRepository Artists { get; } IGenreRepository Genres { get; } ILibraryFolderRepository Folders { get; } ILibraryScanner Scanner { get; } ILibraryWatcher Watcher { get; } }
```

Everything above is mockable; view models are tested against fakes, and `Interop.Tests` proves the real implementations against `mpcore`.

## Dependency injection and composition

`Microsoft.Extensions.DependencyInjection` with the generic host. Services are registered per project by an extension method (`services.AddNativeEngine()`, `AddLibrary()`, ...). Lifetimes:

| Service | Lifetime | Notes |
|---------|----------|-------|
| `IAudioEngine`, `PlaybackSession`, `IAnalysisFrameSource`, `IVisualizationHost`, `ILibraryService`, `ShellState`, `ISettingsStore` | Singleton | Created eagerly at startup in the order below |
| Repositories | Singleton (connection pooled inside) | |
| `LibraryScanCoordinator`, `ILibraryFolderPicker`, `LibraryNavigator` | Singleton | The shell's scan triggers and status (E3-S12); the coordinator takes the XAML thread's `SynchronizationContext` at registration |
| View models | Transient; Shell VM singleton | |
| Windows integration (SMTC, tray, toasts, jump list) | Singleton `IHostedService` | Start after window is shown |

Logging: Serilog through `Microsoft.Extensions.Logging` for C#; `mpcore` logs through `mp_log_set_sink`, which Interop forwards into Serilog with a `native` source tag. No logging call sits on the WASAPI proc or DSP path.

## Startup sequence

```
1. App constructor: Serilog bootstrap logger, unhandled exception handlers, minidump writer registration.
2. OnLaunched:
   a. Single-instance check: AppInstance.FindOrRegisterForKey("main"); redirect and exit if not current.
   b. Build host; open SQLite (apply migrations); load settings.
   c. Create MainWindow, apply theme/backdrop, restore placement, Activate().          ← first frame target < 1.5 s
   d. Parse activation args into a pending command.
3. Post-first-frame (DispatcherQueue low priority):
   a. Interop loads mpcore.dll, checks ABI version, mp_engine_create with saved OutputConfig; fall back to default shared device on failure and surface an InfoBar.
   b. Start the event pump thread.
   c. Attach the renderer to the Now Playing SwapChainPanel (only when visible).
   d. Restore queue_state; if "resume on launch", open the current track paused at the saved position.
   e. Execute the pending activation command.
   f. Start hosted services: SMTC, tray, jump list, toasts; start the library watcher (today: right after Activate()).
   g. After 3 s idle: incremental library scan.
```

Activation while running (file, protocol, jump list, toast button) lands in `OnActivated` on the main instance and is routed to `PlaybackSession` via a `CommandRouter` that understands `play <paths>`, `enqueue <paths>`, `playlist <id>`, `track <path>`, `toggle`, `next`, `previous`.

## Shutdown sequence

1. Capture `QueueState` and window placement; persist.
2. `PlaybackSession.StopAsync(FadeMode.Guard)` with a 500 ms cap.
3. `mp_renderer_destroy` (joins the render thread), then `mp_engine_destroy` (joins analysis, frees WASAPI and BASS, drains callbacks).
4. Stop the event pump; cancel any running scan (batches are transactional).
5. Flush logs.

Close-to-tray, when enabled, only hides the window.

## Error handling policy

- Native errors return `mp_result`; Interop converts them to `EngineEvent.Error` with the message from `mp_last_error`, never to exceptions in view models.
- Transient errors: `InfoBar` at the top of the sidebar, auto-dismiss 8 s. Persistent (device missing, folder offline): sticky `InfoBar` with an action.
- Native crash: the SEH guards convert what they can; a genuine access violation triggers the minidump writer (E8-S5). The soak and interop tests exist to make this rare.
- Every `async void` event handler goes through a `SafeFireAndForget` helper that logs.

## Settings keys (JSON values in `settings.json`; Q-15 kept the file over the `setting` table)

| Key | Type | Default |
|-----|------|---------|
| `output.deviceId` | string | default device |
| `output.mode` | `shared` \| `exclusive` | `shared` |
| `output.bufferMs` | int | 40 shared / 10 exclusive |
| `playback.gapless` | bool | true |
| `playback.crossfadeMs` | int | 0 |
| `playback.replayGain` | `off` \| `track` \| `album` | `album` |
| `playback.replayGainPreampDb` | float | 0 |
| `playback.resumeOnLaunch` | bool | true |
| `library.splitArtists` | bool | true |
| `library.writeRatingsToFiles` | bool | false (Q-7) |
| `ui.theme` | `system` \| `light` \| `dark` | `system` |
| `ui.mode` | `discovery` \| `focus` \| `curation` | `discovery` |
| `ui.hoverPreview` | bool | false until enabled (Q-8, R-15) |
| `ui.reactiveTheming` | bool | true |
| `ui.reactiveSmoothing` | float 0..1 | 0.15 |
| `ui.closeToTray` / `ui.minimizeToTray` | bool | false / false |
| `ui.toastOnTrackChange` | bool | false |
| `ui.albumsSort` | `title` \| `artist` \| `year` \| `added` \| `played` | `title` |
| `ui.tracksHiddenColumns` | comma-separated `TrackColumn` names | empty |
| `viz.preset` | string | `spectrum-bars` |
| `viz.quality` | `auto` \| `low` \| `medium` \| `high` | `auto` |
| `viz.params.<preset>.<name>` | float | preset default |
| `diagnostics.crashReporting` | bool | false |
| `shortcuts.<action>` | string | see ui-screens-and-flows.md |

## Coding conventions

**C++ (`mpcore`)**: C++20, `/W4 /WX /permissive- /utf-8 /Zc:__cplusplus`, `/analyze` in CI, clang-format (LLVM base, 4-space indent). No exceptions across the ABI; internally, exceptions only during construction. RAII wrappers for every BASS and COM handle (`bass_handle`, `com_ptr`). Real-time code: no `new`, no locks, no logging, no `std::string`; asserted by a `RT_ASSERT_NO_ALLOC` debug hook that patches `operator new` during callbacks in Debug builds. Public functions in `abi/` are the only non-`namespace mp` symbols.

**C#**: nullable on, `TreatWarningsAsErrors`, analyzers at `Recommended`, `Microsoft.VisualStudio.Threading.Analyzers`. No `Task.Run` in view models. `Interop` uses `LibraryImport` with `StringMarshalling.Utf8`, `SafeHandle` for every native handle, and `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` trampolines that do nothing but enqueue for the dedicated pump thread (a `BlockingCollection` over a `ConcurrentQueue`; the pump publishes to `IObservable<EngineEvent>`).
