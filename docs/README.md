# Tunqio - Technical Architecture Documentation

A high-performance Windows desktop music player application focused on real-time audio-visual integration and modern user experience patterns.

## Overview

This music player differentiates itself through seamless real-time audio visualization, professional-grade audio processing, and adaptive user interfaces that respond dynamically to audio content. The architecture prioritizes tight audio-to-visual latency (visuals react within one display refresh of audible audio) while maintaining glitch-free audio playback across diverse system configurations.

## Key Technical Features

- **Real-time Audio-Visual Integration**: Visuals react within one display refresh of audible audio, with measured look-ahead compensation (ADR-012)
- **Native C++ Core**: `mpcore.dll` owns the BASS-based audio engine, the lock-free analysis tap and FFT, and the D3D11 renderer behind a versioned C ABI; gapless, sample-accurate track joins
- **Hardware-Accelerated Visualization**: native D3D11 rendering into a WinUI 3 `SwapChainPanel` on a dedicated render thread, data-driven HLSL presets
- **Adaptive User Interface**: WinUI 3 with audio-reactive theming and three modes (Discovery, Focus, Curation)
- **Professional Audio Output**: Shared and exclusive WASAPI with device-change recovery; ASIO post-1.0
- **Fast Library**: SQLite + FTS5, incremental scanning, live folder watching, art cache with palette extraction

## Target Audience

This documentation serves senior software engineers and application architects with experience in:
- Windows desktop application development (.NET, WinUI 3, native C++ interop)
- Audio programming and multimedia frameworks
- Real-time graphics rendering and performance optimization
- Modern UI frameworks and design patterns

## Documentation Structure

Read in this order. The **Design** documents are current; the **Foundation** documents came first and carry banners where a decision in `decisions.md` supersedes a section.

### Design (current)

| Document | Purpose |
|----------|---------|
| [Decisions (ADRs)](decisions.md) | Resolved contradictions and the decisions everything else depends on; all accepted on T-1 (2026-09-08) |
| [Product Scope](product-scope.md) | Personas, 1.0 / 1.1 / later feature tiers, out-of-scope list, success metrics |
| [Product Identity](identity.md) | Every string that names the product: package identity, publisher, alias, `tunqio://` scheme and commands, data folder, title and tooltip formats, what is frozen at 1.0 |
| [Solution Structure](solution-structure.md) | Projects, the native/managed boundary and C ABI, dependency rules, key interfaces, DI, startup/shutdown, settings keys |
| [Library and Data Model](library-and-data.md) | SQLite schema, repositories, scanner pipeline, art cache, queue model, history, durability |
| [UI Screens and Flows](ui-screens-and-flows.md) | Navigation map, mode definitions, screen inventory, ten acceptance flows, shortcuts, accessibility |
| [Build, Test and Release](build-test-release.md) | Toolchain, test strategy per layer, performance harnesses, CI, packaging, licences, Definition of Done |
| [Risks and Open Questions](risks-and-open-questions.md) | Answered questions with where each landed; risk register with mitigation cards |
| [Roadmap and Backlog](roadmap-and-backlog.md) | Milestones M0–M5, epics E0–E8, 77 sized stories with acceptance criteria, transcribed to the kanban |
| [Spikes](spikes/) | Measured findings from the technical spikes: [E0-S4 BASS hello world](spikes/e0-s4-bass-hello.md) (latencies, flag set, position-tracking surprises) |

### Foundation (original, partially superseded)

| Document | Purpose | Superseded sections |
|----------|---------|---------------------|
| [Architecture Overview](architecture-overview.md) | High-level system design and component relationships | Thread model (ADR-010), technology stack table (ADR-001/003/004) |
| [Audio Engine](audio-engine.md) | Audio pipeline, buffering, gapless, output modes | PortAudio/libsamplerate/IPP layers and hand-written WASAPI clients (ADR-003); the custom crossfade buffer (mix-time sync instead) |
| [Visualization Engine](visualization-engine.md) | Analysis pipeline, feature extraction, shaders, quality scaling | WPF D3DImage/D3D9Ex bridge (ADR-002); latency target (ADR-012) |
| [User Interface](user-interface.md) | WinUI 3 rationale, layout, modes, reactive theming | ReactiveUI + Redux state stack (ADR-007); RGB lighting (ADR-008) |
| [Windows Integration](windows-integration.md) | File associations, tray, media keys, SMTC, toasts, jump list | WinForms/WPF API usage and registry writes (ADR-006); Windows Hello (ADR-008) |
| [Performance Optimization](performance-optimization.md) | Memory, threading, SIMD, GPU, adaptive quality | Intel IPP FFT (ADR-003); CPU affinity by default (ADR-010) |
| [Extensibility Patterns](extensibility-patterns.md) | Plugin interfaces and management | C++ vtable plugin ABI and cloud service plugins (ADR-008/009); presets replace code plugins for 1.0 |

## Quick Start Requirements

### Development Environment
- **Platform**: Windows 10 version 2004 (build 19041) or later; Windows 11 recommended
- **IDE**: Visual Studio 2026 with the Desktop development with C++, .NET desktop development and WinUI application development workloads
- **Framework**: .NET 8 LTS, Windows App SDK 1.8, WinUI 3, C# 12 for the shell and library; C++20 (MSVC v145, Windows SDK 10.0.26100) for `mpcore.dll`

### Dependencies
- **Audio**: BASS 2.4 with bassmix, basswasapi and format add-ons, called from the native core (free non-commercial licence, OQ-1)
- **Visualization**: Direct3D 11 / DXGI in the native core; presets are HLSL + JSON
- **UI**: WinUI 3 with CommunityToolkit.Mvvm
- **Library**: Microsoft.Data.Sqlite (FTS5), TagLibSharp
- **Analysis**: pffft (BSD) inside the native core; features published through a triple buffer and one ABI call

### Performance Targets
- **Audio**: zero dropouts in a 24-hour shared-mode soak on the reference machine
- **Visualization**: p95 within one display refresh of audible audio; 60 fps at 1080p on the reference iGPU
- **UI**: cold start under 1.5 s; 100k-track library opens under 500 ms; search under 50 ms
- **Memory**: under 200 MB with a 10k library, under 500 MB with 100k

## Architecture Principles

1. **Real-time Performance**: Nothing on the audio path allocates or blocks after startup
2. **Modular Design**: one native/managed boundary (`mpcore.h`); C# `Core` knows nothing of BASS, D3D, SQLite or WinUI; each subsystem is behind an interface in `Core`
3. **Progressive Disclosure**: UI complexity scales with mode and user expertise
4. **Hardware Optimization**: GPU rendering and multi-core scanning where measured to help; adaptive quality when they do not
5. **Extensibility**: Data-driven visualization presets now; a .NET plugin SDK later
6. **The library is a cache**: deleting the database and rescanning loses nothing the user made

## Getting Started

Read [Decisions](decisions.md) first, then [Product Scope](product-scope.md), then the foundation documents for depth on the subsystem you are implementing. Planning starts from [Roadmap and Backlog](roadmap-and-backlog.md); open questions that block cards are in [Risks and Open Questions](risks-and-open-questions.md).
