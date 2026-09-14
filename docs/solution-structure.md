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
  THIRD-PARTY-NOTICES.md           Every third-party component the MSIX ships, with licences; vendored sources with SHA-256.
                                   Shipped as licenses/THIRD-PARTY-NOTICES.md and parsed by the About page (E6-S5)
  README.md                        User install and data locations; developer clone-to-run steps (T-86)
  assets/brand/                    tunqio-icon.svg and icon-assets.json, the source of every icon (tools/IconGen, T-191)
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
  presets/                         Built-in visualization presets (preset.json + HLSL). Copied to presets/ beside
                                   mpcore.dll in the app output and into the MSIX by Content items in
                                   Tunqio.App.csproj; tools/check-presets.ps1 fails a build that skipped it.
                                   Holds ADR-009's four: spectrum-bars and waveform (E4-S4),
                                   radial-spectrum and ambient-glow (E4-S5).
  tests/
    Tunqio.Core.Tests/
    Tunqio.Interop.Tests/     Round-trips every ABI call against the real mpcore with the BASS "no sound" device
    Tunqio.Library.Tests/     In-memory SQLite + fixture library
    Tunqio.App.Tests/         View-model and shell-logic tests over fakes, icon asset parsing. As built: no UI automation
                              smoke here; the on-screen checks are the UIA harnesses tools/check-*.ps1 (below)
    Tunqio.Benchmarks/        BenchmarkDotNet gates run with --gate: search, library open, upsert batch, curation,
                              preset switch ([Budget] attributes; build-test-release.md, "Performance verification")
    fixtures/
  tools/
    LatencyRunner/                 C# console over Interop; measures audio-to-picture latency (ADR-012). Written as
                                   "LatencyHarness" in the original plan; decisions.md, ADR-012 "As built"
    SoakRunner/                    C# console over Interop; 24 h playback soak with dropout logging
    FixtureGen/                    Generates the fixture library and 100k database
    IconGen/                       Renders assets/brand/tunqio-icon.svg into every icon asset; build-time only, not shipped
    fetch-native.ps1               Downloads BASS packages into native/bass/ with hash verification (tools/native-deps.json)
    fetch-ffmpeg.ps1               Pinned ffmpeg for the tag-writer tests (tools/ffmpeg-dep.json)
    build.ps1                      Two-step local build (MSBuild.exe for native, dotnet for managed) with -Test
    check-format.ps1               clang-format --dry-run --Werror over native/ (-Fix rewrites)
    check-asan.ps1                 Runs the tagged use-after-free test on the ASan build and requires ASan to report it
    check-presets.ps1              Fails when a built app has no presets/ beside mpcore.dll; ignores MPCORE_PRESET_ROOT
    check-package.ps1              Engine, BASS, licences and icons in the unpackaged output and inside the .msix (T-128)
    check-*.ps1 (the rest)         UI Automation harnesses over the running shell on a scratch --data-root
    uia-geometry.ps1               Shared UIA readers, resizer and Close-TunqioShell for those harnesses
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
- Every export is `extern "C" MP_API mp_result mp_xxx(...)`, `__cdecl`. Handles are opaque pointers (`mp_engine`, `mp_track`, `mp_renderer`). Structs are POD with `uint32_t struct_size` first; a caller whose size is smaller than the callee's is served the prefix that size covers and a larger one is refused, so fields can be appended later (ABI 0.12; the rule lives in `src/abi/struct_size.h` and every export goes through it).
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
mp_result mp_renderer_enum_preset_params(mp_renderer*, const char* id, mp_preset_param_info*, uint32_t*);
                                                                           // label, range, step, unit, choices, hidden
mp_result mp_renderer_set_user_preset_root(mp_renderer*, const char* path); // a second root, scanned as well
mp_result mp_renderer_rescan_presets(mp_renderer*, uint32_t* count);       // the Settings > Visualization refresh
mp_result mp_renderer_set_preset(mp_renderer*, const char* id);
mp_result mp_renderer_set_param(mp_renderer*, const char* name, float);
mp_result mp_renderer_set_theme(mp_renderer*, const mp_theme_colors*);     // the shell's palette, into every preset's b0
mp_result mp_renderer_set_quality(mp_renderer*, mp_quality_policy);      // auto = the render-thread controller (E4-S7)
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

