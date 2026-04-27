# Mailvec

Local-first IMAP archive with keyword (FTS5) and semantic (sqlite-vec) search, exposed to Claude over MCP. Single-account, single-machine, designed to run unattended on a Mac mini. See [`mailvec-project-scope.md`](mailvec-project-scope.md) for the full design.

Sync is done by [`mbsync`](https://isync.sourceforge.io/), so any IMAP server works — Fastmail, iCloud, Gmail (with an app password), self-hosted Dovecot, etc. The shipped `ops/mbsyncrc.example` and reference design use Fastmail because that's the author's setup; swap the `Host` / `User` / `PassCmd` lines and the rest of the pipeline (Maildir → SQLite → embeddings → MCP) is unchanged.

## Layout

```
src/
  Mailvec.Core      shared types, SQLite + Ollama clients, hybrid search
  Mailvec.Indexer   BackgroundService: Maildir -> SQLite (no embeddings)
  Mailvec.Embedder  BackgroundService: SQLite rows -> Ollama embeddings
  Mailvec.Mcp       AspNetCore MCP server (HTTP on :3333, or stdio for MCPB)
  Mailvec.Cli       admin commands (status, search, reindex, rebuild-fts)
tests/
  Mailvec.{Core,Indexer,Mcp}.Tests
schema/
  001_initial.sql   tables, FTS5 triggers, vec0 vector index
ops/
  mbsyncrc.example     IMAP sync config template
  launchd/             plist templates for mbsync + 3 .NET services
  install.sh           Phase 4 installer (stub)
  fetch-sqlite-vec.sh  one-shot: pulls vec0.dylib from upstream releases
  build-mcpb.sh        packages a .mcpb bundle into dist/ for Claude Desktop
  dev-fetch-imap.py    dev-only: pulls last N days of mail without mbsync
runtimes/
  osx-arm64/native  sqlite-vec native binary lands here
manifest.json       Claude Desktop MCPB manifest (binary entry + user_config)
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

## Ollama (semantic search)

The embedder talks to a local Ollama server. One-time setup on macOS:

```sh
brew install ollama
ollama serve &                       # or run as a launchd service
ollama pull mxbai-embed-large        # 1024-dim model used by default
```

You don't need Ollama running to build, run the indexer, or use keyword search — only the embedder, semantic search, and hybrid search depend on it.

The configured model is recorded in the SQLite `metadata` table on first embed. If you change `Ollama:EmbeddingModel` later, the embedder refuses to start until you run `mailvec reindex --all` to clear the existing vectors. Mixing vector spaces silently corrupts results, so this guard is intentional.

## Trying it end-to-end

Two kinds of testing: the automated unit/integration suite, and a manual walkthrough against real mail. Both bypass mbsync; mbsync is the production sync path but isn't required to exercise the rest of the pipeline.

### 1. Automated tests

```sh
dotnet test
```

35 tests across `Mailvec.Core.Tests` and `Mailvec.Indexer.Tests` — parser fixtures, schema migrations, repositories, FTS5 search, vector search (with hand-injected vectors), RRF fusion, the Ollama HTTP client (stubbed), and chunking. No live Ollama or IMAP needed.

### 2. Get a Fastmail app password

Visit <https://app.fastmail.com/settings/security/devicekeys>, click **New App Password**, name it `mailvec-test`, copy the password somewhere safe. Revoke it when you're done.

### 3. Fetch the last 7 days of mail

`ops/dev-fetch-imap.py` writes each message as an `.eml` file into a Maildir layout the indexer understands. Filenames use the IMAP UID, so re-running is idempotent — already-fetched messages are skipped.

```sh
export FASTMAIL_USER=you@example.com
export FASTMAIL_APP_PASSWORD='<paste-here>'    # quote it; contains spaces

./ops/dev-fetch-imap.py
```

Optional overrides: `MAILDIR_ROOT` (default `~/mailvec-test/Mail`), `SINCE_DAYS` (default 7), `IMAP_HOST`, `IMAP_FOLDER`. To pull from a different IMAP provider, set `IMAP_HOST` accordingly.

### 4. Run the indexer

Point the env vars at your test Maildir + a fresh DB path:

```sh
export Archive__MaildirRoot=~/mailvec-test/Mail
export Archive__DatabasePath=~/mailvec-test/archive.sqlite
export Logging__LogLevel__Default=Information

dotnet run --project src/Mailvec.Indexer
```

Look for a single line like `MaildirScanner: seen=N upserted=N parseFailed=K softDeleted=0`. The watcher then idles until new files arrive; ^C once the initial scan is done. Any `parseFailed > 0` count means real-world headers tripped the parser — capture the offending file (the warning prints the full path) and we can add a fixture.

### 5. Run the embedder (optional, for semantic/hybrid)

Make sure Ollama is running and `mxbai-embed-large` is pulled (see the Ollama section above), then in a second terminal with the same env vars:

```sh
dotnet run --project src/Mailvec.Embedder
```

Watch for `Embedded N messages (M chunks) in <ms>` lines. The first call is slow (model load); subsequent batches are fast. ^C when `mailvec status` shows full coverage.

### 6. Search

Same env vars, third terminal:

```sh
dotnet run --project src/Mailvec.Cli -- status
dotnet run --project src/Mailvec.Cli -- search "ramen"                  # keyword (FTS5/BM25)
dotnet run --project src/Mailvec.Cli -- search --semantic "vacation plans"
dotnet run --project src/Mailvec.Cli -- search --hybrid "tree quote"
dotnet run --project src/Mailvec.Cli -- search "lunch AND friday" -n 10  # boolean, custom limit
dotnet run --project src/Mailvec.Cli -- search '"exact phrase"'          # phrase
```

`status` prints message counts, embedding coverage, and warns if the schema's recorded embedding model disagrees with config.

The Phase 2 exit criterion is "semantic queries return relevant results the FTS layer would have missed" — a quality call that needs your eyes on a real archive. Useful comparison queries are paraphrases ("trip planning" vs `vacation`), synonyms (`bill` vs `invoice`), and topic-level recall (`subscription renewal`, `house repairs`).

### 7. Cleanup

```sh
rm -rf ~/mailvec-test
unset FASTMAIL_USER FASTMAIL_APP_PASSWORD Archive__MaildirRoot Archive__DatabasePath
# then revoke the app password at https://app.fastmail.com/settings/security/devicekeys
```

## Connecting to Claude Desktop

The MCP server is shipped as an [MCPB bundle](https://blog.modelcontextprotocol.io/posts/2025-11-20-adopting-mcpb/) — a single `.mcpb` file that Claude Desktop installs with one drag-and-drop. The bundle contains a self-contained .NET binary plus the `vec0.dylib`, so the user doesn't need a .NET SDK installed.

**Build the bundle:**

```sh
./ops/fetch-sqlite-vec.sh   # if you haven't yet
./ops/build-mcpb.sh         # writes dist/mailvec-<version>.mcpb (~50 MB)
```

**Install:** drag `dist/mailvec-<version>.mcpb` onto Claude Desktop, or `open dist/mailvec-<version>.mcpb`. The install dialog prompts for three values (defined in `manifest.json`):

- **Maildir root** — directory mbsync writes mail into. Default `~/Mail/Fastmail`.
- **Database path** — SQLite archive. Default `~/Library/Application Support/Mailvec/archive.sqlite`. Created if it doesn't exist (empty schema).
- **Ollama endpoint** — default `http://localhost:11434`. Only used to embed search queries; the indexer/embedder are separate processes.

The bundle extracts to `~/Library/Application Support/Claude/extensions/<id>/` — a non-TCC location, which avoids the `~/Documents` read block we hit during early dev.

**Updating to a new build:**

```sh
./ops/build-mcpb.sh --bump   # patch-bumps manifest.json, builds, opens the result
```

Then in Claude Desktop: Settings → Extensions → Mailvec → toggle off, accept the install prompt, quit + relaunch. Toggling off (vs uninstalling) preserves your `user_config` values across the upgrade. Claude Desktop ignores re-installs of the same version, so the bump is what makes the new binary actually take effect — without it, the install prompt is a no-op.

To bump manually instead (e.g. for a minor or major version), edit `manifest.json` and run `./ops/build-mcpb.sh` without the flag.

The indexer and embedder run as your own processes outside the bundle — they keep going across updates and don't need to be restarted when you ship a new MCP build.

**Notes:**

- The bundled binary is `osx-arm64` only (declared in `manifest.json` `compatibility.platforms`). Add `osx-x64` to `build-mcpb.sh` if you need it.
- The binary is unsigned. macOS Gatekeeper may prompt the first time Claude Desktop spawns it; allow once and it's fine. If it gets quarantined silently, `xattr -dr com.apple.quarantine "$HOME/Library/Application Support/Claude/extensions/"` clears it.
- All logs from the spawned MCP server go to stderr and land in `~/Library/Logs/Claude/mcp-server-mailvec.log`. First place to look if anything misbehaves.

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

### ✅ Phase 2 — Semantic layer

Adds locally-generated embeddings and hybrid (FTS + vector) search. **Exit criterion met:** semantic + hybrid search return chunk-level results from sqlite-vec; behaviour validated against hand-injected vectors in tests. Real-world quality validation is on you once you have an embedded archive.

- `OllamaClient` — typed `HttpClient` against `POST /api/embed` with `Microsoft.Extensions.Http.Resilience` retry/circuit-breaker.
- `ChunkingService` — paragraph-aware splitter (~4 chars/token heuristic) with configurable size + overlap; hard-splits unbroken blocks.
- `ChunkRepository` — atomic chunk + vector writes (so a message is never half-embedded).
- `EmbeddingWorker` — `BackgroundService` that polls `messages WHERE embedded_at IS NULL`, prepends subject to body, batches Ollama calls, and refuses to start if `metadata.embedding_model` disagrees with config.
- `VectorSearchService` — sqlite-vec `MATCH/k` query, returns one row per message (best-matching chunk), filters soft-deleted.
- `HybridSearchService` — Reciprocal Rank Fusion (k=60) over BM25 + vector legs.
- CLI: `search --semantic`, `search --hybrid`, `reindex --all | --folder=NAME`. `status` now surfaces embedding coverage, chunk count, and schema/config model mismatches.

### ✅ Phase 3 — MCP exposure

Wires the archive up to Claude. **Exit criterion met.**

- `Mailvec.Mcp` runs in two transports sharing the same Core wiring:
  - **HTTP** (default) on `127.0.0.1:3333` for Claude Code and the smoke tests.
  - **stdio** (`--stdio` flag) for Claude Desktop, packaged as an `.mcpb` bundle (see [Connecting to Claude Desktop](#connecting-to-claude-desktop)).
- Tools: `search_emails` (keyword / semantic / hybrid with folder/date/sender filters), `get_email`, `get_thread`, `list_folders`. The original 6-tool design merged to 4 — `recent_emails` is `search_emails` with `query` omitted, and `find_by_sender` is `search_emails` with `fromExact`.
- Hybrid search reused from Phase 2.

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
