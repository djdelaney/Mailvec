-- Mailvec initial schema.
-- Apply once on a fresh database. Subsequent changes go in schema/migrations/.

PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

-- Core messages table.
CREATE TABLE messages (
    id                INTEGER PRIMARY KEY,
    message_id        TEXT UNIQUE NOT NULL,
    thread_id         TEXT,
    maildir_path      TEXT NOT NULL,
    maildir_filename  TEXT NOT NULL,
    folder            TEXT NOT NULL,
    subject           TEXT,
    from_address      TEXT,
    from_name         TEXT,
    to_addresses      TEXT,
    cc_addresses      TEXT,
    date_sent         TEXT,
    date_received     TEXT,
    size_bytes        INTEGER,
    has_attachments   INTEGER DEFAULT 0,
    body_text         TEXT,
    body_html         TEXT,
    raw_headers       TEXT,
    indexed_at        TEXT NOT NULL,
    embedded_at       TEXT,
    deleted_at        TEXT
);

CREATE INDEX idx_messages_thread    ON messages(thread_id);
CREATE INDEX idx_messages_folder    ON messages(folder);
CREATE INDEX idx_messages_date_sent ON messages(date_sent);
CREATE INDEX idx_messages_to_embed  ON messages(embedded_at) WHERE embedded_at IS NULL;

-- Full-text index over messages.
CREATE VIRTUAL TABLE messages_fts USING fts5(
    subject, from_name, from_address, body_text,
    content='messages', content_rowid='id',
    tokenize='porter unicode61'
);

CREATE TRIGGER messages_ai AFTER INSERT ON messages BEGIN
    INSERT INTO messages_fts(rowid, subject, from_name, from_address, body_text)
    VALUES (new.id, new.subject, new.from_name, new.from_address, new.body_text);
END;

CREATE TRIGGER messages_ad AFTER DELETE ON messages BEGIN
    INSERT INTO messages_fts(messages_fts, rowid, subject, from_name, from_address, body_text)
    VALUES ('delete', old.id, old.subject, old.from_name, old.from_address, old.body_text);
END;

CREATE TRIGGER messages_au AFTER UPDATE ON messages BEGIN
    INSERT INTO messages_fts(messages_fts, rowid, subject, from_name, from_address, body_text)
    VALUES ('delete', old.id, old.subject, old.from_name, old.from_address, old.body_text);
    INSERT INTO messages_fts(rowid, subject, from_name, from_address, body_text)
    VALUES (new.id, new.subject, new.from_name, new.from_address, new.body_text);
END;

-- Per-message text chunks fed to the embedder.
CREATE TABLE chunks (
    id           INTEGER PRIMARY KEY,
    message_id   INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    chunk_index  INTEGER NOT NULL,
    chunk_text   TEXT NOT NULL,
    token_count  INTEGER,
    UNIQUE(message_id, chunk_index)
);

CREATE INDEX idx_chunks_message ON chunks(message_id);

-- Vector index. Dimension MUST match Ollama:EmbeddingDimensions in config.
-- mxbai-embed-large = 1024; nomic-embed-text = 768. Mixing models silently
-- corrupts results; the embedder verifies the metadata table on startup.
CREATE VIRTUAL TABLE chunk_embeddings USING vec0(
    chunk_id   INTEGER PRIMARY KEY,
    embedding  FLOAT[1024]
);

-- Tracks Maildir state for reconciliation between scans.
CREATE TABLE sync_state (
    maildir_full_path TEXT PRIMARY KEY,
    message_id        TEXT,
    last_seen_at      TEXT NOT NULL,
    content_hash      TEXT
);

-- Schema + embedding-model metadata. Seeded by the migration runner.
CREATE TABLE metadata (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

INSERT INTO metadata(key, value) VALUES
    ('schema_version',       '1'),
    ('embedding_model',      'mxbai-embed-large'),
    ('embedding_dimensions', '1024');