- `Core` references nothing but the BCL, System.Reactive and Microsoft.Extensions.Logging.Abstractions, and targets plain `net8.0`.
- `Interop` is the only C# project with `DllImport`/`LibraryImport` of `mpcore`; nothing else names the DLL.
- `Library` references `Core` and its own NuGet packages; it never touches the engine.
- `App` is the only project that references WinUI, Windows App SDK and H.NotifyIcon, and the only place with `DispatcherQueue` calls. As built: CsWin32 was never adopted; the shell's few Win32 calls (user32, kernel32) are hand-written `DllImport`s in `Activation/NativeWindowing.cs` and beside the code that needs them.
- Inside `mpcore`, `render/` may include `analysis/` headers (frame store); `audio/` and `analysis/` never include `render/`, and only `audio/` includes BASS headers. Enforced by include-path layout and `native/mpcore.tests/src/test_architecture.cpp` (tag `[architecture]`), which greps includes.
- Managed rules are enforced twice: `TunqioLayeringGuard` in `Directory.Build.targets` fails the build on a forbidden `ProjectReference`, and `ArchitectureTests` (NetArchTest) in `Core.Tests` fails when `Core` depends on the layers above it, on Windows or on SQLite/TagLib types.

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
1. App constructor: Serilog bootstrap logger, unhandled exception handlers. (As built, E8-S5: the minidump writer is not
   registered here. `CrashReporter.Install()` runs in `OnLaunched`, once the data root and `diagnostics.crashReporting`
   are known, and registers nothing while that setting is off.)
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

Activation while running (file, protocol, jump list, toast button) lands in `OnActivated` on the main instance and is routed to `PlaybackSession` via a `CommandRouter` that understands `play <paths>`, `enqueue <paths>`, `playlist <id>`, `track <path>`, `toggle`, `next`, `previous`. As built (E7-S5, T-78), the jump list's two are `tunqio://track?id=<track id>` and `tunqio://playlist?id=<playlist id>` in the router's URI grammar, by id rather than path (windows-integration.md, "As built (E7-S5)"); the jump list controller starts after the window is shown and is the first thing shutdown stops.

**As built (E7-S1, T-74).** Step 2a runs before the XAML app exists, not in `OnLaunched`:

- `Program.Main` (`Tunqio.App/Program.cs`) is the entry point; the generated one is off (`DISABLE_XAML_GENERATED_MAIN`).
  It reads this launch's activation (`AppInstance.GetCurrent().GetActivatedEventArgs()`: the files or URI of a packaged
  activation, else the command line) and calls `AppInstance.FindOrRegisterForKey` with the **data root's** key, not
  `"main"`: `InstanceKey` hashes the normalised full path of `--data-root`, or of `%LocalAppData%\Tunqio` without it. So
  there is one Tunqio per profile, and a harness on a scratch `--data-root` is always a separate instance that can never
  redirect into the app Phil is using.
- **Another spelling of Tunqio.exe (T-192):** before the key, an unpackaged launch whose own path differs from the file
  system's spelling (letter case, as COM's lowercase LocalServer32 path for a toast press, a shortcut or a script) starts
  Tunqio.exe again from the true spelling with the same arguments, passes the foreground on and exits with 0. AppInstance
  hashes the exact module path into every key's scope, so without it that launch would not find the running instance. The
  relaunched process carries `TUNQIO_CANONICAL_RELAUNCH` and never relaunches again. A packaged launch is left alone.
- **Not the current instance:** it passes the foreground on (`AllowSetForegroundWindow` to the running process), calls
  `RedirectActivationToAsync` on the thread pool with a 10 s cap, writes one line to the profile's log, and returns 0
  without starting XAML.
