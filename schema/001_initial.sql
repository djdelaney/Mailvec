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
--
-- attachment_text is the same idea applied to extracted document content:
-- a denormalized space-joined concatenation of attachments.extracted_text for
-- every attachment whose extraction_status='done'. Fed into messages_fts so
-- BM25 keyword search can match terms from PDF / DOCX bodies (otherwise a
-- message with body_text="Sent from my iPhone" + a PFAS-quote PDF attachment
-- is keyword-invisible). attachments.extracted_text remains the source of
-- truth — never parse this column back into a per-attachment list.
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
    attachment_text   TEXT,
    body_text         TEXT,
    body_html         TEXT,
    raw_headers       TEXT,
    indexed_at        TEXT NOT NULL,
    embedded_at       TEXT,
    deleted_at        TEXT,
    -- Hash of the parsed message body (see Mailvec.Core.Parsing.MessageBodyHasher).
    -- Populated by the indexer on every parse so we can detect when an upstream
    -- body mutation should invalidate existing embeddings. NULL means "fresh
    -- insert with no prior row" (treated as unchanged for embedding purposes).
    content_hash      TEXT
);

CREATE INDEX idx_messages_thread     ON messages(thread_id);
CREATE INDEX idx_messages_folder     ON messages(folder);
CREATE INDEX idx_messages_date_sent  ON messages(date_sent);
CREATE INDEX idx_messages_to_embed   ON messages(embedded_at) WHERE embedded_at IS NULL;
-- Used by HealthService.ReadCounts for cheap MAX(indexed_at) lookups; without
-- it /health takes ~7 s on a real-sized archive (full table scan).
CREATE INDEX idx_messages_indexed_at ON messages(indexed_at);

-- Full-text index over messages. Six columns; column ordering is API-relevant
-- because BM25 weights and snippet() column indices reference these positions
-- (KeywordSearchService passes -1 to let FTS5 pick the best-matching column).
CREATE VIRTUAL TABLE messages_fts USING fts5(
    subject, from_name, from_address, body_text, attachment_names, attachment_text,
    content='messages', content_rowid='id',
    tokenize='porter unicode61'
);

CREATE TRIGGER messages_ai AFTER INSERT ON messages BEGIN
    INSERT INTO messages_fts(rowid, subject, from_name, from_address, body_text, attachment_names, attachment_text)
    VALUES (new.id, new.subject, new.from_name, new.from_address, new.body_text, new.attachment_names, new.attachment_text);
END;

CREATE TRIGGER messages_ad AFTER DELETE ON messages BEGIN
    INSERT INTO messages_fts(messages_fts, rowid, subject, from_name, from_address, body_text, attachment_names, attachment_text)
    VALUES ('delete', old.id, old.subject, old.from_name, old.from_address, old.body_text, old.attachment_names, old.attachment_text);
END;

CREATE TRIGGER messages_au AFTER UPDATE ON messages BEGIN
    INSERT INTO messages_fts(messages_fts, rowid, subject, from_name, from_address, body_text, attachment_names, attachment_text)
    VALUES ('delete', old.id, old.subject, old.from_name, old.from_address, old.body_text, old.attachment_names, old.attachment_text);
    INSERT INTO messages_fts(rowid, subject, from_name, from_address, body_text, attachment_names, attachment_text)
    VALUES (new.id, new.subject, new.from_name, new.from_address, new.body_text, new.attachment_names, new.attachment_text);
END;

-- Per-attachment metadata. Replaced wholesale on every message upsert that
-- detects body content changes; preserved across no-op rescans so extracted
-- text isn't thrown away. part_index is the order within mime.Attachments and
-- is what get_attachment(id, partIndex) keys on. extracted_text holds the
-- plain text recovered from PDF/DOCX/TXT bodies; extraction_status records
-- whether extraction was attempted and how it ended ('done', 'unsupported',
-- 'oversize', 'no_text', 'failed', or NULL = not yet attempted).
CREATE TABLE attachments (
    id                 INTEGER PRIMARY KEY,
    message_id         INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    part_index         INTEGER NOT NULL,
    filename           TEXT,
    content_type       TEXT,
    size_bytes         INTEGER,
    extracted_text     TEXT,
    extracted_at       TEXT,
    extraction_status  TEXT,
    UNIQUE(message_id, part_index)
);

CREATE INDEX idx_attachments_message ON attachments(message_id);

-- Per-message text chunks fed to the embedder. `source` is 'body' or
-- 'attachment'; for 'attachment' rows, attachment_id points at the source
-- attachment so search results can identify which document matched. We
-- still namespace by message_id so the existing dedup-per-message
-- window function in VectorSearchService keeps working — one search row
-- per email regardless of whether the match came from body or attachment.
CREATE TABLE chunks (
    id            INTEGER PRIMARY KEY,
    message_id    INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    chunk_index   INTEGER NOT NULL,
    chunk_text    TEXT NOT NULL,
    token_count   INTEGER,
    source        TEXT NOT NULL DEFAULT 'body',
    attachment_id INTEGER REFERENCES attachments(id) ON DELETE CASCADE,
    UNIQUE(message_id, chunk_index)
);

CREATE INDEX idx_chunks_message    ON chunks(message_id);
CREATE INDEX idx_chunks_attachment ON chunks(attachment_id) WHERE attachment_id IS NOT NULL;

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
    ('schema_version',       '6'),
    ('embedding_model',      'mxbai-embed-large'),
    ('embedding_dimensions', '1024');
