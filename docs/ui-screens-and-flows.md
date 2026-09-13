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

The transport (E2-S2) lives in the controls panel and binds `TransportViewModel`, which reflects `PlaybackSnapshot` for everything except the position during a drag. That exception is the whole design: a `Slider` raises the same event for a user dragging and for a binding update, and snapshots arrive at 10 Hz, so a position bound straight through jumps back under the thumb every 100 ms. The pointer marks the drag, the view model refuses to move the position unless one is in progress, and one seek is issued when the capture is lost. Keyboard scrubbing commits on key-up rather than per repeat. Every control is a real `Button`, `ToggleButton` or `Slider` with an `AutomationProperties.Name` carrying its state ("Repeat all", not "Repeat"), and `tools/check-transport-automation.ps1` walks the live UIA tree to check that — including the Queue button (E2-S5), which it invokes so the flyout's own controls are walked too. The shortcuts from the table below are `KeyboardAccelerator`s on the shell root, each refusing to fire while focus is in a text box — a search box that shuffles the queue whenever someone types an S is not a search box.

Files reach the queue from three places (E2-S4): the empty state's Open files / Open folder buttons, and a drop anywhere on the window — `AllowDrop` is on the shell root rather than on a panel, because someone dragging an album onto a music player is not aiming at a region. `OpenFilesService` walks folders in full, keeps only the extensions `AudioFormats` knows, and sorts a folder's contents so disc sub-folders stay in order; a picker's selection keeps the order the user gave it. AC-75's 500 ms is met by reading tags for the *first* file only, playing it, and then reading and appending the rest behind the music — 200 tag reads never fit in 500 ms, so the order of the work is the design and not an optimisation. A file the library already has plays as its own row, with its play count and art; one it does not becomes a transient track (`TransientTrackStore`, ids counting down from −1 against SQLite's positive rowids) that plays without joining the library, which is flow 2. A drop with nothing playable in it says so in the `InfoBar` and leaves the queue alone; the ordinary case says nothing, because the music starting is the feedback.

The Now Playing panel (E2-S3) draws the art over the visualizer surface with the metadata block under it, and binds `NowPlayingViewModel`, which reflects `PlaybackSnapshot` the way the transport does with one difference that matters: it notifies once per real track change rather than once per snapshot. The art binding is a function binding that builds a fresh `BitmapImage` whenever the property it reads notifies, so re-raising at 10 Hz would re-open a 1000 px file ten times a second for a track nobody changed; `QueueItem` carries an `InstanceId`, so replaying the same track counts as a change and the ten snapshots after it do not. The placeholder tile — a hue from an FNV-1a hash of the album title, with its initials — is a layer *underneath* the image rather than an alternative to it, because the decode takes real time and something has to be on the screen during it; a file that has gone since the row was written simply leaves the placeholder showing. The format badge reads a lossless track as depth and rate (`FLAC 24/96`) and a lossy one as a bit rate (`MP3 320 kbps`), and states neither where the scanner could not tell. `Tunqio.exe --nowplaying-spike` writes `nowplaying-spike.json`: what the panel is actually showing, read back off the live tree, and sixteen 1000 px loads at once measured against the gaps between composition frames.

The shell's three panels (E2-S1) take their shares and their orientation from `ShellLayout`, a table rather than a set of `AdaptiveTrigger`s: the criterion is about proportions, and a rule that exists only as a `VisualState` can be checked only by looking at a running window. `ShellChrome` is the one piece that touches the visual tree. The controls panel is a transport bar at its natural height in every shape, never a share: under Now Playing when the panels are columns, with the sidebar running the full height beside both, and across the bottom of both when they stack (T-182; until then it was a third, 15% column, which measured 128 px at a 1000 px window against a transport row that needs 242, and clipped Shuffle to nothing). Below 800 px the panels stack at equal heights, the art scaling down to its half, and the library views sit behind a single menu button rather than a strip of icons, because there the sidebar is short rather than narrow; between 800 and 1200 they are columns at 2 : 1; from 1200 up they are 60 / 40. Those shares are Phil's (T-182: a first cut at 75 / 25 and 80 / 20, with 2 : 1 stacked, was rejected in review because the sidebar narrowed too far to browse and Compact could not be navigated). Now Playing and the sidebar each keep a floor (400 / 200 px), and `ShellLayout.FloorsBind` says when one wins over its share; at these shares none ever does, since the narrowest column shape is 533 / 267 px. Now Playing's floor is also the bar's, and it covers the transport's 242 px. `Tunqio.exe --shell-spike` measures the panels on the live tree at five widths and switches every theme, and writes the table to `shell-spike.json`. The backdrop is Mica on Windows 11 and desktop acrylic on Windows 10, which is half the supported range (Q-4); the root paints nothing when there is a backdrop, so the layered surfaces show through it, and paints an opaque brush when there is neither. A theme change is a `RequestedTheme` on that root, so it repaints brushes already in the tree with nothing unloaded and no frame of bare window.

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
- Genres: tag cloud sized by count → filtered Tracks view. Folders: the library folders → Tracks filtered by folder (a sub-folder tree is not in E3-S8). Recent/Most: Tracks view with fixed sort and a limit of 500, over played tracks only for the two played views.

### Virtualised lists (E3-S3)
Every library view binds an `IncrementalList<T>` (`Tunqio.Core.Library`): an `ObservableCollection` that appends one keyset page at a time through a `PageLoader<T>` (`PageLoaders.Tracks/Albums/Artists` over the repositories). One load is in flight at a time, a short page or the query's `Take` ends the list, `Reset` drops a page still loading, and a failed page leaves the list loadable with `LastError` set for the error surface. The loader always runs on the thread pool: Microsoft.Data.Sqlite completes its async calls inline, and a page that completed synchronously inside the ListView's measure pass re-entered the list's own request until all 100k rows were in memory and the window had never painted. In the app, `IncrementalItemsSource<T>` adds `ISupportIncrementalLoading`, so `TracksList` (a `ListView` with fixed 36 px rows over an `ItemsStackPanel`, secondary columns on `x:Phase` 1 and 2) pages on its own, while `AlbumsGrid` (an `ItemsRepeater` with `UniformGridLayout`, tiles recycled through its pool, the art cache's 300 px tile over the title-hash placeholder through `AlbumArt.Tile(art_hash)`, a `BitmapImage` XAML decodes off the UI thread) asks for the next page whenever the scroll position is within two viewports of the end. `Tunqio.exe --library-spike [DB] [--seconds N] [--out FILE]` shows both over a database, scrolls the Tracks table one viewport per frame to the end and writes `library-spike.json` (rows loaded, containers created against rows shown, frame gaps, working set before and after); the fps and memory criteria it evaluates are the reference-machine ones (T-90).

