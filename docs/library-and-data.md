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
  art_hash        TEXT,                       -- key into art\ cache, derived from the tracks after every batch (lowest disc/track with an embedded picture, else the folder image); NULL = no art
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
  art_hash       TEXT,                        -- the file's embedded picture; NULL when it has none (album.art_hash covers it)
  mbid           TEXT,
  added_at       INTEGER NOT NULL,
  rating         INTEGER,                     -- 0..100, user data
  play_count     INTEGER NOT NULL DEFAULT 0,  -- denormalised from play_event
  last_played_at INTEGER,
  missing        INTEGER NOT NULL DEFAULT 0   -- file not found at last scan; hidden but retained
  -- v2 (E3-S12): missing_since INTEGER, when the file was first found missing; NULL while present. Purge missing deletes rows flagged for over 30 days.
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

**Migrations (E3-S1).** `Tunqio.Library.Database.LibraryMigrations.All` is the ordered, append-only list of every schema version; `LibraryMigrator` applies the pending ones on open, each in its own transaction followed by its `schema_version` row, so a crash mid-upgrade leaves the previous version intact. Version 0 is an empty file (migration 1 creates `schema_version` itself). A shipped migration is frozen: `tests/fixtures/schema/v{N}.sql` snapshots the DDL at version N with one row in every table, and `LibraryMigratorTests` proves each snapshot matches what the migrations produce, migrates it to the latest version with its rows intact, and ends with exactly the fresh schema. A schema change is therefore always a new migration plus a new snapshot, never an edit to `LibrarySchema.V1`. A database whose version is newer than the build is refused (`LibraryDatabaseException`, `NewerVersion`) and left untouched. Shipped: v1 the initial schema; v2 (E3-S12) `ALTER TABLE track ADD COLUMN missing_since INTEGER`, stamping rows already flagged with the upgrade's clock (SQLite's `now`, the earliest moment the build can vouch for). A snapshot of an `ALTER`ed table keeps the text SQLite leaves in `sqlite_master`, the new column appended after the original DDL's last newline with no whitespace before the closing paren, because the migrator test compares that text whitespace-normalised.

**Connections.** `LibraryDatabase` opens the file once (WAL is switched on there and persists in the file), then hands out `Microsoft.Data.Sqlite` pooled connections with `foreign_keys` on and `synchronous = NORMAL`. `AcquireWriterAsync` is a process-wide writer lease so the scanner and UI writes never contend on SQLite's busy timeout; readers need no lease. `OpenInMemory` gives repository tests a shared-cache in-memory database at the current schema.

**FTS maintenance.** `track_fts` is contentless and is updated by the repository in the same transaction as the track write, using `INSERT INTO track_fts(rowid, ...)` and the `'delete'` command. A full rebuild command exists for repair. Trigram tokenizer gives substring matching for "as you type" search with no prefix-index tuning.

