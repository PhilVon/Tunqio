# UI Screens, Navigation and Flows

Companion to user-interface.md, which covers the framework and layout mechanics. This document inventories every screen, defines the three modes precisely, lists the primary user flows with their acceptance behaviour, and fixes the keyboard and accessibility contract. Decisions: ADR-007 (state), ADR-008 (scope).

## Navigation map

```
MainWindow (Shell)
├── Now Playing panel (always present; content varies by mode)
│     ├── Visualizer surface (SwapChainPanel)  ── preset switcher flyout
│     ├── Album art + metadata block
│     └── Lyrics placeholder (1.1)
├── Sidebar (contextual; collapsed in Focus)
│     ├── Library ▸ Albums | Artists | Tracks | Genres | Folders | Recently added | Recently played | Most played
│     ├── Album detail / Artist detail / Genre detail / Folder detail (push navigation within sidebar)
│     ├── Playlists ▸ list ▸ Playlist detail
│     ├── Queue (also available as a flyout from the controls panel)
│     ├── Search results (overlays the current sidebar page while the search box has text)
│     └── Curation editor (dual pane; Curation mode only)
├── Controls panel (persistent; auto-hide in Focus)
│     ├── Transport, progress, volume, shuffle, repeat
│     ├── Mode switcher (Discovery | Focus | Curation)
│     └── Queue button, Settings button, Mini player button
├── Settings (full-panel overlay, replaces sidebar + Now Playing metadata; visualizer keeps running behind)
│     Playback | Output | Library | Appearance | Visualization | Shortcuts | About & Diagnostics
└── Dialogs: Add folder, Tag editor, Playlist name, Confirm batch edit, First-run welcome
MiniPlayerWindow (separate always-on-top window)
Tray icon + flyout
```

Navigation inside the sidebar uses a `Frame` with a back stack (Alt+Left). The Now Playing panel never navigates; it reflects `PlaybackSession`.

## Modes, defined precisely

| | Discovery | Focus | Curation |
|---|---|---|---|
| Intent | Browse and sample | Listen | Organise |
| Now Playing width | 60% | 100% | 40% |
| Sidebar | Library grid views, default Albums | Hidden; swipe/hover from left edge peeks the queue | Dual pane: source list (library/playlist) left, target playlist right |
| Controls | Visible | Auto-hide after 3 s idle; reappear on pointer move or any key | Visible, plus batch action bar when selection > 1 |
| Visualizer | Ambient Glow behind art by default | Full-bleed, current preset, art dimmed | Minimal (waveform strip under art) |
| Hover preview | On: 500 ms hover on an album tile starts a 15 s preview at −12 dB through a **second mixer channel**; main playback ducks by 6 dB; leaving the tile fades the preview in 200 ms | Off | Off |
| Reactive theming | On | On, stronger (saturation floor 0.3) | Reduced (lightness only) |
| Keyboard | 1 | 2 | 3 (Ctrl+1/2/3) |
| Entered from | default; Esc from Focus | double-click art, F11, or mode switcher | mode switcher, or "Edit playlist" action |

Mode is persisted. Switching modes never interrupts playback. Transition animations are Composition implicit animations of 200–300 ms and are skipped when reduced motion is on.

**Hover preview mechanics (Discovery).** Preview uses a separate decode stream added to the mixer at −12 dB with a 200 ms ramp, starting at 30% into the track (or 0 if under 60 s). It is bypassed for the analysis tap so the visualizer keeps following the main track. Only one preview at a time; moving between tiles queues the next after the current fade-out.

## Screen inventory

Each screen lists what it shows, primary actions, empty state, and the data contract it binds to.

### Shell / Now Playing
- Shows: art (up to 1000 px), title, artists (clickable), album (clickable), year, format badge (FLAC 24/96), progress with elapsed/remaining toggle, visualizer.
- Actions: preset switcher, toggle art/visualizer emphasis, love/rate (1–5 stars), show in folder, tag editor.
- Empty: "Nothing playing" with Add folder / Open files buttons on first run.
- Binds: `PlaybackSnapshot`, `TrackDto`, `AnalysisFrame` (theming), `IVisualizationHost`.

### Library › Albums
- Grid of 300 px art tiles (virtualised `ItemsRepeater` with `UniformGridLayout`), title, album artist, year. Sort: title, artist, year, added, played. Filter chips: genre, decade, format.
- Actions on tile: play, play next, enqueue, add to playlist, open detail. Hover preview in Discovery.
- Empty: prompt to add folders; during first scan shows progress inline.
- Binds: `IAlbumRepository.ListAsync` via keyset paging.

### Library › Artists
- List with 96 px circle art (first album art), name, album and track counts. Alphabet jump bar. Detail shows albums grid plus "Appears on".

