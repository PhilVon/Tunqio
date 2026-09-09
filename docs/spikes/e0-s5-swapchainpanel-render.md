# Spike E0-S5: SwapChainPanel render thread

Card T-15. Run on 2026-09-09 on the development machine (Windows 10 Pro 19045, NVIDIA GeForce RTX 4080 SUPER,
144 Hz display at 100 % scale). The code is the first cut of the real renderer, not a throwaway:
`native/mpcore/src/render/renderer.cpp` behind `mp_renderer_*` in `mpcore.h` 0.3, `Tunqio.Interop.NativeRenderer`,
and the `SwapChainPanel` in `MainWindow.xaml`. The measurement harness is the app itself:

```
Tunqio.exe --render-spike [--seconds N] [--resizes N] [--warp] [--out FILE]
```

It sizes the client area to 1920 × 1080, renders the 64-bar scene for N seconds (steady phase), then performs N
window resizes at 40 ms intervals through six sizes, settles for two seconds and writes the renderer statistics
as JSON. Exit code 0 requires no device loss, no missed refresh during the steady phase and ≥ 59 fps.

## Results

| Run | Adapter | Steady fps (10 s / 8 s) | Missed refreshes, steady | Frame max, steady | Resizes applied | Device lost | Frame histogram after the storm (< 8.4, < 16.7, < 20, < 33.4, < 50, ≥ 50 ms) |
|-----|---------|-------------------------|--------------------------|-------------------|-----------------|-------------|-----------------------------------------------------------------------------|
| Hardware | RTX 4080 SUPER | **144.1** (display refresh) | **0** | 8.98 ms | 101 / 100 requested | no | 2681, 11, 0, 0, 0, 0 |
| WARP (`--warp`) | Microsoft Basic Render Driver | **144.0** | **0** | 18.4 ms | 101 / 100 requested | no | 2388, 18, 1, 0, 0, 0 |

Every frame interval in both runs was under 20 ms; the 11 and 18 intervals between 8.4 and 16.7 ms fall inside
the resize storm. `DxgiMissedRefreshes` over the whole run (233 on hardware) is dominated by the storm: DWM
holds frames while the window is being resized, which is expected and not a rendering stall. The steady phase
counted zero.

**The reference iGPU figure is still open.** The development machine has a discrete GPU; AC-19 asks for the
reference iGPU, which needs the reference machine. WARP on this 24-thread CPU rendering the 64-bar scene at
1920 × 1061 sustained the display rate, so the scene is far from the limit of even a software rasteriser; the
iGPU run is a confirmation, not a risk.

## What the spike proved

- **C# to native handoff of the panel.** The shell passes `((IWinRTObject)panel).NativeObject.ThisPtr` (the
  panel's IUnknown) through `mp_renderer_create`; the core queries WinUI 3's `ISwapChainPanelNative`
  (IID `63aad0b8-7c24-40ff-85a8-640d944cc325`, declared locally in `swapchainpanel_native.h` so mpcore does not
  depend on the NuGet include path) and calls `SetSwapChain` on the calling (UI) thread. No CsWinRT projection
  or COM interop code in C#.
- **Composition swap chain + waitable object.** `CreateSwapChainForComposition` with
  `DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL`, two `B8G8R8A8_UNORM` buffers, `DXGI_ALPHA_MODE_IGNORE`,
  `DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT`, `SetMaximumFrameLatency(1)`. The render thread blocks on
  the waitable object, renders, presents with sync interval 1. Frame pacing follows the display exactly.
- **Resize on the render thread.** The shell forwards `SizeChanged` and `CompositionScaleChanged` as pixel size
  plus scale; the render thread applies the latest pending request before the next frame (`ResizeBuffers`,
  new RTV, `SetMatrixTransform` with the inverse composition scale). 100 requests coalesce into whatever the
  thread manages to apply (101 here, including the initial one); no tearing artefacts were seen and no device
  loss occurred. The headless tests toggle the scale between 1.0 and 1.5 on every resize to cover the DPI
  transform path; a real monitor DPI switch is a manual check (AC-174-style, on the card).
- **WARP fallback.** `D3D11CreateDevice(HARDWARE)` failure falls back to `WARP` automatically (`force_warp`
  forces it). WARP ran the scene at display rate here; the ≥ 30 fps question in AC-22 is answered yes for this
  scene and this CPU, and the E4 presets will need re-measuring on WARP as they get heavier.
- **Headless mode for tests.** `headless = 1` renders into an offscreen texture with no swap chain, so the
  renderer runs on CI (WARP, no display): creation, frames, a 100-resize storm, visibility pause/resume and the
  stubbed preset exports are covered by `mpcore.tests` (`[render]`) and `Tunqio.Interop.Tests`.
- **Statistics come from the render thread without locks:** atomics for counters, a six-bucket frame-time
  histogram from QPC deltas, and `IDXGISwapChain::GetFrameStatistics` for present count and missed refreshes.

## Surprises

1. `AppWindow.ResizeClient(1920, 1080)` yields a 1920 × 1061 panel: WinUI reserves the caption band inside the
   client area when the default title bar is in use. For the harness this does not matter; E2-S1 should size
   windows through the content, not the client rectangle, or extend content into the title bar.
2. `GetFrameStatistics` returns `DXGI_ERROR_FRAME_STATISTICS_DISJOINT` for the first frames after creation and
   after some resizes; the renderer ignores those samples rather than counting them as misses.
3. Runtime `D3DCompile` of the two shaders takes a few milliseconds at start-up; fine for presets loaded once,
   and it means the preset loader (E4-S3) can reuse this path unchanged.
4. Presenting from a thread other than the UI thread is entirely fine with a composition swap chain; only
   `SetSwapChain` needs the UI thread.

## ABI 0.3 surface

Implemented: `mp_renderer_create`, `mp_renderer_destroy`, `mp_renderer_resize`, `mp_renderer_set_visible`,
`mp_renderer_get_stats`. Declared and stubbed (return `MP_E_STATE` naming the story): `mp_renderer_enum_presets`,
`mp_renderer_set_preset`, `mp_renderer_set_param` (E4-S3), `mp_renderer_set_theme` (E4-S6),
`mp_renderer_set_quality` (E4-S7). `mp_renderer_create` takes the engine handle for E4-S1's analysis stream and
accepts NULL until then.
