# Local test database

How to point the indexer, embedder, and CLI at a throwaway DB + Maildir so you can debug parser issues, try queries, or test schema migrations without touching `~/Library/Application Support/Mailvec/archive.sqlite`.

The mechanism is env vars. `Archive__DatabasePath` and `Ingest__MaildirRoot` override the shared `appsettings.Local.json` written by `ops/install.sh` (precedence: per-binary appsettings → shared file → env vars). Set them in a shell session and every Mailvec binary launched from that shell reads the test paths; production launchd agents are untouched.

## 1. Get a Maildir to point at

Two options:

**A. Pull a slice from IMAP with the dev script.** Get a Fastmail app password at <https://app.fastmail.com/settings/security/devicekeys> (click **New App Password**, name it `mailvec-test`). Revoke it when done.

```sh
export FASTMAIL_USER=you@example.com
export FASTMAIL_APP_PASSWORD='<paste-here>'   # quote it; contains spaces
./ops/dev-fetch-imap.py
```

Defaults to the last 7 days of INBOX into `~/mailvec-test/Mail`. Overrides: `MAILDIR_ROOT`, `SINCE_DAYS`, `IMAP_HOST`, `IMAP_FOLDER`. Filenames are the IMAP UID, so re-runs are idempotent.

**B. Reuse your production Maildir, read-only.** Point `Ingest__MaildirRoot` at the existing `~/Mail/Fastmail`. The indexer doesn't write to the Maildir — it only reads — so the source is safe. Faster than A for large slices; only catch is that any flag rewrite by a concurrent mbsync run will look like a content change on the next scan and force a re-parse.

## 2. Run the indexer against the test paths

```sh
export Ingest__MaildirRoot=~/mailvec-test/Mail        # or ~/Mail/Fastmail for option B
export Archive__DatabasePath=~/mailvec-test/archive.sqlite

dotnet run --project src/Mailvec.Indexer
```

Look for `MaildirScanner: seen=N upserted=N parseFailed=K`. ^C once the initial scan completes; the watcher then idles. `parseFailed > 0` means real-world headers tripped the parser — the warning line prints the full path so you can capture a fixture.

The schema migrator creates a fresh empty DB at `Archive__DatabasePath` if it doesn't exist, so there's no risk of touching production.

## 3. (Optional) Run the embedder

For semantic and hybrid queries. Ollama must be running with `mxbai-embed-large` pulled (see README Quickstart). Second terminal, same env vars:

```sh
dotnet run --project src/Mailvec.Embedder
```

Watch for `Embedded N messages (M chunks)` lines. First call is slow (model load); later batches are fast. ^C when `mailvec status` shows full coverage.

## 4. Query

Third terminal, same env vars. Any CLI subcommand works against the test DB while the env vars are set. **Don't use the installed `mailvec` shim** — it inherits whatever env its parent process had, so launching it from the tray or another shell hits production. For test-DB work, always go through `dotnet run --project src/Mailvec.Cli -- …` from the shell that has the overrides.

```sh
dotnet run --project src/Mailvec.Cli -- status

# Search modes
dotnet run --project src/Mailvec.Cli -- search "ramen"                       # keyword (FTS5/BM25)
dotnet run --project src/Mailvec.Cli -- search --semantic "vacation plans"
dotnet run --project src/Mailvec.Cli -- search --hybrid "tree quote"

# Query modifiers
dotnet run --project src/Mailvec.Cli -- search "lunch AND friday" -n 10      # boolean, custom limit
dotnet run --project src/Mailvec.Cli -- search '"exact phrase"'              # phrase
dotnet run --project src/Mailvec.Cli -- search "invoice" --date-from 2025-01-01 --date-to 2025-03-31
dotnet run --project src/Mailvec.Cli -- search --hybrid "tree quote" --date-from 2024-06-01   # date filter works on all modes

# Fetch one message
dotnet run --project src/Mailvec.Cli -- get 52226                            # by internal id
dotnet run --project src/Mailvec.Cli -- get '<abc@example.com>' --body       # by RFC Message-ID, full body

# Maintenance
dotnet run --project src/Mailvec.Cli -- audit-embeddings                     # sanity-check vector index
dotnet run --project src/Mailvec.Cli -- checkpoint                           # truncate the SQLite WAL
dotnet run --project src/Mailvec.Cli -- purge-deleted --dry-run              # preview hard-delete of soft-deleted rows
dotnet run --project src/Mailvec.Cli -- purge-deleted -y                     # actually purge (irreversible)
```

- `status` prints message counts, embedding coverage, and warns if the DB's recorded embedding model disagrees with config.
- `--date-from` / `--date-to` accept ISO 8601 (`YYYY-MM-DD` or full RFC 3339), are inclusive, and apply to all three search modes; an unset bound means "open" (so `--date-from` alone scopes to "since").
- `get <id>` accepts either an internal SQLite id (numeric) or an RFC 5322 Message-ID (anything else). Useful for following up on a search hit or eyeballing a specific message Claude flagged.
- `audit-embeddings` sweeps the vector index for zero / NaN / abnormal-norm vectors — worth running after a large reindex or an Ollama upgrade.
- `purge-deleted` hard-deletes rows that the indexer marked `deleted_at IS NOT NULL` (and their chunks/vectors/attachments/FTS entries). Soft-deletes accumulate as you remove folders from mbsync but stay in the file until purged.

After a bulk embedder run, `checkpoint` truncates the SQLite WAL file, which can grow to multiple GB during long write sessions. SQLite auto-checkpoints during normal operation, so day-to-day you don't need to think about it.

## 5. Cleanup

```sh
rm -rf ~/mailvec-test
unset FASTMAIL_USER FASTMAIL_APP_PASSWORD Ingest__MaildirRoot Archive__DatabasePath
```

Then revoke the app password at <https://app.fastmail.com/settings/security/devicekeys> if you used option A.
