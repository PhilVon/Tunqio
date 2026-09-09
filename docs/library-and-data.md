# Library and Data Model

The library subsystem turns folders of audio files into a browsable, searchable catalogue and keeps it current. It owns the SQLite database, the scanner, tag reading and writing, album art caching, playlists, the play queue model and play history. Decisions: ADR-005 (SQLite + TagLibSharp), ADR-010 (library worker threads).

## Storage layout

```
%LocalAppData%\Tunqio\
  library.db              SQLite, WAL mode. Catalogue, playlists, history, settings.
  library.db-wal / -shm   SQLite journal files.
  settings.json           Settings (E0-S6 JsonSettingsStore; atomic replace). Stays JSON by decision Q-15 (T-89): settings survive a database reset and are readable without SQLite.
  library.corrupt-<ts>.db An unusable database moved aside by E3-S1 recovery (kept, never deleted by the app).
  art\
    ab\abcdef0123...\      Album art keyed by SHA-256 of the source image bytes.
      original.jpg|png     Untouched source (only kept if < 4 MB).
      1000.jpg             Long edge 1000 px, quality 85.
      300.jpg              Grid tile.
      96.jpg               List row and SMTC thumbnail.
      palette.json         Five dominant colours + luminance, for theming.
  logs\                    Rolling Serilog files, 7 days.
  presets\                 User visualization presets (ADR-009).
  exports\
    playlists\*.m3u8       Auto-exported on every playlist change (see "Durability").
```

