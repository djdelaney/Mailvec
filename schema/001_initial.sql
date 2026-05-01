-- Mailvec initial schema.
-- Apply once on a fresh database. Subsequent changes go in schema/migrations/.

PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

-- Core messages table.
-- attachment_names is a denormalized space-joined list of attachment filenames,
-- maintained in lockstep with the attachments table by MessageRepository.Upsert.
-- It exists ONLY as an FTS feed so the index can find messages by their
-- attached filenames (e.g. "mortgage_statement_2024.pdf") without cross-table
-- triggers. Filename boundaries are not preserved — FTS5's unicode61 tokenizer
-- splits on whitespace AND punctuation, so any separator we picked would
-- tokenize identically. The attachments table below is the source of truth
-- for per-attachment metadata; never parse this column back into a list.
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
    attachment_names  TEXT,
    body_text         TEXT,
    body_html         TEXT,
    raw_headers       TEXT,
    indexed_at        TEXT NOT NULL,
    embedded_at       TEXT,
    deleted_at        TEXT,
    -- Hash of the parsed message body (see Mailvec.Core.Parsing.MessageBodyHasher).
    -- Populated by the indexer on every parse so we can detect when an upstream
    -- body mutation should invalidate existing embeddings. NULL until first scan
    -- under v3. Migration: schema/migrations/003_message_body_hash.sql.
    content_hash      TEXT
);

CREATE INDEX idx_messages_thread    ON messages(thread_id);
CREATE INDEX idx_messages_folder    ON messages(folder);
CREATE INDEX idx_messages_date_sent ON messages(date_sent);
CREATE INDEX idx_messages_to_embed  ON messages(embedded_at) WHERE embedded_at IS NULL;

-- Full-text index over messages.
CREATE VIRTUAL TABLE messages_fts USING fts5(
    subject, from_name, from_address, body_text, attachment_names,
    content='messages', content_rowid='id',
    tokenize='porter unicode61'
);

CREATE TRIGGER messages_ai AFTER INSERT ON messages BEGIN
    INSERT INTO messages_fts(rowid, subject, from_name, from_address, body_text, attachment_names)
    VALUES (new.id, new.subject, new.from_name, new.from_address, new.body_text, new.attachment_names);
END;

CREATE TRIGGER messages_ad AFTER DELETE ON messages BEGIN
    INSERT INTO messages_fts(messages_fts, rowid, subject, from_name, from_address, body_text, attachment_names)
    VALUES ('delete', old.id, old.subject, old.from_name, old.from_address, old.body_text, old.attachment_names);
END;

CREATE TRIGGER messages_au AFTER UPDATE ON messages BEGIN
    INSERT INTO messages_fts(messages_fts, rowid, subject, from_name, from_address, body_text, attachment_names)
    VALUES ('delete', old.id, old.subject, old.from_name, old.from_address, old.body_text, old.attachment_names);
    INSERT INTO messages_fts(rowid, subject, from_name, from_address, body_text, attachment_names)
    VALUES (new.id, new.subject, new.from_name, new.from_address, new.body_text, new.attachment_names);
END;

-- Per-attachment metadata. Replaced wholesale on every message upsert — no
-- partial state. part_index is the order within mime.Attachments and is what
-- a future get_attachment(id, partIndex) MCP tool would key on. Filename can
-- be NULL (some MIME parts have no Content-Disposition filename / Content-Type
-- name); content_type and size_bytes are populated when MimeKit can determine
-- them.
CREATE TABLE attachments (
    id            INTEGER PRIMARY KEY,
    message_id    INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    part_index    INTEGER NOT NULL,
    filename      TEXT,
    content_type  TEXT,
    size_bytes    INTEGER,
    UNIQUE(message_id, part_index)
);

CREATE INDEX idx_attachments_message ON attachments(message_id);

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
    ('schema_version',       '3'),
    ('embedding_model',      'mxbai-embed-large'),
    ('embedding_dimensions', '1024');