- **The current instance:** it subscribes `AppInstance.Activated`, which queues each redirected activation's tokens in
  `Program.Inbox`, and starts the app as the generated `Main` did. Once the window is shown, `App.StartActivationRouting`
  (steps 2d and 3e) runs this launch's own tokens through `CommandRouter`, then attaches the inbox, whose activations are
  each routed on the XAML thread. A redirected launch's arguments arrive as one string (unpackaged, with the program name
  first); `CommandLineText` splits it by the C runtime's rules.
- The measurement modes (`--library-spike`, `--render-spike`, `--nowplaying-spike`, `--shell-spike`) neither register nor
  redirect. A key that cannot be registered at all starts the app unshared rather than not at all.
- The router's commands and what it refuses are in [windows-integration.md](windows-integration.md), "Protocol
  activation". The playlist, track and toast verbs above arrive with E7-S3 and E7-S4.
- Proven unpackaged by `tools/check-single-instance.ps1`: on a scratch root, second processes with a file, with
  `tunqio://queue`, `toggle` and `next`, with 50 paths, and with `tunqio://play?path=` each exit with code 0 while root A
  keeps one process and one window that plays, pauses, skips, holds a 50-item queue and replaces it again; a launch on a second scratch root gets its own window.

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
- Native crash: the SEH guards convert what they can; a genuine access violation triggers the minidump writer (E8-S5). The soak and interop tests exist to make this rare. As built: the minidump writer exists since E8-S5 (T-84) and runs only when crash reporting is switched on; see "As built (E8-S5, T-84)" below. A fault inside an mpcore export never reaches it, because that export's SEH guard converts the fault; it catches faults on the core's own unguarded threads and in managed code.
- Every fire-and-forget task goes through a helper that logs. As built: it is `Forget(this Task, string what)` in `Tunqio.App/Controls/LibraryActions.cs`, not a class named `SafeFireAndForget`.

**As built (E8-S5, T-84).** Crash reporting is opt-in (`diagnostics.crashReporting`, off by default) and **local-only**:
Q-123, answered by Phil on 2026-09-14, keeps every report on the machine. Nothing is uploaded and there is no upload address
setting; a user who wants to send a report keeps it and sends an Export diagnostics zip themselves.

- **Registration is in `OnLaunched`, not the App constructor** (startup step 1 above): the data root the reports go under and
  the opt-in are only known once the settings are open, so `CrashReporter.Install()` runs right after the launch line is
  logged. With the setting off nothing is registered and nothing is captured, beyond what Windows itself does.
- **Managed crashes.** App's XAML handler (when the exception is not handled, because XAML then ends the process with a
  stowed-exception fail-fast that no filter sees) and its AppDomain handler (when terminating) call
  `CrashReporter.CaptureUnhandled` after logging (T-188). Unobserved task exceptions are only logged: in .NET 8 they do not end
  the process, so there is no crash to report.
- **Native crashes.** A top-level filter (`SetUnhandledExceptionFilter`), registered when the setting is on at launch or is
  turned on later. It leaves the CLR's own code (0xE0434352) to the AppDomain handler, and always hands on to the filter
  registered before it, so the runtime and Windows still do what they would have. A fault inside an mpcore export on a managed
  caller's thread never reaches it: the export's SEH guard has already turned it into `MP_E_INTERNAL`. What it catches is a fault
  on a thread with no guard, which for the core means its own audio, analysis and render threads.
