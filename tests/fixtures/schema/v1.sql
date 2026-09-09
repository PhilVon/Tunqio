-- Frozen snapshot of library schema version 1 (docs/library-and-data.md, "Schema (v1)") with one row in every
-- table. Loaded by LibraryMigratorTests: the runner must bring it to the latest version and end with exactly the
-- schema a fresh database gets, with these rows intact. Never edit a shipped snapshot; add v{N}.sql when
-- migration N ships (copy the DDL the migrations produce at that version).

CREATE TABLE schema_version (version INTEGER NOT NULL, applied_at INTEGER NOT NULL);

CREATE TABLE library_folder (
  id           INTEGER PRIMARY KEY,
  path         TEXT NOT NULL UNIQUE,
  enabled      INTEGER NOT NULL DEFAULT 1,
  last_scan_at INTEGER,
  last_scan_status TEXT
);

CREATE TABLE artist (
  id         INTEGER PRIMARY KEY,
  name       TEXT NOT NULL,
  sort_name  TEXT NOT NULL,
  mbid       TEXT,
  UNIQUE (name COLLATE NOCASE)
);

CREATE TABLE album (
  id              INTEGER PRIMARY KEY,
  title           TEXT NOT NULL,
  album_artist_id INTEGER REFERENCES artist(id),
  year            INTEGER,
  disc_count      INTEGER,
  art_hash        TEXT,
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
  path           TEXT NOT NULL UNIQUE,
  source_kind    TEXT NOT NULL DEFAULT 'local',
  file_size      INTEGER NOT NULL,
  file_mtime     INTEGER NOT NULL,
  title          TEXT NOT NULL,
  album_id       INTEGER REFERENCES album(id),
  track_no       INTEGER,
  disc_no        INTEGER,
  year           INTEGER,
  duration_ms    INTEGER NOT NULL,
  bitrate_kbps   INTEGER,
  sample_rate    INTEGER,
  channels       INTEGER,
  bit_depth      INTEGER,
  codec          TEXT NOT NULL,
  composer       TEXT,
  comment        TEXT,
  rg_track_gain  REAL,
  rg_track_peak  REAL,
  rg_album_gain  REAL,
  rg_album_peak  REAL,
  art_hash       TEXT,
  mbid           TEXT,
  added_at       INTEGER NOT NULL,
  rating         INTEGER,
  play_count     INTEGER NOT NULL DEFAULT 0,
  last_played_at INTEGER,
  missing        INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX ix_track_album ON track(album_id, disc_no, track_no);
CREATE INDEX ix_track_added ON track(added_at DESC);
CREATE INDEX ix_track_played ON track(last_played_at DESC);
CREATE INDEX ix_track_folder ON track(folder_id);

CREATE TABLE track_artist (
  track_id  INTEGER NOT NULL REFERENCES track(id) ON DELETE CASCADE,
  artist_id INTEGER NOT NULL REFERENCES artist(id),
  role      TEXT NOT NULL DEFAULT 'artist',
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
  pinned      INTEGER NOT NULL DEFAULT 0,
  kind        TEXT NOT NULL DEFAULT 'manual'
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
  played_ms  INTEGER NOT NULL,
  completed  INTEGER NOT NULL
);
CREATE INDEX ix_play_event_track ON play_event(track_id, started_at DESC);

CREATE TABLE queue_state (
  id            INTEGER PRIMARY KEY CHECK (id = 1),
  items_json    TEXT NOT NULL,
  current_index INTEGER,
  position_ms   INTEGER,
  shuffle       INTEGER NOT NULL,
  repeat_mode   TEXT NOT NULL,
  saved_at      INTEGER NOT NULL
);

CREATE TABLE setting (
  key   TEXT PRIMARY KEY,
  value TEXT NOT NULL
);

CREATE VIRTUAL TABLE track_fts USING fts5(
  title, artists, album, album_artist,
  content='',
  tokenize = 'trigram'
);

INSERT INTO schema_version(version, applied_at) VALUES (1, 1757376000000);
INSERT INTO library_folder(id, path, enabled, last_scan_at, last_scan_status) VALUES (1, 'D:\Music\', 1, 1757376000000, 'ok');
INSERT INTO artist(id, name, sort_name, mbid) VALUES (1, 'The Field Notes', 'Field Notes, The', NULL);
INSERT INTO album(id, title, album_artist_id, year, disc_count, art_hash, mbid) VALUES (1, 'Tape One', 1, 1998, 1, NULL, NULL);
INSERT INTO genre(id, name) VALUES (1, 'Folk');
INSERT INTO track(id, folder_id, path, source_kind, file_size, file_mtime, title, album_id, track_no, disc_no, year, duration_ms, bitrate_kbps, sample_rate, channels, bit_depth, codec, composer, comment, rg_track_gain, rg_track_peak, rg_album_gain, rg_album_peak, art_hash, mbid, added_at, rating, play_count, last_played_at, missing)
  VALUES (1, 1, 'D:\Music\Field Notes - Tape One (1998)\01 Morning.flac', 'local', 1234567, 1757376000000, 'Morning', 1, 1, 1, 1998, 187000, 900, 44100, 2, 16, 'flac', NULL, NULL, -6.5, 0.98, -7.1, 0.99, NULL, NULL, 1757376000000, 80, 3, 1757376000000, 0);
INSERT INTO track_artist(track_id, artist_id, role, position) VALUES (1, 1, 'artist', 0);
INSERT INTO track_genre(track_id, genre_id) VALUES (1, 1);
INSERT INTO playlist(id, name, created_at, modified_at, pinned, kind) VALUES (1, 'Favourites', 1757376000000, 1757376000000, 1, 'manual');
INSERT INTO playlist_item(playlist_id, position, track_id) VALUES (1, 0, 1);
INSERT INTO play_event(id, track_id, started_at, played_ms, completed) VALUES (1, 1, 1757376000000, 187000, 1);
INSERT INTO queue_state(id, items_json, current_index, position_ms, shuffle, repeat_mode, saved_at) VALUES (1, '[1]', 0, 12000, 0, 'off', 1757376000000);
INSERT INTO setting(key, value) VALUES ('ui.theme', '"dark"');
INSERT INTO track_fts(rowid, title, artists, album, album_artist) VALUES (1, 'Morning', 'The Field Notes', 'Tape One', 'The Field Notes');
