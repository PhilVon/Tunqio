# Product Scope

This document is the authoritative statement of what the 1.0 release does, who it is for, and what is deliberately left out. Every backlog item traces to a feature here.

## Name

**Tunqio** (decided on T-1, Q-11). Package identity, the `tunqio://` protocol, the data folder and the execution alias all carry it; every identifier and its freeze status is listed in [identity.md](identity.md).

## Vision

A Windows desktop player for people who own their music. It should feel instant, look alive, and never glitch. The visualizer and the audio-reactive interface are the reason to choose it; gapless bit-perfect playback and a fast library are the reasons to keep it.

## Target users

| Persona | Library | What they value | Mode they live in |
|---------|---------|-----------------|-------------------|
| **The collector** | 20k–150k local files, mixed FLAC/MP3, carefully tagged | Fast browse, correct metadata, gapless albums, exclusive-mode output | Curation, Focus |
| **The ambient listener** | 2k–10k files, plays albums start to finish while working | Low-distraction Focus mode, tray control, media keys, pleasant visuals | Focus |
| **The explorer** | Growing library, discovers by album art | Visual browsing, hover previews, quick queueing | Discovery |

Not a target: streaming-service subscribers with no local files, DJs needing beat-matching, users on Windows 8.1 or earlier.

## Release tiers

### 1.0 (MVP) — must ship

