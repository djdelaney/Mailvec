-- v4 -> v5: feed extracted attachment text into the FTS5 index.
--
-- Phase 4.5 wired attachment-text extraction into chunks/embeddings (the
-- vector path) but never connected it to messages_fts (the keyword path),
-- so a message with body_text="Sent from my iPhone" plus a PFAS-quote PDF
-- attachment was keyword-invisible. RRF fusion in HybridSearchService
-- doubly-rewards items found by both legs, so semantic-only hits got
-- pushed below dual-signal items even when semantic ranked them at #3.
--
-- Adds:
--   1. messages.attachment_text — denormalized space-joined concatenation
--      of attachments.extracted_text (status='done'), maintained by
--      MessageRepository.Upsert in the same transaction as attachment_names.
--   2. attachment_text as a sixth column on messages_fts.
--   3. Backfill from existing attachments rows (no .eml re-walk needed,
--      unlike the v3 -> v4 transition).
--
-- FTS5 doesn't support ALTER TABLE ... ADD COLUMN on virtual tables, so we
-- drop + recreate the FTS table and triggers, then rebuild the index.

ALTER TABLE messages ADD COLUMN attachment_text TEXT;

-- Backfill messages.attachment_text from existing attachments. group_concat
-- joins multi-attachment messages on a space; matches the runtime build path
-- in MessageRepository.BuildAttachmentText.
UPDATE messages SET attachment_text = (
    SELECT group_concat(extracted_text, ' ')
    FROM attachments
    WHERE attachments.message_id = messages.id
      AND attachments.extracted_text IS NOT NULL
      AND length(attachments.extracted_text) > 0
);

DROP TRIGGER IF EXISTS messages_ai;
DROP TRIGGER IF EXISTS messages_ad;
DROP TRIGGER IF EXISTS messages_au;
DROP TABLE IF EXISTS messages_fts;

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

-- Repopulate the index from messages (now including the backfilled
-- attachment_text column).
INSERT INTO messages_fts(messages_fts) VALUES('rebuild');
