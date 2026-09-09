namespace Tunqio.Library.Schema;

/// <summary>
/// The library database schema, version 1, exactly as documented in docs/library-and-data.md ("Schema (v1)").
/// Frozen: this is migration 1 in <see cref="Database.LibraryMigrations"/> and tests/fixtures/schema/v1.sql
/// snapshots it. Schema changes are new migrations, never edits here.
/// </summary>
public static class LibrarySchema
{
    public const string V1 = """
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
        """;
}
