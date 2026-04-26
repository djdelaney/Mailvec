# Mailvec

Local-first IMAP archive with keyword (FTS5) and semantic (sqlite-vec) search, exposed to Claude over MCP. Single-account, single-machine, designed to run unattended on a Mac mini. See [`mailvec-project-scope.md`](mailvec-project-scope.md) for the full design.

Sync is done by [`mbsync`](https://isync.sourceforge.io/), so any IMAP server works — Fastmail, iCloud, Gmail (with an app password), self-hosted Dovecot, etc. The shipped `ops/mbsyncrc.example` and reference design use Fastmail because that's the author's setup; swap the `Host` / `User` / `PassCmd` lines and the rest of the pipeline (Maildir → SQLite → embeddings → MCP) is unchanged.

## Layout

```
src/
  Mailvec.Core      shared types, SQLite + Ollama clients, hybrid search
  Mailvec.Indexer   BackgroundService: Maildir -> SQLite (no embeddings)
  Mailvec.Embedder  BackgroundService: SQLite rows -> Ollama embeddings
  Mailvec.Mcp       AspNetCore MCP server (HTTP, localhost:3333)
  Mailvec.Cli       admin commands (status, search, reindex, rebuild-fts)
tests/
  Mailvec.{Core,Indexer,Mcp}.Tests
schema/
  001_initial.sql   tables, FTS5 triggers, vec0 vector index
ops/
  mbsyncrc.example  IMAP sync config template
  launchd/          plist templates for mbsync + 3 .NET services
  install.sh        Phase 4 installer (stub)
runtimes/
  osx-arm64/native  sqlite-vec native binary lands here
```

## Build

Requires .NET 10 SDK.

```sh
./ops/fetch-sqlite-vec.sh   # downloads vec0.dylib (run once)
dotnet build
dotnet test
```

The `sqlite-vec` extension is loaded at runtime from `runtimes/<rid>/native/vec0.dylib`. The fetch script grabs the latest release from the upstream GitHub repo; pin a version with `SQLITE_VEC_VERSION=0.1.9 ./ops/fetch-sqlite-vec.sh`.

Central package management lives in `Directory.Packages.props`. Shared MSBuild settings (target framework, nullable, warnings-as-errors) live in `Directory.Build.props`.

## Status

Built in phases per the [design doc](mailvec-project-scope.md#8-phased-build-plan). Each phase has a hard exit criterion and ships standalone value before the next begins.

### ✅ Phase 0 — Repo scaffold

Solution, projects, central package management, shared MSBuild settings, ops + schema folders, gitignore, README.

### ✅ Phase 1 — Ingest pipeline

A searchable local archive. **Exit criterion met:** point the indexer at a Maildir and `mailvec search "<query>"` returns BM25-ranked hits with snippets.

- Schema migration runner (`Mailvec.Core.Data.SchemaMigrator`) over an embedded `001_initial.sql`.
- MimeKit-based parser with fixture-driven tests (plain text, multipart, html-only, unicode headers, attachments).
- `MaildirScanner` — recursive full scan, soft-delete reconciliation, mbsync `new/`→`cur/` rename detection.
- `MaildirWatcher` — debounced `FileSystemWatcher` with `tmp/` filtering.
- `MessageIngestService` — `BackgroundService` that runs an initial scan, then reacts to watcher pulses + a periodic safety-net rescan.
- FTS5 with BM25 ordering and bracketed snippets.
- `mailvec status | search | rebuild-fts` CLI.

### ⬜ Phase 2 — Semantic layer

Adds locally-generated embeddings and hybrid (FTS + vector) search.

- Ollama HTTP client with retry/backoff (`Microsoft.Extensions.Http.Resilience`).
- `ChunkingService` — token-aware splitter sized to the embedding model's context window.
- `EmbeddingWorker` — `BackgroundService` that processes rows where `embedded_at IS NULL` in batches.
- Hybrid search via Reciprocal Rank Fusion (RRF) over BM25 + cosine similarity.
- `mailvec search --semantic` for direct comparison against keyword results.

**Exit criterion:** semantic queries surface relevant results that the FTS layer misses (e.g. paraphrased subjects, synonyms).

### ⬜ Phase 3 — MCP exposure

Wires the archive up to Claude.

- AspNetCore MCP server using `ModelContextProtocol.AspNetCore`, bound to `127.0.0.1:3333`.
- Tools: `search_emails`, `get_email`, `get_thread`, `list_folders`, `find_by_sender`, `recent_emails`.
- Hybrid search reused from Phase 2.

**Exit criterion:** Claude can answer "when did Bartlett last quote me for the tree work?" without being told where to look.

### ⬜ Phase 4 — Operationalization

Makes the system survive reboots unattended.

- launchd plists for mbsync + the three .NET services (templates already in `ops/launchd/`).
- `ops/install.sh` — publishes services, rewrites plist `__INSTALL_PREFIX__` placeholders, loads the agents, verifies health.
- `/health` endpoint on the MCP server.
- Log rotation via launchd stdout/stderr paths.
- Coverage / freshness metrics surfaced through `mailvec status`.

**Exit criterion:** reboot the Mac mini; everything comes back without intervention.

### Out of scope (per design doc §11)

Sending mail, modifying Fastmail state, multi-account support, calendar/contacts/files, web UI, real-time push, attachment indexing.