**Playback**
- Play, pause, stop, next, previous, seek (click and drag on a progress bar with hover time tooltip), volume with mute, playback speed unchanged.
- Formats: MP3, FLAC, WAV, AIFF, AAC/M4A/ALAC, OGG Vorbis, Opus, WMA, WavPack, APE. Cue sheets are not in 1.0.
- Gapless playback (default on) and optional user crossfade 0–12 s. Sample-accurate for WAV, AIFF, FLAC, ALAC, WavPack, MP3 (LAME/Xing delay and padding applied), AAC (priming and padding from the file's edit list or `iTunSMPB`, trimmed by the engine), Ogg Vorbis and Opus, also when the source rate differs from the output. Best-effort for WMA (the decoder drops the last 1 792 frames; no gapless metadata exists for it); APE is unmeasured, because no encoder exists to build a fixture pair with. Measured in [spikes/e1-s2-gapless-join.md](spikes/e1-s2-gapless-join.md).
- Shared-mode WASAPI by default; exclusive mode as an opt-in setting with device selection, and automatic recovery when the device disappears.
- ReplayGain (track and album) read from tags and applied; no scanning of untagged files in 1.0.
- Play queue: play now, play next, add to queue, remove, reorder by drag, clear, save queue as playlist. Shuffle (off / on) and repeat (off / all / one). Queue and position are restored on relaunch.
- Resume last track at last position on launch (opt-out setting).

**Library**
- Watched folders (add, remove, rescan). Incremental scan on launch and live updates via file-system watcher.
- Metadata from tags: title, artist(s), album, album artist, track and disc numbers, year, genre(s), composer, duration, bitrate, sample rate, channels, ReplayGain, embedded art, MusicBrainz IDs if present.
- Album art from embedded pictures or folder images; cached thumbnails at three sizes.
- Browse by Albums (grid), Artists, Tracks, Genres, Folders, Recently added, Recently played, Most played.
- Search across title, artist, album with instant results as you type (FTS5).
- Sort and filter per view; multi-select; context menu actions (play, play next, add to queue, add to playlist, show in folder, properties).
- Playlists: create, rename, delete, reorder, add and remove tracks; import and export M3U8; smart playlists are post-1.0.
- Basic tag editing (title, artist, album, album artist, year, genre, track/disc number) for single tracks and multi-select batches, written back to files with a confirmation and undo within the session.
- Play history and play counts.

**Visualization and audio-reactive UI**
- Real-time analysis pipeline producing spectrum (1024 bins), waveform (512 samples), RMS, spectral centroid, six octave bands, onset flag.
- Built-in presets: Spectrum Bars, Waveform, Radial Spectrum, Ambient Glow. Switchable from Now Playing; per-preset parameters exposed in Settings.
- Audio-reactive theming: background gradient and accent derived from album art palette blended with live spectral colour, with a smoothing control and an off switch. Honours the system reduced-motion and high-contrast settings by disabling reactivity.
- Adaptive quality: render resolution and update rate step down when frame time exceeds budget, step up when headroom returns.

**Interface**
- Three-panel shell (Now Playing / contextual sidebar / persistent controls) with compact and medium breakpoints.
- Three modes: Discovery, Focus, Curation, switchable via a segmented control and keyboard shortcuts.
- Mini player: a small always-on-top window with art, title, transport and progress.
- Light, dark and system themes; Mica backdrop on Windows 11.
- Keyboard shortcuts for every transport action and view; shortcut reference in Settings.
- Accessibility: full keyboard navigation, Narrator-readable names on all controls, focus visuals, minimum 4.5:1 text contrast in both themes, reduced-motion respected.
- Settings: Playback, Output, Library, Appearance, Visualization, Shortcuts, About and diagnostics.

**Windows integration**
- File type associations and "Open with"; multi-file and folder drop onto the window or queue.
- Single instance: opening a file from Explorer while running plays it in the existing window.
- System Media Transport Controls with art, title, artist, album, position and playback status.
- Tray icon with transport menu, minimise-to-tray and close-to-tray options.
- Toast notification on track change (opt-in) with transport buttons.
- Jump list with recent tracks and pinned playlists.
- `tunqio://` protocol for play/queue commands.

**Quality**
- No audible dropouts in a 24-hour soak test at shared-mode default buffer on the reference machine.
- Cold start to interactive under 1.5 s; library of 100k tracks opens under 500 ms; search under 50 ms.
- Memory under 200 MB idle with a 10k library, under 500 MB with 100k.
- Crash reporting (opt-in) and a diagnostics export.

### 1.1 — planned next

- Smart playlists with rule builder.
- Equaliser (10-band) and DSP chain with presets.
- ReplayGain scanning of untagged files (EBU R128).
- Lyrics from embedded tags and .lrc sidecars, synced display.
- Cue sheet support.
- Folder-based library view improvements and "rescan this folder".
- Last.fm scrobbling.
- Microsoft Store distribution.

### Later / unscheduled

- MPC (Musepack) playback: needs un4seen's `bass_mpc` add-on, one more dependency to ship, attribute and keep licensed for a format that is rare in 2026 (Q-26 on T-88).
- ASIO output (bassasio, separate licence).
- Visualization code plugins via the .NET SDK (ADR-009).
- Online metadata lookup (MusicBrainz, cover art archive).
- Cloud storage import (OneDrive local sync folders already work as watched folders).
- RGB lighting integration.
- ARM64 build.
- Portable (unpackaged, self-contained) distribution.

### Explicitly out of scope

- Streaming-service integration (Spotify, Apple Music and similar).
- Windows Hello, accounts, premium tiers, private playlists.
- Video playback.
- Mobile or cross-platform builds.
- Ripping, burning, format conversion.

## Success metrics for 1.0

| Metric | Target | How measured |
|--------|--------|--------------|
| Audio dropouts | 0 in 24 h soak, shared mode, reference machine | `BASS_WASAPI_GetInfo` underrun counter logged by soak harness |
| Visual latency | p95 within one display refresh of audible audio | Latency harness (ADR-012) |
| Frame rate | 60 fps sustained on reference iGPU at 1080p with Spectrum Bars | DXGI frame statistics telemetry |
| Cold start | < 1.5 s to first interactive frame | ETW trace, `App.OnLaunched` to first `Loaded` |
| Library open | < 500 ms for 100k tracks | Benchmark fixture database |
| Search | < 50 ms p95 for 3-character query on 100k | Benchmark fixture database |
| Crash-free sessions | > 99.5% | Crash reporter (opt-in) |

**Reference machine:** 4-core / 8-thread laptop CPU from 2020 or later, integrated GPU, 16 GB RAM, NVMe SSD, Windows 11 23H2, built-in Realtek audio at 48 kHz.

## Guiding constraints

1. Nothing on the audio path allocates or blocks after startup.
2. The UI thread never waits on the engine, the database or the file system.
3. Every feature works with the visualizer disabled.
4. Every feature works from the keyboard alone.
5. The library is a cache of the files; deleting the database and rescanning must lose nothing except play history and playlists, which are therefore stored separately and exported automatically.
