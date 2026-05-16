# Dev walkthrough — trying Mailvec end-to-end

Two kinds of testing: the automated unit/integration suite, and a manual walkthrough against real mail. Both bypass mbsync; mbsync is the production sync path but isn't required to exercise the rest of the pipeline.

For full production install (launchd agents, tray, MCPB bundle for Claude Desktop), see the [README](../README.md) Quickstart instead — this page is the manual, terminal-driven path useful for debugging.

## 1. Automated tests

```sh
dotnet test
```

~220 tests across `Mailvec.Core.Tests`, `Mailvec.Mcp.Tests`, and `Mailvec.Indexer.Tests` — parser fixtures, schema migrations, repositories, FTS5 search, vector search (with hand-injected vectors), RRF fusion, the Ollama HTTP client (stubbed), chunking, attachment extraction (PDF/DOCX/text), path expansion, and per-MCP-tool coverage (`search_emails`, `get_email`, `get_thread`, `list_folders`, `get_attachment`) plus the HTTP transport. No live Ollama or IMAP needed.

## 2. Get a Fastmail app password

Visit <https://app.fastmail.com/settings/security/devicekeys>, click **New App Password**, name it `mailvec-test`, copy the password somewhere safe. Revoke it when you're done.

## 3. Fetch the last 7 days of mail

`ops/dev-fetch-imap.py` writes each message as an `.eml` file into a Maildir layout the indexer understands. Filenames use the IMAP UID, so re-running is idempotent — already-fetched messages are skipped.

```sh
export FASTMAIL_USER=you@example.com
export FASTMAIL_APP_PASSWORD='<paste-here>'    # quote it; contains spaces

./ops/dev-fetch-imap.py
```

Optional overrides: `MAILDIR_ROOT` (default `~/mailvec-test/Mail`), `SINCE_DAYS` (default 7), `IMAP_HOST`, `IMAP_FOLDER`. To pull from a different IMAP provider, set `IMAP_HOST` accordingly.

## 4. Run the indexer

Point the env vars at your test Maildir + a fresh DB path:

```sh
export Ingest__MaildirRoot=~/mailvec-test/Mail
export Archive__DatabasePath=~/mailvec-test/archive.sqlite
export Logging__LogLevel__Default=Information

dotnet run --project src/Mailvec.Indexer
```

Look for a single line like `MaildirScanner: seen=N upserted=N parseFailed=K softDeleted=0`. The watcher then idles until new files arrive; ^C once the initial scan is done. Any `parseFailed > 0` count means real-world headers tripped the parser — capture the offending file (the warning prints the full path) and we can add a fixture.

## 5. Run the embedder (optional, for semantic/hybrid)

Make sure Ollama is running and `mxbai-embed-large` is pulled (see the README's Quickstart), then in a second terminal with the same env vars:

```sh
dotnet run --project src/Mailvec.Embedder
```

Watch for `Embedded N messages (M chunks) in <ms>` lines. The first call is slow (model load); subsequent batches are fast. ^C when `mailvec status` shows full coverage.

For a small test archive this finishes in minutes. A full archive (tens of thousands of messages) is overnight territory on Apple Silicon without a dedicated GPU — the embedder works through it in the background and you can keep using `mailvec status` / `search` against partial coverage at any time. After a bulk run, `mailvec checkpoint` truncates the SQLite WAL file, which can grow to multiple GB during long write sessions:

```sh
dotnet run --project src/Mailvec.Cli -- checkpoint
```

This is a one-shot cleanup. SQLite auto-checkpoints during normal operation, so day-to-day you don't need to think about it.

## 6. Search

Same env vars, third terminal:

```sh
dotnet run --project src/Mailvec.Cli -- status
dotnet run --project src/Mailvec.Cli -- search "ramen"                  # keyword (FTS5/BM25)
dotnet run --project src/Mailvec.Cli -- search --semantic "vacation plans"
dotnet run --project src/Mailvec.Cli -- search --hybrid "tree quote"
dotnet run --project src/Mailvec.Cli -- search "lunch AND friday" -n 10  # boolean, custom limit
dotnet run --project src/Mailvec.Cli -- search '"exact phrase"'          # phrase
dotnet run --project src/Mailvec.Cli -- search "invoice" --date-from 2025-01-01 --date-to 2025-03-31   # date window
dotnet run --project src/Mailvec.Cli -- search --hybrid "tree quote" --date-from 2024-06-01            # date filter works on all modes
dotnet run --project src/Mailvec.Cli -- get 52226                       # fetch one message by internal id
dotnet run --project src/Mailvec.Cli -- get '<abc@example.com>' --body  # fetch by RFC Message-ID, full body
dotnet run --project src/Mailvec.Cli -- audit-embeddings                # sanity-check vector index
dotnet run --project src/Mailvec.Cli -- checkpoint                      # truncate the SQLite WAL
dotnet run --project src/Mailvec.Cli -- purge-deleted --dry-run         # preview hard-delete of soft-deleted rows
dotnet run --project src/Mailvec.Cli -- purge-deleted -y                # actually purge them (irreversible)
```

`status` prints message counts, embedding coverage, and warns if the schema's recorded embedding model disagrees with config. `--date-from` / `--date-to` accept ISO 8601 (`YYYY-MM-DD` or full RFC 3339) and apply to all three search modes; bounds are inclusive and an unset bound means "open" (so `--date-from` alone scopes to "since"). `get <id>` fetches a single message — pass either an internal SQLite id (numeric) or an RFC 5322 Message-ID (anything else); useful for following up on a `search -i` hit or eyeballing a specific message Claude flagged. `audit-embeddings` sweeps the vector index for zero / NaN / abnormal-norm vectors — useful right after a large reindex or an Ollama upgrade. `purge-deleted` hard-deletes rows that the indexer marked `deleted_at IS NOT NULL` (and their chunks/vectors/attachments/FTS entries) — soft-deletes accumulate as you remove folders from mbsync but stay in the file until purged.

The Phase 2 exit criterion is "semantic queries return relevant results the FTS layer would have missed" — a quality call that needs your eyes on a real archive. Useful comparison queries are paraphrases ("trip planning" vs `vacation`), synonyms (`bill` vs `invoice`), and topic-level recall (`subscription renewal`, `house repairs`).

## 7. Cleanup

```sh
rm -rf ~/mailvec-test
unset FASTMAIL_USER FASTMAIL_APP_PASSWORD Ingest__MaildirRoot Archive__DatabasePath
# then revoke the app password at https://app.fastmail.com/settings/security/devicekeys
```