### Library › Tracks
- Virtualised `ListView` table: number, title, artist, album, duration, format, plays, rating. Column chooser. Multi-select with Shift/Ctrl. Sort by any column.
- Binds: `ITrackRepository.StreamAsync` with `TrackQuery`.

### Library › Genres, Folders, Recently added, Recently played, Most played
- Genres: tag cloud sized by count → filtered Tracks view. Folders: tree of library folders → Tracks filtered by folder. Recent/Most: Tracks view with fixed sort and a limit of 500.

### Virtualised lists (E3-S3)
Every library view binds an `IncrementalList<T>` (`Tunqio.Core.Library`): an `ObservableCollection` that appends one keyset page at a time through a `PageLoader<T>` (`PageLoaders.Tracks/Albums/Artists` over the repositories). One load is in flight at a time, a short page or the query's `Take` ends the list, `Reset` drops a page still loading, and a failed page leaves the list loadable with `LastError` set for the error surface. The loader always runs on the thread pool: Microsoft.Data.Sqlite completes its async calls inline, and a page that completed synchronously inside the ListView's measure pass re-entered the list's own request until all 100k rows were in memory and the window had never painted. In the app, `IncrementalItemsSource<T>` adds `ISupportIncrementalLoading`, so `TracksList` (a `ListView` with fixed 36 px rows over an `ItemsStackPanel`, secondary columns on `x:Phase` 1 and 2) pages on its own, while `AlbumsGrid` (an `ItemsRepeater` with `UniformGridLayout`, tiles recycled through its pool, the art cache's 300 px tile over the title-hash placeholder through `AlbumArt.Tile(art_hash)`, a `BitmapImage` XAML decodes off the UI thread) asks for the next page whenever the scroll position is within two viewports of the end. `Tunqio.exe --library-spike [DB] [--seconds N] [--out FILE]` shows both over a database, scrolls the Tracks table one viewport per frame to the end and writes `library-spike.json` (rows loaded, containers created against rows shown, frame gaps, working set before and after); the fps and memory criteria it evaluates are the reference-machine ones (T-90).

### Album detail
- Header: art, title, artist, year, genre, disc count, total duration, format summary. Body: tracks grouped by disc with number, title, artists if different from album artist, duration, rating. Actions: play album, shuffle album, enqueue, add to playlist, edit tags (batch), show in folder.

### Playlist detail
- Ordered tracks with drag reorder handles, remove, total duration. Actions: play, shuffle, rename, delete (confirm), export M3U8, pin to jump list. In Curation mode this is the right pane with drop targets.

### Queue
- Now-playing item pinned at top, upcoming items below with drag reorder, remove, "clear upcoming", "save as playlist". Shows total remaining time. Reflects shuffle order.

### Search
- Search box in sidebar header. Results grouped: Tracks (top 20), Albums (top 10), Artists (top 10), with "show all" per group. Enter plays the first track result; arrow keys move; Ctrl+Enter enqueues.
- Binds: `ISearchService.SearchAsync` debounced 120 ms, cancels the previous query.

### Settings
- Playback: gapless, crossfade slider, ReplayGain mode and preamp, resume on launch, previous-track threshold.
- Output: device list with default marker, shared/exclusive, buffer size, sample rate readout, "test tone" button, "recover device" action.
- Library: folders list (add/remove/rescan), scan status and last report, split-artists toggle, purge missing, rebuild search index, import/export playlists, write ratings to files.
- Appearance: theme, backdrop, reactive theming toggle and smoothing, reduced-motion override, accent source (art / spectrum / system).
- Visualization: preset list with thumbnails, per-preset parameters, quality policy, show FPS overlay.
- Shortcuts: table of actions with editable key bindings, conflict detection, reset.
- About & Diagnostics: version, licences (BASS, TagLibSharp and others), open logs folder, export diagnostics zip, crash reporting opt-in, performance readout (dropouts, frame time, memory).

### Mini player
- 360×120 window: art, title/artist marquee, transport, progress, volume on hover. Always-on-top toggle, snap to corners, double-click returns to main window. Uses a second `Window` with `OverlappedPresenter` (no title bar).

### Tag editor dialog
- Single: all editable fields, art preview with replace/remove. Batch: fields show "(multiple values)" and only changed fields are written. Preview list of files affected; Confirm writes with progress; Undo available from the sidebar `InfoBar` for the session.

### First-run welcome
- Three steps: add folders (offers Music folder by default), choose output device, choose theme. Skippable. Scan starts immediately in the background.

## Primary flows and acceptance behaviour

