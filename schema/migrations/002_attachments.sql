-- v2: per-message attachment metadata.
--
-- Adds:
--   * messages.attachment_names — denormalized space-joined filename list, fed
--     into FTS so attachment-name keyword matches surface in search results.
--     Source of truth lives in the new attachments table; never parse this back.
--   * messages_fts column for attachment_names. FTS5 has no ALTER for column-set
--     changes, so the virtual table is dropped and recreated, then repopulated
--     from messages via the FTS5 'rebuild' command.
--   * attachments table + idx_attachments_message.
--
-- After this migration the attachments table is empty and attachment_names is
-- NULL for every existing row. The next indexer pass refills both via
-- MessageRepository.Upsert (which replaces attachments wholesale per message).
-- Until then, attachment-name keyword search returns nothing — but ordinary
-- subject / from / body searches over previously-indexed rows still work,
-- because the FTS rebuild repopulates from the messages content table.

ALTER TABLE messages ADD COLUMN attachment_names TEXT;

DROP TRIGGER IF EXISTS messages_au;
DROP TRIGGER IF EXISTS messages_ad;
DROP TRIGGER IF EXISTS messages_ai;
DROP TABLE IF EXISTS messages_fts;

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

INSERT INTO messages_fts(messages_fts) VALUES('rebuild');

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