`%LocalAppData%\Tunqio\` is the literal path: the manifest disables MSIX file-system write virtualisation so the tree is not redirected under `Packages\...\LocalCache` and deleted on uninstall (see [identity.md](identity.md)).

The database is a cache of the files plus user data. Deleting `library.db` and rescanning must reproduce the catalogue; user data (playlists, history, ratings) is protected by the export rule below.

## Schema (v1)

All timestamps are Unix milliseconds UTC. Text is UTF-8. `WITHOUT ROWID` is not used; integer primary keys double as stable IDs for the UI.

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;

CREATE TABLE schema_version (version INTEGER NOT NULL, applied_at INTEGER NOT NULL);

CREATE TABLE library_folder (
  id           INTEGER PRIMARY KEY,
  path         TEXT NOT NULL UNIQUE,          -- normalised, trailing separator, case preserved
  enabled      INTEGER NOT NULL DEFAULT 1,
  last_scan_at INTEGER,
  last_scan_status TEXT                       -- 'ok' | 'partial' | 'failed'
);

CREATE TABLE artist (
  id         INTEGER PRIMARY KEY,
  name       TEXT NOT NULL,
  sort_name  TEXT NOT NULL,                   -- "Beatles, The"
  mbid       TEXT,                            -- MusicBrainz artist id if tagged
  UNIQUE (name COLLATE NOCASE)
);

CREATE TABLE album (
  id              INTEGER PRIMARY KEY,
  title           TEXT NOT NULL,
  album_artist_id INTEGER REFERENCES artist(id),
  year            INTEGER,
  disc_count      INTEGER,
  art_hash        TEXT,                       -- key into art\ cache; NULL = no art
  mbid            TEXT,
  UNIQUE (title COLLATE NOCASE, album_artist_id, year)
);

CREATE TABLE genre (
  id   INTEGER PRIMARY KEY,
  name TEXT NOT NULL UNIQUE COLLATE NOCASE
);

CREATE TABLE track (
  id             INTEGER PRIMARY KEY,
  folder_id      INTEGER NOT NULL REFERENCES library_folder(id) ON DELETE CASCADE,
  path           TEXT NOT NULL UNIQUE,        -- absolute path for source_kind 'local'
  source_kind    TEXT NOT NULL DEFAULT 'local', -- reserved per Q-3; only 'local' in 1.0
  file_size      INTEGER NOT NULL,
  file_mtime     INTEGER NOT NULL,            -- for change detection
  title          TEXT NOT NULL,               -- falls back to file name
  album_id       INTEGER REFERENCES album(id),
  track_no       INTEGER,
  disc_no        INTEGER,
  year           INTEGER,
  duration_ms    INTEGER NOT NULL,
  bitrate_kbps   INTEGER,
  sample_rate    INTEGER,
  channels       INTEGER,
  bit_depth      INTEGER,
  codec          TEXT NOT NULL,               -- 'flac', 'mp3', ...
  composer       TEXT,
  comment        TEXT,
  rg_track_gain  REAL,                        -- dB
  rg_track_peak  REAL,
  rg_album_gain  REAL,
  rg_album_peak  REAL,
  art_hash       TEXT,                        -- embedded art if it differs from album art
  mbid           TEXT,
  added_at       INTEGER NOT NULL,
  rating         INTEGER,                     -- 0..100, user data
  play_count     INTEGER NOT NULL DEFAULT 0,  -- denormalised from play_event
  last_played_at INTEGER,
  missing        INTEGER NOT NULL DEFAULT 0   -- file not found at last scan; hidden but retained
);
CREATE INDEX ix_track_album ON track(album_id, disc_no, track_no);
CREATE INDEX ix_track_added ON track(added_at DESC);
CREATE INDEX ix_track_played ON track(last_played_at DESC);
CREATE INDEX ix_track_folder ON track(folder_id);

CREATE TABLE track_artist (                   -- many-to-many; role distinguishes credit type
  track_id  INTEGER NOT NULL REFERENCES track(id) ON DELETE CASCADE,
  artist_id INTEGER NOT NULL REFERENCES artist(id),
  role      TEXT NOT NULL DEFAULT 'artist',  -- 'artist' | 'composer' | 'performer'
  position  INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (track_id, artist_id, role)
);
CREATE INDEX ix_track_artist_artist ON track_artist(artist_id);

CREATE TABLE track_genre (
  track_id INTEGER NOT NULL REFERENCES track(id) ON DELETE CASCADE,
  genre_id INTEGER NOT NULL REFERENCES genre(id),
  PRIMARY KEY (track_id, genre_id)
);

CREATE TABLE playlist (
  id          INTEGER PRIMARY KEY,
  name        TEXT NOT NULL,
  created_at  INTEGER NOT NULL,
  modified_at INTEGER NOT NULL,
  pinned      INTEGER NOT NULL DEFAULT 0,     -- appears in jump list
  kind        TEXT NOT NULL DEFAULT 'manual'  -- 'manual' | 'smart' (1.1)
);

CREATE TABLE playlist_item (
  playlist_id INTEGER NOT NULL REFERENCES playlist(id) ON DELETE CASCADE,
  position    INTEGER NOT NULL,
  track_id    INTEGER NOT NULL REFERENCES track(id) ON DELETE CASCADE,
  PRIMARY KEY (playlist_id, position)
);

CREATE TABLE play_event (
  id         INTEGER PRIMARY KEY,
  track_id   INTEGER NOT NULL REFERENCES track(id) ON DELETE CASCADE,
  started_at INTEGER NOT NULL,
  played_ms  INTEGER NOT NULL,                -- how much was actually heard
  completed  INTEGER NOT NULL                 -- 1 if > 50% or > 4 min heard (scrobble rule)
);
CREATE INDEX ix_play_event_track ON play_event(track_id, started_at DESC);

CREATE TABLE queue_state (                    -- single row; restored on launch
  id            INTEGER PRIMARY KEY CHECK (id = 1),
  items_json    TEXT NOT NULL,                -- ordered track ids + original order for shuffle
  current_index INTEGER,
  position_ms   INTEGER,
  shuffle       INTEGER NOT NULL,
  repeat_mode   TEXT NOT NULL,
  saved_at      INTEGER NOT NULL
);

CREATE TABLE setting (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL                          -- JSON. Reserved: settings live in settings.json (Q-15); this table is for per-library state such as scan cursors
);

-- Full-text search. External-content table so the row is never duplicated.
CREATE VIRTUAL TABLE track_fts USING fts5(
  title, artists, album, album_artist,
  content='',                                 -- contentless; we rebuild from track on change
  tokenize = 'trigram'
);
```