**Search (E3-S9).** `Tunqio.Library.Repositories.SqliteSearchService` splits the text on whitespace. A term of three or more characters is a quoted trigram phrase (substring, case-insensitive, all terms AND-ed across the row's columns); a shorter term is a `LIKE`, beside the MATCH or on its own when no term is long enough, so the first keystrokes still answer. Each group is three layers: a candidate set (present tracks only, cut at 1000 track rows, or 2000 rows behind albums and artists), a ranking of those ids on cheap columns (a title or name starting with the text first, then alphabetical), and the full row select for the winners plus one, which sets the group's more-flag. Albums come from tracks whose `{album album_artist}` columns matched, with per-album aggregates correlated for the winners only (the Albums grid's whole-table aggregate join costs 42 ms on 100k); artists are the names containing every term among the artists of tracks whose `{artists album_artist}` columns matched (the `artists` column is a joined list, so a column match alone would leak co-credits). Ranking every match with `bm25()` was measured at 47 ms p95 for a three-character term on the 100k database (41k matches); the capped shape measures a few milliseconds per group. `RebuildIndexAsync` (Settings › Library › Rebuild search index) issues `'delete-all'` then one `INSERT ... SELECT` over every track row from the same derivation the maintenance uses (`FtsSql`), under the writer lease in one transaction: about 1.2 s for 100k rows.

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
    Task<IReadOnlyList<TrackFileStamp>> SnapshotAsync(long folderId, CancellationToken ct); // scanner only: (id, path, size, mtime, missing) per folder
    Task<bool> SetRatingAsync(long id, int? rating, CancellationToken ct);             // E6-S7: the row only; no tag write lives here (T-115)
}

public interface IAlbumRepository { /* Get, GetDetail (tracks in disc/track order + genres), List(AlbumQuery), Count */ }
public interface IArtistRepository { /* Get, GetDetail (own albums + "appears on"), List(ArtistQuery), Count */ }
public interface IGenreRepository { /* List: every genre with its present-track count */ }
public interface ILibraryFolderRepository { /* List, Add (normalised path, idempotent), SetEnabled, Remove (cascades tracks), RecordScan */ }
public interface ILibraryService { ITrackRepository Tracks; IAlbumRepository Albums; IArtistRepository Artists; IGenreRepository Genres; ILibraryFolderRepository Folders; IPlaylistRepository Playlists; ISearchService Search; ILibraryScanner Scanner; }
public interface ILibraryScanner { bool IsScanning; Task<ScanReport> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken ct); }   // E3-S5
public interface IPlaylistRepository { /* E6-S1: List (pinned first, then name, with item count and total duration), GetDetail (tracks in playlist order; a track may appear twice), Create / Rename (trimmed, blank refused), Delete (items cascade), AddTracks (appended in order, unknown ids skipped), RemoveAt(positions), Move(fromPosition, toPosition), ReplaceTracks (E5-S4: the whole list in one write, unknown ids skipped, which is what Curation's undo and redo write), SetPinned (E7-S5: sets or clears pinned for the jump list, leaves modified_at alone, raises Changed only when the flag changed, false for an unknown id); positions 0-based and contiguous, rewritten in one transaction because (playlist_id, position) is the key; every change stamps modified_at */ }
public interface IPlayHistoryRepository { /* record event, recently played, most played, per-track stats (E3-S11) */ }
public interface ISearchService { Task<SearchResults> SearchAsync(string text, SearchLimits limits, CancellationToken ct); Task<int> RebuildIndexAsync(CancellationToken ct); }   // E3-S9: groups of tracks, albums and artists with a more-flag each
```

The contracts and DTOs live in `Tunqio.Core.Library` (no SQLite); the SQL implementations in `Tunqio.Library.Repositories` (E3-S2).

Rules:
- One `SqliteConnection` per operation from the pool (`LibraryDatabase.OpenConnectionAsync`); WAL mode allows concurrent readers with the single writer, and every write holds `LibraryDatabase.AcquireWriterAsync` for its one transaction.
- All list queries are keyset-paged (`WHERE (k1, k2, ..., id) > (?, ?, ..., ?) LIMIT n`) so virtualised UI lists never OFFSET-scan 100k rows. The cursor is a `PageCursor` (the sort keys and id of the last row) obtained from `TrackQuery.CursorAfter(row)` / `AlbumQuery.CursorAfter(row)`; `StreamAsync` drives it internally. Every sort-key expression coalesces nulls to a sentinel (`SortKeys`: U+FFFF for text, `long.MaxValue` for times, `int.MaxValue` for years, -1 for ratings) so a null never breaks the row-value comparison, and text keys use `COLLATE NOCASE`. `TrackQuery.SortKeysOf(row)` reproduces the SQL keys from a DTO, which is what makes the cursor and the tests' oracle agree by construction.
- `TrackQuery` is a plain record (sort field, direction, filters by album/artist/genre/folder/text, missing, played-only, page size, total cap) translated to SQL by one query builder; `TrackRepositoryTests` pages every sort × direction × filter combination against a LINQ oracle over the same rows. Sorts: title, artist (first credit, then album order), album (title, disc, track), duration, year, added, last played, play count, rating, codec.
- Writes from the scanner are batched in transactions of 500 tracks (`UpsertBatchAsync`: 500 new tracks with credits, genres and FTS on the 100k database in well under 150 ms). Inside the batch, artists, genres and albums are resolved by name (case-insensitive) through a per-transaction cache; album identity is `(title, album artist, year)` with the album artist falling back to the first credited artist; artist `sort_name` moves a leading "The"/"A"/"An" to the end. A re-upserted path keeps its id, `added_at`, rating and play history. A rating is a single small write (`SetRatingAsync`). A tag edit has no row write of its own: it reaches the database through a targeted rescan of the files it wrote, the same upsert the scanner uses, which is the one write path for tags (E3-S10; T-115 deleted the unused row-level tag write).
- **FTS5 and write order.** `track_fts` is contentless, so a row is removed by re-supplying the exact values that were indexed; the repository derives them from the row (title; credited artists joined with ", " in credit order; album title; album artist) both when indexing and when un-indexing. The batch upsert gathers every FTS write after the last track write: a track statement that follows an FTS insert in the same transaction makes FTS5 flush its pending terms, which measured at 280 ms per 500 interleaved rows against 10 ms gathered.
- Track rows share their repeated values through the repository's `StringPool` (album title and artist, codec, art hash, composer, and the `ArtistRef` credit objects by artist id): a list that has paged through 100k rows keeps one instance of each rather than one per row. The E3-S3 spike measured 708 B retained per `TrackDto` before pooling; the pool is bounded (65 536 entries, then it starts again) and thread-safe.
- Every repository method is covered by an integration test against an in-memory database seeded from the fixture manifest (`LibraryDatabase.OpenInMemory`).

## Scanner

The scanner is a pipeline of stages connected by bounded `System.Threading.Channels`:

```
Enumerate ──► Diff ──► ReadTags ──► ExtractArt ──► Upsert ──► Index
 (1 thread)  (1)      (N workers)   (2 workers)   (1 writer)  (in Upsert txn)