### Library views (E3-S8)
The eight library views and the two detail pages live in `Tunqio.App.Library`: `LibraryPane` is a `NavigationView` (compact left pane, one item per view) over a `Frame` with a back stack (the pane's back button and Alt+Left), placed in `MainWindow`'s right column until E2-S1 builds the shell. Each page owns a view model from DI (`AddLibraryViews`: transient view models, a singleton `LibraryNavigator` the pane attaches its frame to, and `IFileRevealer` for "show in folder"); the view models are tested against fake repositories in `Tunqio.App.Tests`. `AlbumsPage` binds `AlbumsViewModel` (sort chooser remembered in `ui.albumsSort`, with Added and Played opening newest-first; genre, decade and format chips from `IGenreRepository` and `IAlbumRepository.ListFacetsAsync`; every change replaces the paged source). `TracksPage` takes a `TracksSpec` (all, genre, folder, recently added, recently played, most played) and binds `TracksViewModel`: the header cells sort (same column flips direction; counts and dates open descending), the fixed views lock the sort and cap at 500 rows, and the column chooser hides columns in the header and every realised row (`ui.tracksHiddenColumns`). `ArtistsPage` pages by sort name with a jump bar that loads pages until the letter is in the list; `GenresPage` is a wrap-panel cloud with log-scaled font sizes; `FoldersPage` lists the library folders. `AlbumDetailPage` shows the 1000 px art, the facts ("12 tracks · 48 min · 2 discs · FLAC 16/44.1"), and the tracks in a grouped `ListView` when there is more than one disc; `ArtistDetailPage` shows the fronted albums and "Appears on" in two `AlbumsGrid`s plus "play all". Actions reach playback through `IPlaybackCommands` (`Tunqio.Core.Playback`: play now from an index with an optional shuffle, play next, enqueue), the queue-facing face of `PlaybackSession` (E1-S10). The app registers `AppPlaybackCommands`, which forwards to the session `AudioStartup` creates after the first frame and logs the request when there is no session yet or audio could not start at all (E1-S10e). Keyboard: Tab enters a grid once, the arrow keys move between tiles (`XYFocusKeyboardNavigation`), Enter plays, Shift+Enter plays next, Ctrl+Enter queues, Shift+F10 opens the tile or row menu, and a row menu acts on the selection in list order. Not yet wired: hover preview (E5-S5), add to playlist (E6-S1), edit tags (E3-S10), refreshing an open view when a scan commits (E3-S12), and inline scan progress in the empty state.

### Album detail
- Header: art, title, artist, year, genre, disc count, total duration, format summary. Body: tracks grouped by disc with number, title, artists if different from album artist, duration, rating. Actions: play album, shuffle album, enqueue, add to playlist, edit tags (batch), show in folder.

### Playlist detail
- Ordered tracks with drag reorder handles, remove, total duration. Actions: play, shuffle, rename, delete (confirm), export M3U8, pin to jump list. In Curation mode this is the right pane with drop targets.

### Queue
- Now-playing item pinned at top, upcoming items below with drag reorder, remove, "clear upcoming", "save as playlist". Shows total remaining time. Reflects shuffle order.
- E2-S5 ships `QueuePanel` over `QueueViewModel` as the flyout from the controls panel's Queue button; the sidebar's own Queue page waits for the Playlists section it sits beside in the map above (E6-S1), and "save as playlist" waits for the same story. The pinned row is the queue's current item and the list below it is everything after it in *play* order, so a shuffled queue reads in the order it will play. Both the pinned row and every upcoming row can be removed; removing the one that is playing starts whatever took its place. "Clear upcoming" empties everything after the current track and drops what the engine had pre-opened with it, since a queue that says one thing while the mixer says another is worse than either. The remaining time is the rest of the current track — from the engine, which has the file open, rather than from a tag that can be wrong — plus every upcoming track's length.
- The reorder is the `ListView`'s own, and the view model watches the bound collection rather than `DragItemsCompleted`: a pointer drag and a keyboard reorder then reach `PlaybackSession` by one path. A reorder arrives there as a remove and then an insert, and the half-way state is not a permutation of the queue's upcoming items, which is what stops a row in flight being reported as a removal. AC-76 is the part only the session can do — the track after the current one is already open in the mixer, so a drag that changes which track that is has to re-open it, and the test asserts that through the engine's call log rather than through the queue value.

### Search
- Search box in the sidebar header, above the library pane's page frame (E3-S9: `LibraryPane` hosts an `AutoSuggestBox` over the `NavigationView`). While it has text, `SearchResultsView` replaces the page: one grouped list, Tracks (top 20), Albums (top 10), Artists (top 10), each group with "Show all" (500) / "Show fewer" in its header, empty groups omitted, "No results" when all are.
- Keys: Ctrl+F or `/` focuses the box; Down moves into the results and Up from the first row returns; Enter in the box plays the first track result, Shift+Enter plays it next, Ctrl+Enter queues it; the same keys on a track or album row act on that row; Enter on an artist opens it; Esc (in the box or the list) clears the search and restores the page. Any page navigation (a result opened, a pane item chosen) clears the search too.
- Binds: `SearchViewModel` over `ISearchService.SearchAsync`, debounced 120 ms; a new keystroke cancels the query in flight and a cancelled query's answer is never shown, whether the backend honoured the cancellation or not.

### Settings
- Playback: gapless, crossfade slider, ReplayGain mode and preamp, resume on launch, previous-track threshold.
- Output: device list with default marker, shared/exclusive, buffer size, sample rate readout, "test tone" button, "recover device" action.
- Library: folders list (add/remove/rescan), scan status and last report, split-artists toggle, purge missing, rebuild search index, import/export playlists, write ratings to files.
  - E3-S12 ships `LibrarySettingsPage` over `LibrarySettingsViewModel`. Until E6 builds the overlay it lives in the library pane's frame, opened by the `NavigationView` settings item or Ctrl+, (a cached page: coming back keeps its state). Folders: one card each (name, path, "Scanned 5 min ago · ok" / "Never scanned" / "Disabled", an enable switch, Rescan, Remove behind a confirming dialog since the tracks go with their play history and playlist entries); Add folder… opens the system folder picker, stores the folder, refreshes the watcher and scans it. Scanning: an indeterminate bar with one live line ("Reading tags · 1,234 files seen · 300 read · 12 added"), Cancel, Rescan all (every file re-read), and the last report ("Completed in 1.5 s · 116 files · 30 added · 5 updated · 80 unchanged · 1 failed", "Last scan 5 min ago", an expander listing up to 50 files that could not be read). Tags: the split-artists and write-ratings switches, saved to `settings.json` as they change. Maintenance: Purge N missing (tracks flagged for over 30 days; disabled at zero), Rebuild search index, Regenerate art (clears the cache, then a forced rescan renders it again; shown only when an art cache is registered); each ends in an `InfoBar` saying what it did. Import/export playlists arrives with the playlist store and M3U8 layer (E6-S1, E6-S2).
- Appearance: theme, backdrop, reactive theming toggle and smoothing, reduced-motion override, accent source (art / spectrum / system).
- Visualization: preset list with thumbnails, per-preset parameters, quality policy, show FPS overlay.
  - E4-S9 ships `VisualizationSettingsPage` over `VisualizationSettingsViewModel`. Until E6 builds the overlay it lives in the library pane's frame beside Settings › Library, and a pair of buttons at the top of either page is the settings navigation; the pane's settings item stays selected on both. Thumbnails and the quality policy are not here — the first has no source yet and the second is E4-S7's `mp_renderer_set_quality`, still a stub. **Nothing about a preset is written down on the page.** The list is `mp_renderer_enum_presets` and every control is built from `mp_renderer_enum_preset_params` (T-142): the label, the range, the step, the unit, and whether it is a slider or a list of named modes. That is what lets a preset a *user* wrote get the controls its own manifest asked for, and it is what keeps Ambient Glow's `art_primary`/`art_secondary`/`art_accent` off the page entirely — they are set by code from the album art palette and carry packed sRGB integers, and the manifest marks them hidden. Choosing a preset switches the running visualizer, is stored as `viz.preset` and is what the next launch starts on; a preset whose shader will not compile leaves the one that was drawing on screen and puts the compiler's own first diagnostic (file, line, error code, text) in the page's `InfoBar`, which is the only place in the app AC-117's message ever reaches a person. Reset returns every parameter to its manifest default; parameter values themselves are not persisted, because the ABI returns them to their defaults on a preset switch and the page follows that rather than fighting it. **Your presets**: the page names `%LocalAppData%\Tunqio\presets`, opens it, and Refresh rereads both roots — a button and not a watcher, for the reasons in [visualization-engine.md](visualization-engine.md). **Audio-reactive theming** (T-151): `ui.reactiveTheming` as a switch and `ui.reactiveSmoothing` as a slider that says what it means in seconds — 0 is a 0.5 s time constant and 1 is 4 s, and the label states the one the setting maps to, because "0.35" is a number with no meaning outside the source. Those two belong to Appearance in the map above and will move there when E6 builds it; they are here now because E4-S6 shipped them with no UI at all. `tools/check-visualization-settings.ps1` reads all of it off the live UIA tree, including a preset written to disk mid-run, and — after the first version of this page shipped with sliders running under the controls panel — the geometry as well: at three window widths and three scroll positions, every control lies inside the content column, the column fills the panel it is in, and the settings surface stops short of the controls panel. Nothing on this page carries a fixed `Width`; the column is capped once at 720 and everything in it stretches.
- Shortcuts: table of actions with editable key bindings, conflict detection, reset.
- About & Diagnostics: version, licences (BASS, TagLibSharp and others), open logs folder, export diagnostics zip, crash reporting opt-in, performance readout (dropouts, frame time, memory).

### Mini player
- 360×120 window: art, title/artist marquee, transport, progress, volume on hover. Always-on-top toggle, snap to corners, double-click returns to main window. Uses a second `Window` with `OverlappedPresenter` (no title bar).

### Tag editor dialog
- Single: all editable fields, art preview with replace/remove. Batch: fields show "(multiple values)" and only changed fields are written. Preview list of files affected; Confirm writes with progress; Undo available from the sidebar `InfoBar` for the session.
- A clean write closes the dialog and the sidebar `InfoBar` is the report ("12 tracks updated"), because it survives long enough to read and a dialog dismissed by its own success does not. The per-file verdict column beside each file is therefore a *failure* surface: on a clean batch the verdicts are written and dismissed together, and a file that could not be written keeps the dialog up so its reason stays on screen. The list of affected files is the only thing in the dialog that scrolls — a page that scrolls around a list that also scrolls sends the wheel to the wrong one.

### First-run welcome
- Three steps: add folders (offers Music folder by default), choose output device, choose theme. Skippable. Scan starts immediately in the background.

## Primary flows and acceptance behaviour

1. **First run to first sound.** Launch → welcome → add Music folder → scan begins → Albums grid fills progressively → click an album tile → playback starts within 300 ms of click → SMTC shows art and title. The grid must populate as batches commit; the user does not wait for the scan to finish.
2. **Open file from Explorer while running.** Double-click a FLAC → existing window activates → track plays now; the previous queue is preserved and the new track is inserted at the current position. If the file is outside library folders it plays but is not added to the library; a chip offers "Add folder to library".
3. **Gapless album.** Play a continuous-mix album → no audible gap or click at any boundary → Now Playing metadata updates within 100 ms of the boundary → play events recorded per track.
4. **Device unplug.** Playing through USB DAC in exclusive mode → unplug → playback pauses within 500 ms, sticky `InfoBar` "Output device disconnected" with "Use default device" → reconnect → `InfoBar` offers "Switch back". No crash, no silent continuation on the wrong device without notice.
   - E2-S7 ships this as `ShellNotices` over `NoticePanel`, one `InfoBar` per notice and one notice per kind. The sticky / transient split follows what the notice is *about*: the output's health is `OutputStatus` on `PlaybackSnapshot` and stays until the snapshot says otherwise, because a device that is gone is a state the user is still in a minute later; engine errors (`PlaybackSession.Errors`, a stream so a second failure cannot overwrite the first) and scan reports are events, told once and timed out. Recovering is the session's, since it is the only caller of `IAudioEngine`: `UseSystemDefaultOutputAsync` reopens on the default and resumes from the position the loss parked, and does not rewrite `output.deviceId` — that is the user's setting, and rewriting it would mean plugging the device back in never brought it back. A scan reports only when it changed rows or could not read a file; the launch scan runs at every start, and a bar saying "0 files" every time trains the user to ignore the bar this flow needs them to read.
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
| Diagnostics overlay | Ctrl+Shift+D |

Shortcuts are registered at the shell level, and E2-S6 ships them as `ShellShortcuts` — a table, not a list of `KeyboardAccelerator`s in markup. Half of it cannot be accelerators at all: an accelerator fires only on a key that reached the end of the routed event unhandled, and a library tile is a `Button` that takes Space as a press while a list or `ComboBox` takes S, R, M and Q as type-ahead, so the bare keys are taken at the shell root's tunnelling `PreviewKeyDown` before the focused control sees them. The arrow keys are the opposite and stay accelerators, because they are how a grid is navigated and a shell that took them would trade that for a five-second seek; Ctrl+Left and Ctrl+Right are pre-empted anyway, which costs a grid its focus-without-selection move and is the right trade while browsing. Nothing in the table fires while focus is in a text box — broader than a single-letter opt-out, since Ctrl+Left is how a caret moves by word and Space is a space — and the focused-element check must be `FocusManager.GetFocusedElement(XamlRoot)`, since the parameterless overload answers for a `CoreWindow` a desktop app does not have and silently returns null. `tools/check-shortcuts.ps1` presses the keys at a running window and reads the effect off the transport's own automation names. Media keys are SMTC (E7); the shortcuts above whose features do not exist yet are deliberately not registered. Ctrl+Shift+D (E2-S8) is the table's one exception to the text-box rule: it opens the diagnostics overlay — playback state, output format, latency, underruns, adapter, frame time and histogram, refreshed twice a second while it is open and copyable to the clipboard as text — and a text box has no opinion about that chord, while the moment the overlay is most wanted is the moment something has gone wrong wherever the user was.

## Accessibility contract

- Every interactive element has an `AutomationProperties.Name`; art tiles announce "Album <title> by <artist>, <year>" and Tracks rows announce "<title> by <artist>, <album>, <duration>". A list row needs one as much as a button does: without it the row falls back to the data object's own `ToString`, and a screen reader reads the whole record - file path and all - on every arrow key. Rows name the four columns that identify a track and stop there; format, plays and rating are detail, and Narrator repeats the whole string per row. Live regions announce track changes in Focus mode (`AutomationProperties.LiveSetting = Polite`).
- Focus order follows visual order; Focus mode keeps a focusable but invisible transport so keyboard users are never stranded.
- Contrast: theme tokens are validated in a unit test against WCAG 2.1 AA for text and 3:1 for controls in light, dark and high-contrast. Reactive theming blends only the background layer; foreground text is always drawn on a solid or acrylic surface with guaranteed contrast. As built (E4-S6), the guarantee is a closed-form bound on the background's relative luminance rather than a search, so it is checked over **every one of the 16 777 216 sRGB colours** for each theme rather than sampled - 0 violations, worst 4.5000:1 - and the foreground tokens drawn on a reactive surface are opaque, because an alpha'd foreground resolves against the colour that is moving and cannot be guaranteed by a bound on the background alone.
- Reduced motion (`UISettings.AnimationsEnabled == false`) disables reactive theming and transitions. High contrast disables reactive theming and the Mica backdrop. Both are read live on every 30 Hz poll as well as on the system's change event, so the stop is bounded by the poll rather than by whether a notification arrived - measured at 0.000 ms on the event and 33.333 ms with the event lost.
- Visualizer has an off switch and never flashes above 3 Hz full-field luminance change (presets are checked with a luminance-delta test in Rendering.Tests).
- Text scales with system text size; layout breakpoints are tested at 100%, 150% and 200%.

## Localisation

Strings live in `Strings/en-US/Resources.resw`; no hard-coded UI text. 1.0 ships English only; the build fails on strings not in the resource file (analyzer). Right-to-left is not tested in 1.0.

## Visual language notes

- Type: Segoe UI Variable, sizes from the WinUI type ramp; Now Playing title at 28 px semibold.
- Surfaces: layered Fluent (Mica base → acrylic sidebar → solid cards). Art palette drives the accent when "accent source = art".
- Iconography: Segoe Fluent Icons throughout; no custom glyphs except the app icon.
- Spacing: 8 px grid; tiles 300 px with 16 px gutters; list rows 44 px (48 px in touch mode).