**Migrations (E3-S1).** `Tunqio.Library.Database.LibraryMigrations.All` is the ordered, append-only list of every schema version; `LibraryMigrator` applies the pending ones on open, each in its own transaction followed by its `schema_version` row, so a crash mid-upgrade leaves the previous version intact. Version 0 is an empty file (migration 1 creates `schema_version` itself). A shipped migration is frozen: `tests/fixtures/schema/v{N}.sql` snapshots the DDL at version N with one row in every table, and `LibraryMigratorTests` proves each snapshot matches what the migrations produce, migrates it to the latest version with its rows intact, and ends with exactly the fresh schema. A schema change is therefore always a new migration plus a new snapshot, never an edit to `LibrarySchema.V1`. A database whose version is newer than the build is refused (`LibraryDatabaseException`, `NewerVersion`) and left untouched.

**Connections.** `LibraryDatabase` opens the file once (WAL is switched on there and persists in the file), then hands out `Microsoft.Data.Sqlite` pooled connections with `foreign_keys` on and `synchronous = NORMAL`. `AcquireWriterAsync` is a process-wide writer lease so the scanner and UI writes never contend on SQLite's busy timeout; readers need no lease. `OpenInMemory` gives repository tests a shared-cache in-memory database at the current schema.

**FTS maintenance.** `track_fts` is contentless and is updated by the repository in the same transaction as the track write, using `INSERT INTO track_fts(rowid, ...)` and the `'delete'` command. A full rebuild command exists for repair. Trigram tokenizer gives substring matching for "as you type" search with no prefix-index tuning.

**Artist splitting.** Tag values are split on `;`, `/`, ` feat. `, ` ft. ` and `,` only when the `ARTISTS` multi-value tag is absent. Splitting rules are a settings toggle because some artist names contain those separators.

**Album identity.** An album is `(title, album artist, year)`. Album artist falls back to the first track artist; a folder containing tracks with three or more distinct artists and no album-artist tag is treated as a compilation with album artist "Various Artists". Tracks with no album tag go to a per-folder pseudo-album titled after the folder name.

## Repository layer

`Tunqio.Library` exposes interfaces, all async, all cancellable, none of which return live database objects:

```csharp
public interface ITrackRepository
{
    Task<TrackDto?> GetAsync(long id, CancellationToken ct);
    Task<IReadOnlyList<TrackDto>> GetByIdsAsync(IReadOnlyList<long> ids, CancellationToken ct);
    Task<IReadOnlyList<TrackDto>> ListAsync(TrackQuery query, CancellationToken ct);   // sort, filter, paging
    IAsyncEnumerable<TrackDto> StreamAsync(TrackQuery query, CancellationToken ct);      // for virtualised lists
    Task<int> CountAsync(TrackQuery query, CancellationToken ct);
    Task UpsertBatchAsync(IReadOnlyList<ScannedTrack> tracks, CancellationToken ct);    // scanner only
    Task MarkMissingAsync(IReadOnlyList<long> ids, bool missing, CancellationToken ct);
    Task UpdateTagsAsync(long id, TagEdit edit, CancellationToken ct);
}

public interface IAlbumRepository { /* list with art hash and track count; detail with tracks grouped by disc */ }
public interface IArtistRepository { /* list with album/track counts; detail with albums and appearances */ }
public interface IPlaylistRepository { /* CRUD, reorder as (from, to) moves, bulk add */ }
public interface IPlayHistoryRepository { /* record event, recently played, most played, per-track stats */ }
public interface ISearchService { Task<SearchResults> SearchAsync(string text, int limit, CancellationToken ct); }
public interface ISettingsStore { T Get<T>(string key, T fallback); Task SetAsync<T>(string key, T value); }
```

Rules:
- One `SqliteConnection` per operation from a small pool; WAL mode allows concurrent readers with the single scanner writer.
- All list queries are keyset-paged (`WHERE (sort_key, id) > (?, ?) LIMIT n`) so virtualised UI lists never OFFSET-scan 100k rows.
- `TrackQuery` is a plain record (sort field, direction, filters by album/artist/genre/folder/text, page) translated to SQL by one query builder with unit tests per filter combination.
- Writes from the scanner are batched in transactions of 500 tracks. UI writes (rating, playlist edit) are single small transactions.
- Every repository method is covered by an integration test against an in-memory `:memory:` database seeded from a fixture.

