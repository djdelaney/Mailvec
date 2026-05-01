-- v3: track a content-hash of the parsed message body on the messages row so
-- the indexer can detect when an upstream body change should invalidate the
-- existing embedding. NULL = "no hash recorded yet" (legacy rows that haven't
-- been re-scanned since v3 landed). The scanner backfills lazily on next visit.
ALTER TABLE messages ADD COLUMN content_hash TEXT;
