# Architecture Overview

> **Status: foundation document, partially superseded.** The four-thread model is replaced by the ownership table in ADR-010 (BASS owns decode and output; the app owns Analysis, Render, Library workers and UI). The technology stack table is replaced by ADR-001, ADR-003 and ADR-004 (.NET 8 / WinUI 3 shell, BASS add-ons only, native C++ core DLL with a C ABI). Layering and data-flow descriptions remain valid. See [decisions.md](decisions.md) for the record and [README.md](README.md) for the current reading order.

> **What shipped (T-86, 2026-09-14).** The statements below that no longer match the code, and where the real thing is:
>
> - **Processes and modules.** One process: the WinUI 3 shell `Tunqio.exe` (`src/Tunqio.App`) over three managed
>   libraries (`Tunqio.Core`, `Tunqio.Interop`, `Tunqio.Library`) and the native `mpcore.dll`, which owns BASS, the
>   analysis thread and the D3D11 renderer. See [solution-structure.md](solution-structure.md).
> - **Interop is not C++/CLI.** The managed side calls `mpcore.dll`'s C ABI (`native/mpcore/include/mpcore.h`)
>   through `LibraryImport` in `Tunqio.Interop`; native events arrive through callback trampolines that only enqueue
>   for a pump thread ([solution-structure.md](solution-structure.md), "The native/managed boundary").
> - **No ReactiveUI and no Redux-style store.** View models use CommunityToolkit.Mvvm; `PlaybackSession` in
>   `Tunqio.Core` is the single owner of playback state and publishes `IObservable<PlaybackSnapshot>` (ADR-007).
> - **Threads.** BASS owns decode and WASAPI output; `mpcore` owns the analysis thread (pffft, publishing through a
>   triple buffer at 93.75 Hz) and the render thread; the app owns the UI thread, the event pump and library workers
>   (ADR-010). There is no format-conversion thread and no hand-written ring buffer between decode and output; the
>   `AudioBuffer` sample and the buffer sizes below are the original sketch.
> - **Visualization surface.** The renderer draws into a composition swap chain on a WinUI 3 `SwapChainPanel`, not a
>   shared texture ([spikes/e0-s5-swapchainpanel-render.md](spikes/e0-s5-swapchainpanel-render.md); E4-S3 "As built"
>   in [solution-structure.md](solution-structure.md)). Presets are data (`presets/`, `preset.json` + HLSL), not code.
> - **Analysis.** pffft (vendored), not Intel IPP or FFTW3; the frame layout is `mp_analysis_frame` in `mpcore.h`.
> - **Latency.** The visual target is ADR-012's "p95 within one display refresh of audible audio", measured by
>   `tools/LatencyRunner` (decisions.md, ADR-012 "As built"; [spikes/e4-s8-latency-floor.md](spikes/e4-s8-latency-floor.md)).
>   The latency table below predates it.
> - **Extensibility.** Built-in and user visualization presets only (ADR-009); no DSP, codec-plugin or metadata-provider
>   interfaces shipped. BASS add-ons are the fixed set in `tools/native-deps.json`, loaded at engine creation.
> - **Technology stack.** .NET 8 (`net8.0-windows`, built with the .NET 10 SDK for C# 14), WinUI 3 / Windows App SDK 1.8,
>   BASS 2.4, D3D11, pffft; no WPF, no DirectSound fallback. The table at the end of this page is superseded.

The Windows music player employs a layered architecture optimized for real-time audio-visual performance. The design centers on four core subsystems that operate concurrently while maintaining strict timing guarantees.

## System Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                    User Interface Layer (WinUI 3)               │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐ │
│  │  Now Playing    │  │  Contextual     │  │  Persistent     │ │
│  │  Focus Area     │  │  Sidebar        │  │  Controls       │ │
│  │     (60%)       │  │     (25%)       │  │     (15%)       │ │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│               Visualization Engine (D3D11)                      │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐ │
│  │  Spectrum       │  │  Waveform       │  │  Audio-Reactive │ │
│  │  Analysis       │  │  Renderer       │  │  UI Elements    │ │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│                Audio Processing Pipeline                        │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐ │
│  │  Format         │  │  Playback       │  │  Real-time      │ │
│  │  Conversion     │  │  State Machine  │  │  Analysis       │ │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────┐
│               Audio Engine (BASS.dll Foundation)                │
│  ┌─────────────────┐  ┌─────────────────┐  ┌─────────────────┐ │
│  │  Codec          │  │  Output         │  │  Buffer         │ │
│  │  Management     │  │  Management     │  │  Management     │ │
│  └─────────────────┘  └─────────────────┘  └─────────────────┘ │
└─────────────────────────────────────────────────────────────────┘
```

## Core Architectural Components

### 1. Audio Engine Foundation

The audio engine uses **BASS.dll** as its foundation, providing robust codec support and low-level audio management. This layer handles:

- **Codec Detection and Loading**: Lazy-loaded BASS plugins based on file format detection
- **Output Device Management**: WASAPI exclusive/shared mode with ASIO fallback
- **Buffer Management**: Lock-free circular buffers with cache-line alignment
- **Thread Coordination**: Four dedicated threads with appropriate priority levels

### 2. Audio Processing Pipeline

Built atop the audio engine, this layer provides:

- **Format Normalization**: All audio converted to 32-bit float at consistent sample rates
- **Real-time Analysis**: FFT processing for visualization data extraction
- **Crossfade Processing**: Gapless playback with sample-accurate timing
- **Dynamic Range Processing**: Optional compression and EQ with preset management

### 3. Visualization Engine

The visualization engine integrates D3D11 rendering with audio analysis:

- **Spectral Analysis**: 1024-sample FFT windows with 50% overlap at 86Hz update rate
- **GPU-Accelerated Rendering**: Hardware-accelerated shaders for real-time effects
- **DXGI Integration**: Surface sharing between D3D11 and WinUI 3 via shared textures
- **Audio-Reactive UI**: Dynamic color mapping based on spectral characteristics

### 4. User Interface Layer

WinUI 3-based interface with responsive design patterns:

- **Adaptive Layout**: Three-panel design that scales across window sizes
- **Mode-Based Navigation**: Discovery, Focus, and Curation workflow modes
- **Progressive Disclosure**: Complexity scaling based on user expertise
- **State Management**: Redux-inspired patterns with ReactiveUI command handling

## Threading Model

> **Superseded (ADR-010).** The four threads below are the original sketch; what shipped is in the "What shipped" note at the top of this page.

The application employs a **four-thread architecture** optimized for real-time performance:

### Thread 1: Audio Callback (THREAD_PRIORITY_TIME_CRITICAL)
- **Purpose**: Hardware buffer management and audio streaming
- **Timing**: Runs at hardware intervals (~10-20ms)
- **Constraints**: Zero allocations, minimal processing
- **Responsibility**: Write decoded audio to primary circular buffer

### Thread 2: Analysis Processing (THREAD_PRIORITY_ABOVE_NORMAL)
- **Purpose**: FFT analysis and feature extraction
- **Timing**: Processes at 86Hz (11.6ms intervals)
- **Responsibility**: Read from primary buffer, perform spectral analysis, write to visualization buffer

### Thread 3: Format Conversion (THREAD_PRIORITY_NORMAL)
- **Purpose**: Sample rate conversion and crossfade preparation
- **Timing**: Asynchronous, demand-driven
- **Responsibility**: Prepare next track buffers, handle format transitions

### Thread 4: UI Thread (THREAD_PRIORITY_NORMAL)
- **Purpose**: User interface updates and event handling
- **Timing**: 60Hz update cycle (16.7ms intervals)
- **Responsibility**: Consume visualization data, update D3D11 uniforms, handle user input

## Memory Management Strategy

### Lock-Free Buffer Architecture

The system uses **lock-free ring buffers** to eliminate blocking between threads:

```cpp
struct AudioBuffer {
    alignas(64) std::atomic<uint32_t> writePos;  // Cache line aligned
    alignas(64) std::atomic<uint32_t> readPos;   // Separate cache line
    float samples[BUFFER_SIZE];                  // Audio data
};
```

### Buffer Sizing Strategy

- **Primary Audio Buffer**: 4MB (85ms at 48kHz stereo, 32-bit float)
- **Analysis Buffer**: 1MB (21ms with 1024-sample windows)
- **Hardware Buffer**: 480-960 samples (10-20ms, system-dependent)
- **Crossfade Buffer**: 2400 samples (50ms at 48kHz)

## Inter-Component Communication

### Audio → Visualization Data Flow

1. **Audio Callback Thread** writes decoded samples to primary ring buffer
2. **Analysis Thread** reads samples via atomic pointers, performs FFT
3. **Analysis Thread** extracts spectral features (centroid, RMS, harmonic ratio)
4. **Analysis Thread** writes visualization data to secondary ring buffer
5. **UI Thread** reads visualization data at 60Hz with interpolation

### UI → Audio Command Flow

> **Superseded (ADR-007, ADR-004).** Commands go from CommunityToolkit.Mvvm view models to `PlaybackSession`, which calls `IAudioEngine`, implemented in `Tunqio.Interop` over `mpcore.dll`'s C ABI; there is no ReactiveUI, Redux store or C++/CLI.

1. **UI Thread** dispatches commands through ReactiveUI command patterns
2. **Commands** route through centralized state manager with Redux-like patterns
3. **State Manager** invokes audio engine methods via C++/CLI interop
4. **Audio Engine** processes commands on appropriate threads with priority handling

## Performance Guarantees

### Latency Targets

| Component | Target Latency | Measurement Point |
|-----------|---------------|-------------------|
| Audio Pipeline | <20ms | Input to hardware output |
| Visualization | <10ms | Audio sample to visual update |
| UI Response | <16.7ms | User input to visual feedback |
| Format Switching | <100ms | Track boundary transitions |

### Throughput Requirements

| Resource | Target Utilization | Fallback Strategy |
|----------|-------------------|-------------------|
| CPU (Audio) | <25% single core | Reduce analysis resolution |
| CPU (Visualization) | <30% single core | Skip frames if needed |
| GPU Memory | <100MB | Reduce texture resolution |
| System Memory | <500MB | Stream large files |

## Extensibility Points

The architecture provides several extension mechanisms:

1. **Audio Format Support**: BASS plugin loading system for new codec support
2. **Visualization Effects**: D3D11 shader system for custom visual effects
3. **UI Themes**: WinUI 3 resource dictionary system for appearance customization
4. **DSP Processing**: Plugin interface for real-time audio effects
5. **Metadata Sources**: Pluggable metadata providers for enhanced library information

## Technology Stack Summary

> **Superseded (ADR-001, ADR-003, ADR-004).** No WPF, DirectSound, D3D12, Intel IPP or FFTW3 ships, and the platform is .NET 8 with a native C++ core; see "What shipped" at the top of this page.

| Layer | Primary Technology | Secondary Options |
|-------|-------------------|-------------------|
| UI Framework | WinUI 3 | WPF (legacy support) |
| Audio Engine | BASS.dll | DirectSound (fallback) |
| Graphics | D3D11 | D3D12 (future upgrade) |
| Analysis | Intel IPP | FFTW3 |
| Platform | .NET 6+ | Native C++ (performance critical) |

This architecture provides the foundation for implementing a high-performance music player that balances real-time constraints with modern user experience expectations.