1. **First run to first sound.** Launch → welcome → add Music folder → scan begins → Albums grid fills progressively → click an album tile → playback starts within 300 ms of click → SMTC shows art and title. The grid must populate as batches commit; the user does not wait for the scan to finish.
2. **Open file from Explorer while running.** Double-click a FLAC → existing window activates → track plays now; the previous queue is preserved and the new track is inserted at the current position. If the file is outside library folders it plays but is not added to the library; a chip offers "Add folder to library".
3. **Gapless album.** Play a continuous-mix album → no audible gap or click at any boundary → Now Playing metadata updates within 100 ms of the boundary → play events recorded per track.
4. **Device unplug.** Playing through USB DAC in exclusive mode → unplug → playback pauses within 500 ms, sticky `InfoBar` "Output device disconnected" with "Use default device" → reconnect → `InfoBar` offers "Switch back". No crash, no silent continuation on the wrong device without notice.
5. **Search and queue.** Type "rad" → results under 50 ms → arrow to a track → Ctrl+Enter enqueues → queue badge increments → Esc clears search and restores the previous sidebar page.
6. **Curation.** Switch to Curation → create playlist "Sunday" → drag six tracks from Albums pane → reorder two → Ctrl+Z undoes the reorder → export M3U8 → file opens in another player with correct relative paths.
7. **Focus session.** Double-click art → Focus → controls fade after 3 s → media key Next works → moving the mouse reveals controls → Esc returns to the previous mode with layout restored.
8. **Batch tag edit.** Select 12 tracks → Edit tags → set Album Artist → Confirm → progress → library view updates → Undo restores the previous values in files and database.
9. **Resume.** Quit mid-track → relaunch → track shown paused at the same position with queue intact → Space resumes.
10. **Reduced motion.** Enable Windows reduced-motion → reactive theming stops, mode transitions become instant, visualizer keeps rendering (it is content, not chrome) unless the user turns it off.

## Keyboard shortcuts (defaults, all rebindable)

| Action | Key |
|--------|-----|
| Play/Pause | Space (when focus is not in a text box), Media Play/Pause |
| Next / Previous | Ctrl+Right / Ctrl+Left, Media keys |
| Seek ±5 s / ±30 s | Right/Left, Shift+Right/Left |
| Volume ±5% / Mute | Up/Down, M |
| Shuffle / Repeat | S / R |
| Discovery / Focus / Curation | Ctrl+1 / Ctrl+2 / Ctrl+3; F11 toggles Focus; Esc leaves Focus |
| Search | Ctrl+F or / |
| Queue | Q |
| Mini player | Ctrl+M |
| Settings | Ctrl+, |
| Play selected / Play next / Enqueue | Enter / Shift+Enter / Ctrl+Enter |
| Add selected to playlist | Ctrl+P |
| Edit tags | F2 |
| Show in folder | Ctrl+Shift+E |
| Rate 1–5 / clear | Ctrl+Alt+1..5 / Ctrl+Alt+0 |
| Undo / Redo (Curation) | Ctrl+Z / Ctrl+Y |
| Back (sidebar) | Alt+Left |
| Next preset | Ctrl+V |

Shortcuts are `KeyboardAccelerator`s registered at the shell level; text boxes opt out of single-letter accelerators via `KeyboardAcceleratorPlacementMode` and a focused-element check.

## Accessibility contract

- Every interactive element has an `AutomationProperties.Name`; art tiles announce "Album <title> by <artist>, <year>". Live regions announce track changes in Focus mode (`AutomationProperties.LiveSetting = Polite`).
- Focus order follows visual order; Focus mode keeps a focusable but invisible transport so keyboard users are never stranded.
- Contrast: theme tokens are validated in a unit test against WCAG 2.1 AA for text and 3:1 for controls in light, dark and high-contrast. Reactive theming blends only the background layer; foreground text is always drawn on a solid or acrylic surface with guaranteed contrast.
- Reduced motion (`UISettings.AnimationsEnabled == false`) disables reactive theming and transitions. High contrast disables reactive theming and the Mica backdrop.
- Visualizer has an off switch and never flashes above 3 Hz full-field luminance change (presets are checked with a luminance-delta test in Rendering.Tests).
- Text scales with system text size; layout breakpoints are tested at 100%, 150% and 200%.

## Localisation

Strings live in `Strings/en-US/Resources.resw`; no hard-coded UI text. 1.0 ships English only; the build fails on strings not in the resource file (analyzer). Right-to-left is not tested in 1.0.

## Visual language notes

- Type: Segoe UI Variable, sizes from the WinUI type ramp; Now Playing title at 28 px semibold.
- Surfaces: layered Fluent (Mica base → acrylic sidebar → solid cards). Art palette drives the accent when "accent source = art".
- Iconography: Segoe Fluent Icons throughout; no custom glyphs except the app icon.
- Spacing: 8 px grid; tiles 300 px with 16 px gutters; list rows 44 px (48 px in touch mode).
