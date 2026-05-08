-- v3 -> v4: per-attachment text extraction + per-chunk source tracking.
--
-- Adds three new columns to attachments (extracted_text / extracted_at /
-- extraction_status) so the indexer can stash the plain text recovered from
-- PDFs / DOCX / TXT bodies, and adds source / attachment_id to chunks so
-- search results can identify whether a hit came from the message body or
-- from a specific attached document.
--
-- Note: the migration only adds columns. Existing attachment rows have
-- extracted_text NULL, so they're invisible to the embedder until something
-- causes the indexer to re-parse the .eml (e.g. mbsync rewriting the file).
-- For an immediate backfill, drop the database and let the indexer rebuild
-- — that's the supported v3 -> v4 path for an existing install.
ALTER TABLE attachments ADD COLUMN extracted_text TEXT;
ALTER TABLE attachments ADD COLUMN extracted_at TEXT;
ALTER TABLE attachments ADD COLUMN extraction_status TEXT;

ALTER TABLE chunks ADD COLUMN source TEXT NOT NULL DEFAULT 'body';
-- SQLite's ALTER TABLE ... ADD COLUMN doesn't support inline REFERENCES
-- for new columns, so we add the FK semantically (the schema for fresh DBs
-- in 001_initial.sql does include the REFERENCES clause; existing DBs
-- enforce attachment_id integrity at the application layer instead).
ALTER TABLE chunks ADD COLUMN attachment_id INTEGER;

CREATE INDEX IF NOT EXISTS idx_chunks_attachment ON chunks(attachment_id) WHERE attachment_id IS NOT NULL;
