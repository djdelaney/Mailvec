-- v14: sync_state.seen — read state, per Maildir copy.
--
-- The seen flag is the `S` in the Maildir info suffix (`<unique>:2,<flags>`),
-- so it is a pure function of the file's path: a flag change is always a
-- rename, which lands at a new path and takes the scanner's write path. That
-- is what keeps an unchanged scan writing nothing — the fast path never needs
-- to touch this column. SyncStateRepository.Upsert derives it from the path
-- on every write (MaildirFlags.IsSeen); nothing passes it in.
--
-- Per COPY, not per message: a message can live in several folders and each
-- copy is its own file with its own flags. Providers that keep seen state per
-- email (Fastmail) converge the copies on the next sync; until then they can
-- disagree, and the search surface defines a message as read only when every
-- live copy is (SearchFilterSql, MessageRepository.GetReadStates).
--
-- Backfilled here, unlike v8/v9, because it needs no config and no file
-- access — only the stored path. The expression below MUST agree with
-- MaildirFlags.IsSeen (pinned by SchemaMigratorTests): basename via the
-- rtrim/replace idiom (SQLite has no reverse instr), then an `S` anywhere
-- after the first `:2,` in it. A file with no info suffix (anything in new/)
-- is unseen.
ALTER TABLE sync_state ADD COLUMN seen INTEGER NOT NULL DEFAULT 0;

UPDATE sync_state
SET seen = 1
WHERE instr(replace(maildir_full_path, rtrim(maildir_full_path, replace(maildir_full_path, '/', '')), ''), ':2,') > 0
  AND instr(
        substr(
            replace(maildir_full_path, rtrim(maildir_full_path, replace(maildir_full_path, '/', '')), ''),
            instr(replace(maildir_full_path, rtrim(maildir_full_path, replace(maildir_full_path, '/', '')), ''), ':2,') + 3),
        'S') > 0;