## Scanner

The scanner is a pipeline of stages connected by bounded `System.Threading.Channels`:

```
Enumerate ──► Diff ──► ReadTags ──► ExtractArt ──► Upsert ──► Index
 (1 thread)  (1)      (N workers)   (2 workers)   (1 writer)  (in Upsert txn)
```

1. **Enumerate** walks each enabled `library_folder` with `Directory.EnumerateFiles` (`EnumerationOptions` with `IgnoreInaccessible`, `RecurseSubdirectories`, attributes to skip hidden/system), filtering by the supported extension set. Emits `(path, size, mtime)`.
2. **Diff** compares against a snapshot of `(path → size, mtime)` loaded once at scan start. New or changed files continue; unchanged files are counted and dropped; paths in the snapshot but not seen are collected and marked `missing = 1` at the end (never deleted; a rescan that finds them clears the flag, and a Settings action purges missing tracks older than 30 days).
3. **ReadTags** runs `Parallel.ForEachAsync` with degree `max(2, cores/2)` at `ThreadPriority.BelowNormal`. Each file is opened with TagLibSharp in read-only mode with a 5 s per-file timeout; failures are logged and the file is recorded with file-name-derived metadata and `codec` from the extension so it still plays. Duration comes from the tag reader; if absent, `BASS_ChannelGetLength` on a decode stream is used (slow path, counted in scan status).
4. **ExtractArt** takes the first embedded picture (front cover preferred), else the first of `cover.*`, `folder.*`, `front.*`, `*.jpg` in the file's folder. Hashes the bytes; if the hash directory exists, skips. Otherwise decodes with `Windows.Graphics.Imaging` and writes the three sizes plus `palette.json` (median-cut over 300 px image, five colours, sorted by population, with relative luminance).
5. **Upsert** is the only database writer. It resolves artists, albums and genres through an in-memory cache for the scan's duration, batches 500 rows per transaction, and updates `track_fts` in the same transaction.
6. **Progress** is reported via `IProgress<ScanProgress>` (files seen, processed, added, updated, failed, current path) at most 4 times a second; the UI shows it in the sidebar footer and in Settings → Library.

**Live updates.** One `FileSystemWatcher` per library folder with `IncludeSubdirectories`, buffer 64 KB. Events are debounced 2 s per path and coalesced; renames are treated as remove plus add. A watcher `Error` event (buffer overflow) triggers a folder rescan. Watchers are paused during a full scan.

**Scheduling.** Full incremental scan on launch after the UI is interactive (delay 3 s), on folder add, and on demand. Scan is cancellable; a cancelled scan leaves the database consistent because each batch is a transaction.

**Scan performance targets.** 10k new files in under 90 s on the reference machine from SSD; 100k unchanged files diffed in under 10 s.

## Tag writing

`ITagWriter.WriteAsync(path, TagEdit)` runs on a library worker, writes with TagLibSharp, then re-reads the file and upserts it so the database reflects exactly what was written. Batch edits are applied per file with a progress dialog and a per-file failure list; the user confirms once before writing begins. An in-session undo stack holds the previous `TagEdit` per file. Files currently being played are edited after playback moves on, or immediately for formats where TagLibSharp writes without truncation (FLAC with padding); the UI explains the delay.

## Play queue model

`PlayQueue` (in `Tunqio.Core`, no database dependency) is a value-typed model:

```csharp
public sealed record QueueItem(long TrackId, Guid InstanceId);   // InstanceId lets the same track appear twice
public sealed class PlayQueue
{
    IReadOnlyList<QueueItem> Items { get; }        // in play order (shuffled order when shuffle on)
    int? CurrentIndex { get; }
    bool Shuffle { get; }
    RepeatMode Repeat { get; }                     // Off | All | One
    // mutations return a new PlayQueue (immutable) so the UI can diff
    PlayQueue PlayNow(IEnumerable<long> ids);      // replaces queue, starts at 0
    PlayQueue PlayNext(IEnumerable<long> ids);     // inserts after current
    PlayQueue Enqueue(IEnumerable<long> ids);      // appends
    PlayQueue Remove(Guid instanceId);
    PlayQueue Move(Guid instanceId, int toIndex);
    PlayQueue ToggleShuffle(Random rng);           // shuffles the *remaining* items, keeps history order
    PlayQueue Advance(bool manual);                // applies repeat rules; manual skip in Repeat One moves on
    PlayQueue Back(TimeSpan position);             // < 3 s into track → previous, else restart (UI rule, applied by session)
    QueueItem? PeekNext();                         // what the engine should pre-open for gapless
}
```

Shuffle keeps the original order so turning shuffle off restores it with the current track in place. `PeekNext` is what `PlaybackSession` hands to the engine to pre-buffer.

## Play history rules

A `play_event` is recorded when a track stops being current. `completed = 1` when heard time exceeds 50% of duration or 4 minutes, whichever is smaller (the Last.fm rule, so 1.1 scrobbling can reuse events). `play_count` and `last_played_at` on `track` are updated in the same transaction only for completed plays. "Recently played" orders by `last_played_at`; "Most played" by `play_count` then `last_played_at`.

## Durability

Playlists and ratings are user work that a rescan cannot recreate. On every playlist mutation the playlist is exported to `exports\playlists\<name>.m3u8` (debounced 5 s) with `#EXTINF` lines; ratings are written to the file tag (`RATING` / POPM) when the user enables "write ratings to files" (default off) and are otherwise in the database only. Settings → Library offers "Import playlists from exports" for recovery after a database reset.

## Failure handling

| Failure | Behaviour |
|---------|-----------|
| Database corrupt on open | Rename to `library.corrupt-<timestamp>.db` (with its -wal/-shm), create fresh, show an InfoBar notice: rescan, and import playlists from exports. "Corrupt" is what SQLite reports while opening, reading the schema and migrating (SQLITE_CORRUPT / SQLITE_NOTADB), plus a SQLite file that is not a Tunqio library. The open path runs no `quick_check` (about 280 ms on the 100k fixture against the 500 ms open budget); `LibraryDatabase.QuickCheck` backs the diagnostics page instead |
| Database from a newer build | Refused, file left in place, InfoBar error; the app runs without a library |
| Folder offline (network share, removable drive) | Tracks marked missing after scan; not purged; playback of a missing track skips with a transient notice |
| Tag read exception | Track added with file-name metadata, flagged in scan report |
| Art decode failure | No art, album uses placeholder derived from album title hash colour |
| FTS out of sync | Settings → Library → Rebuild search index |

## Fixtures for testing

`tests/fixtures/library/` contains a generated mini-library: 60 files across 8 albums covering every supported format (1 s of silence or a sine sweep with correct tags), a compilation with no album artist, a multi-disc album, a track with three artists, a file with corrupt tags, an album with folder art only, and unicode paths. `tests/fixtures/library-100k.db` is a generated database (not files) for repository and search benchmarks.

Both come from `tools/FixtureGen` (E0-S7). The 60 files are committed together with `manifest.json` (per-file SHA-256, expected tags and audio properties); `Tunqio.Library.Tests` checks the committed bytes and tags against the manifest and that two generation runs are byte-identical, `Tunqio.Interop.Tests` opens every file in the native core. PCM is synthesised in code (WAV and AIFF written natively); FLAC, MP3, AAC, ALAC, Vorbis, Opus, WMA and WavPack are encoded with ffmpeg in bit-exact mode, so regenerating needs ffmpeg on PATH. APE and MPC have no available encoder and are not in the fixture set (card T-88). The database is not committed; `FixtureGen db` rebuilds it (100 000 tracks, 6 250 albums, 3 125 artists, about 48 MB) from the v1 schema in `Tunqio.Library.Schema.LibrarySchema` with a fixed seed.