```

1. **Enumerate** walks each enabled `library_folder` with a `FileSystemEnumerable` (`EnumerationOptions` with `IgnoreInaccessible`, `RecurseSubdirectories`, attributes to skip hidden/system), filtering by the supported extension set on the entry's name before it is allocated. The entry carries size and mtime, so there is no second stat per file. A folder whose root is not there (network share, unplugged drive) is `offline`: its tracks are all marked missing, nothing is read, and the folder's `last_scan_status` says so.
2. **Diff** runs on the same thread against `ITrackRepository.SnapshotAsync(folderId)`, the `(path → id, size, mtime, missing)` of every row under the folder, loaded once per folder. New or changed files continue; unchanged files are counted and dropped, and an unchanged file that was flagged missing is queued to have the flag cleared without a read. Paths in the snapshot but not seen are marked `missing = 1` only after the walk has finished, in `MarkMissingAsync` chunks of 500, so an interrupted walk never flags files it did not reach (never deleted; a rescan that finds them clears the flag). The first marking stamps `missing_since` and later scans that still miss the file keep it; clearing the flag, by a rescan that finds the file unchanged or an upsert that re-reads it, forgets the stamp. Settings › Library › Purge missing is `ITrackRepository.PurgeMissingAsync(cutoff)` (with `CountMissingAsync` for the button's count): one transaction that drops the FTS rows by hand (contentless tables do not cascade) and deletes every track flagged since before the cutoff, 30 days before now, so credits, genres, playlist entries and play history cascade with it; a file that returns after a purge is a new track. `ScanRequest.ForceReread` (Settings → Library → Rescan) sends every file through regardless of its stamp. Files travel on **per directory**: the compilation rule needs a folder's worth of tracks, and when a changed file in a directory has no album-artist tag and the directory has unchanged siblings, the siblings are read again so the rule sees the whole folder (they are re-upserted unchanged, and the report still counts them as unchanged).
3. **ReadTags** runs `Parallel.ForEachAsync` over directories and over the files within one, with total concurrency bounded to `max(2, cores/2)` by a semaphore. Each file goes through `ITagReader` (`Tunqio.Core.Library`, implemented by `TagLibTagReader`, E3-S4, see "Tag reader" below) with its 5 s per-file timeout; a failure is logged, listed in the report's `Failures`, and the file is recorded with file-name-derived metadata and `codec` from the extension so it still plays. Duration comes from the tag reader; if it is 0, `IDurationProbe` (the engine's decode stream, bound by the host; absent until then) is asked and the file counted in `SlowPath`. Pool threads are shared, so the documented below-normal priority is not set; the timeout bounds what one file can cost.
4. **ExtractArt** is `IArtCache.StoreAsync(embeddedPicture, audioPath)` behind a two-worker gate, implemented by `Tunqio.Library.Art.ArtCache` (E3-S7). It takes the embedded picture the reader already found (front cover preferred), else the first of `cover.*`, `folder.*`, `front.*`, `*.jpg` in the file's folder (the lookup is memoised per directory against the directory's and the image's stamps, so ten tracks of one folder read the image once), hashes the bytes (SHA-256, lower-case hex: the value of `art_hash`), skips when the hash directory is whole (`palette.json`, written last, is the check), and otherwise decodes with `Windows.Graphics.Imaging` (EXIF orientation applied, colour managed to sRGB) and writes the three sizes as JPEG quality 85 at the source aspect and never upscaled, `original.jpg|png` for a JPEG or PNG source under 4 MB, and `palette.json` (median cut over the 300 px rendering at 5 bits per channel, five colours sorted by population, each with its WCAG relative luminance; an image with fewer distinct colours has the remaining slots filled with darker shades of the dominant one at population 0), all into a temporary directory that is moved into place so a crash leaves nothing a later scan trusts. It returns the embedded picture's hash as both track and album hash, else the folder image's as the album hash only: `track.art_hash` is embedded art, and `album.art_hash` is derived again at the end of every upsert batch from the album's tracks as they stand (the lowest disc and track number that carries a picture, else the folder image the batch reported, else none), so a cover removed from every track takes the album's art with it and a new cover on the first track becomes the album's (T-207; before that the album kept the first hash it was ever given). The same hash in flight twice shares one render; an image WIC cannot decode is logged and treated as no art, and the folder image tried next. Reads are by hash: `PathFor(hash, size)` derives the file path without I/O (a value that is not a 64-digit hash, such as the placeholders in the synthetic 100k database, gives `null`, and a consumer treats a file that fails to load as no art), `LoadPaletteAsync` reads the palette (`{"version":1,"colors":[{"hex","population","luminance"}×5]}`), and `ClearAsync` empties the cache for Settings › Library › Regenerate art, after which a forced rescan fills it again. In the app, `AlbumArt.Tile/Large/Thumbnail(art_hash)` turn a hash into a `BitmapImage` for XAML. Measured on the development machine (Debug, 1 000 album folders of ten tagged WAVs each carrying a distinct 300 px PNG cover, file database): the first scan with art 8.2 s against 1.6 s without, and regenerating from scratch (clear, then forced re-read) 6.7 s for the 1 000 albums; `ArtCachePerformanceTests` holds the 60 s budget stated for the reference machine (T-90).
5. **Upsert** is the only database writer. It fills batches of exactly 500 rows and writes each with `UpsertBatchAsync` (one transaction: artists, albums and genres resolved through the transaction's cache, `track_fts` updated in step). Cancellation is checked inside the batch, so a cancelled scan keeps every batch that committed and nothing of the one in flight; the report says `Cancelled` and the folder's status `cancelled`, and the next scan carries on from the snapshot without re-reading what committed.
6. **Progress** is reported via `IProgress<ScanProgress>` (phase, files seen, processed, added, updated, unchanged, failed, slow-path, current path) at most 4 times a second plus once at the end; the UI shows it in the sidebar footer and in Settings → Library. The `ScanReport` returned by `ScanAsync` carries the same totals, the elapsed time, the per-file failures, one `ScanFolderReport` per folder that ran to its end, and the outcome (`Completed`, `Cancelled`, `Failed` with the error). One scan runs at a time: a second `ScanAsync` while `IsScanning` throws.

The scanner is `Tunqio.Library.Scanning.LibraryScanner`, reached as `ILibraryService.Scanner` (or `ILibraryScanner` from DI). Measured on the development machine (Debug, 10 000 tagged WAVs in 1 000 album folders, file database, read degree 4): first scan 1.5 s, unchanged rescan 0.06 s; `ScanPerformanceTests` holds the reference-machine budgets below, and `tools/FixtureGen tree` writes the same tree for a manual run.

**Tag reader.** `ITagReader.ReadAsync(path, folderId)` never throws for a bad file: it returns a `TagReadResult` whose `ScannedTrack` is always usable and whose `Outcome` says where the metadata came from (`Read`, `NoTags`, `CorruptTags`, `Unsupported`, `TimedOut`, `Failed`; the last four are scan-report failures, `NoTags` is an untagged file). TagLibSharp is synchronous, so each read runs on the pool and is abandoned after the timeout (the parser keeps its thread until it returns; the scan does not wait). A file the parser cannot read is recorded from its path: `04 - Title.mp3` gives track 4 and "Title", a `1-04` prefix or a `Disc 2`/`CD1` folder gives the disc, the containing folder (or the disc folder's parent) is the album, artists are empty, `codec` comes from the extension and `duration_ms` is 0 for the slow path to fill. A damaged ID3v2 header is recognised by the reader itself (`ID3` followed by an impossible version or a non-sync-safe size): TagLibSharp reports an empty tag for it and then mis-locates the first audio frame, so its audio properties are discarded too. `codec` uses one vocabulary (`AudioFormats` in Core): `mp3 flac aac alac vorbis opus wav aiff wma wavpack ape mpc`, decided by the container type and, for MP4 and Ogg, the stream entry; an Opus file has no nominal bitrate, so the file's average is stored. ReplayGain and MusicBrainz ids map straight from the tag; the embedded front cover (or first picture) comes back in the result for the ExtractArt stage so it does not reopen the file. Values are trimmed and composed to Unicode form C before they reach the repositories. The compilation rule needs a folder's worth of tracks, so it is a separate pure step (`CompilationRule.Apply`) the scanner runs per directory on the reader's output; the artist-splitting toggle is read from settings on every call. Gapless data (encoder delay/padding) is not extracted: nothing stores it and the native core reads it from the stream when it opens the file.

**Live updates.** `ILibraryWatcher` (`Tunqio.Library.Scanning.LibraryWatcher`, reached as `ILibraryService.Watcher`; E3-S6) keeps one `FileSystemWatcher` per enabled library folder with `IncludeSubdirectories`, a 64 KB buffer and `FileName | DirectoryName | LastWrite | Size`. The shell starts it once the window is up and calls `RefreshAsync` after Settings changes the folder list; a folder whose root is away gets no watch until a refresh finds it back. Events are keyed by path per folder and due 2 s after the last event on that path (debounce); paths due together travel in one request (coalesce). The request is a *targeted* scan, `ScanRequest.Targeted(folderId, paths)`, which runs the ordinary pipeline over scopes instead of the root: a directory that exists is walked, a file is diffed with the one directory it is in (so the compilation rule still sees the folder and unchanged siblings are dropped by Diff), and a path that is gone is marked missing, the whole subtree when the snapshot knew it as a directory. Missing marking never reaches outside the scopes, and a targeted scan does not write the folder's last-scan record. A rename is its old path and its new one, which the scanner turns into the documented remove plus add (the new row keeps nothing of the old one). What is taken: a supported audio file always; a deleted or renamed-away path always, because a subtree moved out gives one event for the directory only; a created or renamed-in directory; nothing else (a directory's own `Changed`, which every child event already implies, and files the library does not index). A watch `Error`, which is the buffer overflowing under a mass copy, drops the folder's pending paths and queues one rescan of the whole folder; so does a pending set past 4 096 paths. Upserts are keyed by path, so a rescan after targeted scans never duplicates a row. One pump runs the watcher's scans in turn; while any other scan is running (launch, Settings) the work waits and nothing is dropped, which is what "paused during a full scan" means. `ILibraryScanner.ScanCompleted` fires after every scan, manual or watched, with the report; views refresh from it. Measured on the development machine: a file dropped into a watched folder is in the library about 2.1 s later (the debounce plus a 60-file directory listing); 5 000 tagged files written in 3.6 s overflowed a 4 KB test buffer once and were complete 9.4 s after the copy began, one folder rescan, no duplicates.

**Scheduling.** Full incremental scan on launch after the UI is interactive (delay 3 s), on folder add, and on demand; the watcher covers everything in between. Scan is cancellable; a cancelled scan leaves the database consistent because each batch is a transaction. The triggers live in the shell, in `Tunqio.App.Library.LibraryScanCoordinator` (E3-S12): `StartLaunchScan` after the window activates, `ScanRequest.Folder(id)` from Settings › Library › Add folder, and the page's Rescan (all or one folder, `ForceReread`). One shell scan runs at a time (a second request is declined; the page already shows the running one) and a shell scan that arrives while the watcher's scan is running waits for it, polling `IsScanning` every 250 ms and retrying the scanner's one-at-a-time refusal, so neither side fails on the other. The coordinator relays progress and keeps the last report for the page; it also listens to `ScanCompleted` from every origin and, for a report that added, updated, flagged or restored anything (or when Settings removed a folder or purged tracks), bumps its `LibraryVersion` and raises `LibraryChanged` on the UI thread. The library pane asks the page showing to reload (`ILibraryRefreshable`), and a page the frame cached reloads when it comes back if the version moved since it loaded (`LibraryFreshness`).

**Scan performance targets.** 10k new files in under 90 s on the reference machine from SSD; 100k unchanged files diffed in under 10 s.

## Tag writing

`ITagWriter.WriteAsync(path, TagEdit)` runs on a library worker, writes with TagLibSharp, then `ITagEditor` rescans exactly the written paths so the database reflects exactly what was written. That targeted rescan is the only way a tag edit reaches the database: there is no row-level tag write in `ITrackRepository` (T-115), because an upsert built from the tag reader alone would clear the track's `art_hash` and miss its new size, mtime and album identity. Batch edits are applied per file with a progress dialog and a per-file failure list; the user confirms once before writing begins. An in-session undo stack holds the previous `TagEdit` per file. Files currently being played are edited after playback moves on, or immediately for formats where TagLibSharp writes without truncation (FLAC with padding); the UI explains the delay.

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

A `play_event` is recorded when a track stops being current. `completed = 1` when heard time exceeds 50% of duration or 4 minutes, whichever is smaller (the Last.fm rule, so 1.1 scrobbling can reuse events). `play_count` and `last_played_at` on `track` are updated in the same transaction only for completed plays. "Recently played" orders by `last_played_at`; "Most played" by `play_count` then `last_played_at`. Both views query with `PlayedOnly`: a never-played track's last-played sort key is the after-everything sentinel, so without the filter it would lead a descending list.

## Durability

Playlists and ratings are user work that a rescan cannot recreate. On every playlist mutation the playlist is exported to `exports\playlists\<name>.m3u8` with `#PLAYLIST` and `#EXTINF` lines and full paths, within 5 s: the first change opens a 4 s window and every change inside it joins the same write, so a long editing session cannot postpone the export (E6-S2, `Tunqio.Library.Playlists.PlaylistFiles`, following `IPlaylistRepository.Changed`). A renamed or deleted playlist's old file is removed only when this process wrote it, so the first launch after a database reset never deletes the exports it needs; start-up writes every playlist once, catching up a change made in the last window before a crash, and shutdown flushes the window. Exports the user asks for (playlist page, Curation, Settings) are written relative to the file's folder where the track is on the same drive; ratings are written to the file tag when the user enables "write ratings to files" (default off, OQ-7) and are otherwise in the database only. As built (E6-S7, `ITrackRater` / `Tunqio.Library.Tags.TrackRater`): the row is written first through `ITrackRepository.SetRatingAsync` and every view follows the rater's `Changed` event; the file is then written through the E3-S10 tag writer as a one-field edit (temp copy, save, verify by read-back, replace), so the playing file's write waits for playback to release it exactly as the editor's do, a write that fails is a transient notice and the library rating stands, and nothing rescans afterwards because the rating never comes from the file. The file mapping is one table, `Tunqio.Library.Tags.TagRatings`: ID3v2 (MP3, and the ID3 chunk of WAV and AIFF) a `POPM` frame under "Windows Media Player 9 Series" with bytes 1/64/128/196/255 for one to five stars, the frame Explorer and Windows Media Player read; Vorbis comments (FLAC, Ogg Vorbis, Opus) and APEv2 (WavPack) a `RATING` field holding 0..100; MP4 (AAC, ALAC) the `rate` atom holding 0..100; ASF (WMA) `WM/SharedUserRating` at 1/25/50/75/99. Reading folds any of those scales (0..100, 1..5, 0.0..1.0, the POPM bands) to whole stars, so the verify after a write compares like with like. Settings → Library offers "Import playlists from exports" for recovery after a database reset.

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

Both come from `tools/FixtureGen` (E0-S7), as does `tests/fixtures/gapless/` (E1-S2: chirp pairs for the join measurement). The 60 files are committed together with `manifest.json` (per-file SHA-256, expected tags and audio properties); `Tunqio.Library.Tests` checks the committed bytes and tags against the manifest and that two generation runs are byte-identical, `Tunqio.Interop.Tests` opens every file in the native core. PCM is synthesised in code (WAV and AIFF written natively); FLAC, MP3, AAC, ALAC, Vorbis, Opus, WMA and WavPack are encoded with ffmpeg in bit-exact mode, so regenerating needs ffmpeg on PATH. APE has no available encoder and is not in the fixture set; MPC is not a 1.0 format (Q-26 on T-88). The database is not committed; `FixtureGen db` rebuilds it (100 000 tracks, 6 250 albums, 3 125 artists, about 48 MB) from the v1 schema in `Tunqio.Library.Schema.LibrarySchema` with a fixed seed.