- **What is written**, once per process, into `<data root>\crashes\<yyyyMMdd-HHmmss-fff>-<pid>\`: `tunqio.dmp` through
  `MiniDumpWriteDump` from inside the process (no WER LocalDumps keys, nothing in the registry), with `MiniDumpNormal |
  WithUnloadedModules | WithProcessThreadData | WithThreadInfo` and no heap, so a dump is a few megabytes; its size is logged.
  Then `log.txt`, the session's last 200 log lines from `CrashLogBuffer`, a Serilog sink beside the file sink that formats each
  event with the file's template, so the handler copies strings instead of reading the shared log back. Then `report.json`
  (source, exception type and message, stack, native code, time, session, version, dump size) last, so a folder without it is
  a report the crash cut short; it is still listed. The handler catches everything and never throws.
- **Next launch.** `Crash/CrashReportViewModel` and `CrashReportDialog`, after the first-run welcome (one ContentDialog at a
  time). With the setting on, each report nobody has answered is offered once, newest first, at most three per launch. The
  dialog says in plain words what was captured: the exception, the log lines in a read-only box, the dump's size and location,
  and that a dump can hold file paths and track names; and that nothing leaves the PC unless the user sends it. Keep report
  writes a `kept` marker; Delete report (declining) removes the folder; Esc keeps. With the setting off no report is offered,
  and any that exist stay on disk.
- **Export.** `DiagnosticsExport` copies each kept report under `crashes/<report>/` in the zip: its text files redacted like the
  logs, the dump as it is. The About page switch ("Save crash reports on this PC") says exactly this.
- **Test switch.** `--crash-test managed|xaml|native` counts only with `TUNQIO_CRASH_TEST=1` in the environment and a
  `--data-root`. The process calls `SetErrorMode` on itself so no crash box reaches the desktop. `native` calls
  `mp_debug_crash` (ABI 0.20), which faults on a thread inside mpcore.dll. It is the one export no guard covers, and it exists
  because nothing else can crash inside the core on purpose.
- **Proof.** `Tunqio.App.Tests/CrashReportTests` (store, buffer, opt-in rule with a real dump of the test host, switch rule,
  dialog keep/delete/once, export); `tools/check-crash-report.ps1` forces each crash on a scratch root, finds the report, sees
  the dialog by UIA, declines or keeps, exports, and repeats with the setting off.

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
| `ui.hoverPreviewOffered` | bool | false; true once the first-hover offer has been made or the user has used the switch (E5-S5, Q-74) |
| `ui.welcomeShown` | bool | absent on a fresh profile; written once when the first-run welcome is decided: true when shown, false for a profile that predates it (E6-S6) |
| `ui.reactiveTheming` | bool | true |
| `ui.reactiveSmoothing` | float 0..1 | 0.15 |
| `ui.closeToTray` / `ui.minimizeToTray` | bool | false / false |
| `ui.toastOnTrackChange` | bool | false |
| `ui.albumsSort` | `title` \| `artist` \| `year` \| `added` \| `played` | `title` |
| `ui.tracksHiddenColumns` | comma-separated `TrackColumn` names | empty |
| `viz.preset` | string | `ambient-glow` (E5-S3, Q-72: one preset for every mode) |
| `viz.quality` | `auto` \| `low` \| `medium` \| `high` | `auto` |
| `viz.params.<preset>.<name>` | float | preset default |
| `viz.temporalSmoothing` | bool | false (T-184: an envelope delays a transient, so it is opted into) |
| `viz.temporalAttackMs` | float 0..250 | 20 |
| `viz.temporalDecayMs` | float 0..2000 | 300 |
| `diagnostics.crashReporting` | bool | false |
| `shortcuts.<action>` | string, a `KeyChord` (`Ctrl+Alt+P`); `""` unbinds | absent: the table's default (E6-S4; `<action>` is `ShellShortcuts.ActionId`, e.g. `playPause`, `seekBack30`, `volumeUp`) |

## Coding conventions

**C++ (`mpcore`)**: C++20, `/W4 /WX /permissive- /utf-8 /Zc:__cplusplus`, `/analyze` in CI, clang-format (LLVM base, 4-space indent). No exceptions across the ABI; internally, exceptions only during construction. RAII wrappers for every BASS and COM handle (`bass_handle`, `com_ptr`). Real-time code: no `new`, no locks, no logging, no `std::string`; asserted by a `RT_ASSERT_NO_ALLOC` debug hook that patches `operator new` during callbacks in Debug builds. Public functions in `abi/` are the only non-`namespace mp` symbols.

**C#**: nullable on, `TreatWarningsAsErrors`, analyzers at `Recommended`, `Microsoft.VisualStudio.Threading.Analyzers`. No `Task.Run` in view models. `Interop` uses `LibraryImport` with `StringMarshalling.Utf8`, `SafeHandle` for every native handle, and `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` trampolines that do nothing but enqueue for the dedicated pump thread (a `BlockingCollection` over a `ConcurrentQueue`; the pump publishes to `IObservable<EngineEvent>`).
