-- v8: sync_state.folder — folder membership for search.
--
-- A message can live in several folders at once (Gmail's All Mail + every
-- label; Fastmail multi-label; self-CC'd INBOX + Sent), but messages keys on
-- message_id UNIQUE, so only one copy wins `messages.folder` attribution.
-- sync_state already holds one row per live file (refreshed every scan,
-- removed on delete) — adding the copy's folder here turns it into the
-- membership table: the folder search filter matches `m.folder OR EXISTS
-- (sync_state)`, and list_folders counts from membership, so a message is
-- findable under every folder it actually lives in.
--
-- Backfill is deliberately NOT done here: folder derivation needs MaildirRoot
-- (config, not stored in the DB). The column starts NULL and the scanner
-- writes it on every sync_state upsert — one full scan after upgrade and all
-- live rows are populated. Until then the filter's `m.folder = $folder` half
-- and FolderStats' legacy fallback keep folder features working.
--
-- The composite index also serves the scanner's rename-repair lookup
-- (FreshPathForMessageId), which previously full-scanned sync_state per
-- repaired row.
ALTER TABLE sync_state ADD COLUMN folder TEXT;

CREATE INDEX idx_sync_state_message_folder ON sync_state(message_id, folder);
