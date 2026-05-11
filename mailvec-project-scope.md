# Mailvec — Local Archive with Semantic Search

A local-first email archive and search system for a single IMAP account, running entirely on a Mac mini (Apple Silicon, macOS). Syncs mail via `mbsync`, indexes it into SQLite with full-text and vector search, and exposes it to AI agents via an MCP server. Originally designed against Fastmail — and the included `mbsyncrc.example` reflects that — but any IMAP server works since `mbsync` is the sync layer.

> **About this document.** This is the original design doc, written at Phase 0 to scope the build. It captures the intended shape of the system and the reasoning behind major decisions, and is updated when scope changes (new phases, dropped non-goals). **It is not a blow-by-blow reflection of the current codebase** — for that, see [`README.md`](README.md) (user-facing layout, setup, current Phase status) and [`CLAUDE.md`](CLAUDE.md) (operational gotchas accumulated phase-by-phase). When the three documents drift, README + CLAUDE.md are authoritative on what's actually built; this doc is authoritative on what we set out to build and what's still planned.

---

## 1. Goals and non-goals

### Goals

- **Durable local archive.** Every message in the IMAP account exists as both a Maildir file on disk and a row in a SQLite database on the Mac mini.
- **Two search modes.** Keyword/boolean search via SQLite FTS5, and semantic search via locally-generated embeddings.
- **AI-agent access.** An MCP server exposing search and retrieval tools. Claude Desktop is the v1 target via an MCPB bundle and Claude Code talks to it over local HTTP; Phase 5 extends the same transports to other locally-running agents (Gemini CLI, Codex CLI, ChatGPT desktop). Public-HTTPS / OAuth access for the cloud LLMs is in §12 Future ideas.
- **Runs unattended.** All components run as launchd services and survive reboot without intervention.
- **No cloud dependencies for search/storage.** IMAP sync is the only network hop; embeddings are generated locally via Ollama.

### Non-goals

- Not a mail client. No reading UI, no composing, no marking as read.
- Not bidirectional. The archive is read-only from the user's perspective; changes on the IMAP server flow down, never up.
- Not multi-user. Single account, single machine.
- Not real-time. Sync runs on a timer (every 5 minutes is fine). No IDLE/push.
- No authentication on the MCP server. The server stays bound to `127.0.0.1`, so the macOS user boundary is the trust boundary. OAuth + HTTPS would only be needed to expose Mailvec to cloud LLMs over a public tunnel — see §10 Security model and §12 Future ideas.

---

## 2. Architecture

```
┌────────────┐    IMAP     ┌─────────┐     Maildir     ┌──────────────────┐
│  IMAP svr  │◀───────────▶│ mbsync  │────────────────▶│  ~/Mail/<acct>   │
└────────────┘             └─────────┘                 └──────────────────┘
                                                                │
                                                                │ FileSystemWatcher
                                                                ▼
                                                       ┌────────────────────┐
                                                       │ Mailvec.Indexer    │
                                                       │ (BackgroundService)│
                                                       └────────────────────┘
                                                                │
                                                                │ writes rows + FTS
                                                                ▼
                ┌────────────────────┐       reads/writes    ┌─────────────────────┐
                │ Mailvec.Embedder   │──────────────────────▶│   archive.sqlite    │
                │ (BackgroundService)│                       │   + sqlite-vec      │
                └────────────────────┘                       │   + FTS5            │
                           │                                 └─────────────────────┘
                           │ embeds text                                │
                           ▼                                            │
                  ┌────────────────┐                                    │ reads
                  │    Ollama      │                                    ▼
                  │ localhost:11434│                         ┌──────────────────────┐
                  └────────────────┘                         │    Mailvec.Mcp       │
                                                             │  HTTP :3333 / stdio  │
                                                             └──────────────────────┘
                                                                        │ MCP
                                                                        ▼
                                                       ┌────────────────────────────┐
                                                       │ Claude Desktop (MCPB),     │
                                                       │ Claude Code,               │
                                                       │ Gemini CLI, Codex CLI,     │
                                                       │ ChatGPT desktop (Phase 5)  │
                                                       └────────────────────────────┘
```

Four independent processes — indexer, embedder, MCP server, CLI — communicating only through the filesystem (Maildir) and the SQLite database. Each can be restarted or replaced without affecting the others.

---

## 3. Components

### 3.1 mbsync (external, not our code)

`isync`/`mbsync` handles the IMAP sync. Installed via Homebrew, configured via `~/.mbsyncrc`, scheduled via launchd. Produces a Maildir tree at a configurable root (e.g. `~/Mail/Fastmail`).

**Our responsibility:** ship an `mbsyncrc.example` template and a launchd plist.

### 3.2 Mailvec.Core (class library)

Shared types and infrastructure used by all other projects.

- Domain models (`Message`, `Chunk`, `SearchResult`, etc.)
- `ArchiveDbContext` or equivalent data-access layer
- SQLite connection factory with `sqlite-vec` extension loading
- Ollama HTTP client wrapper
- Configuration POCOs (`ArchiveOptions`, `OllamaOptions`, `IndexerOptions`, `McpOptions`)
- Shared logging setup

### 3.3 Mailvec.Indexer (worker service)

A `BackgroundService` that keeps the SQLite `messages` table in sync with the Maildir on disk.

**Responsibilities:**
- On startup, scan the Maildir root and reconcile against `sync_state`.
- Watch the Maildir with `FileSystemWatcher` (recursive, debounced by ~500ms — mbsync writes to `tmp/` then renames into `new/` or `cur/`).
- Parse each file with MimeKit, extract headers, text body, and HTML body.
- Upsert rows into `messages` keyed by Message-ID, with Maildir path tracked separately so file moves (e.g. `new/` → `cur/`) don't cause duplicates.
- Handle deletions: mark rows as `deleted_at` rather than hard-delete (optional; can also hard-delete).
- Trigger FTS5 updates via triggers on `messages`.

**Does not** call Ollama or produce embeddings. Pure ingest.

### 3.4 Mailvec.Embedder (worker service)

A `BackgroundService` that generates embeddings for messages lacking them.

**Responsibilities:**
- Periodically (every 30s when idle, immediately when the indexer signals new rows — optional SQLite trigger or file-based event) query for `messages WHERE embedded_at IS NULL`.
- For each message, chunk the body text into overlapping windows sized to fit the embedding model's context.
- Call `POST http://localhost:11434/api/embed` with batches of chunks.
- Write chunks and their vectors into `chunks` and `chunk_embeddings`.
- Update `messages.embedded_at`.
- On startup, verify the `metadata` table's recorded embedding model matches the configured model; if not, refuse to start and instruct the user to run `mailvec reindex --all`. Never silently mix vector spaces.

### 3.5 Mailvec.Mcp (AspNetCore server)

An MCP server over HTTP, implemented with `ModelContextProtocol.AspNetCore`. Binds to `localhost:3333` by default.

**Tools exposed** (the original 6-tool plan merged to 4 search/fetch tools — `recent_emails` is `search_emails` with `query` omitted, `find_by_sender` is `search_emails` with `fromExact` — plus `get_attachment` added in Phase 3 for attachment delivery):

| Tool | Purpose |
|---|---|
| `search_emails` | Keyword / semantic / hybrid search with filters (folder, date range, `fromContains`, `fromExact`). With `query` omitted, becomes a date-sorted browse. |
| `get_email` | Fetch full message body + headers + attachment metadata (filename, MIME, size, `partIndex`) by ID. |
| `get_thread` | Fetch all messages in a thread (or just the singleton if `thread_id` is NULL). |
| `list_folders` | List Maildir folders with message counts. |
| `get_attachment` | Extract one attachment by `(messageId, partIndex)` to `~/Downloads/mailvec/`, return the path. Inlines images as `ImageContentBlock` and small text-ish files as a text block. |

**Transports**: HTTP (default, `127.0.0.1:3333`) for Claude Code and smoke tests; stdio (`--stdio`) packaged as an `.mcpb` bundle for Claude Desktop. Phase 5 extends the same two transports to other local agents (Gemini CLI, Codex CLI, ChatGPT desktop) — no protocol changes, just per-client config and quirk capture. Public-HTTPS access for cloud LLMs is in §12 Future ideas.

**Hybrid search approach (v1):**
1. Run FTS5 query → top 50 candidates with BM25 scores.
2. Run vector similarity query → top 50 chunks with distances, join back to messages.
3. Reciprocal rank fusion (RRF) across the two lists → final ranked set.
4. Return top N with snippets.

### 3.6 Mailvec.Cli (console app, optional but recommended)

Admin/operator commands that run against the same database.

- `mailvec status` — counts, last sync time, embedding coverage %
- `mailvec reindex` — clear `embedded_at` for matching rows, triggering re-embed
- `mailvec rebuild-fts` — drop and recreate FTS5 content
- `mailvec search "query"` — run the same hybrid search the MCP server uses, for debugging
- `mailvec rebuild-bodies` — re-derive `body_text` from stored `body_html` after the HTML→text converter changes; with `--reembed` also clears `embedded_at` so the embedder regenerates vectors
- `mailvec audit-embeddings` — sweep the vector index for zero/NaN/abnormal-norm vectors (sanity check after large reindexes or Ollama upgrades)
- `mailvec eval` — runs a fixture-driven query set against the live archive to compare BM25 / semantic / hybrid quality

---

## 4. Data model

SQLite file at `~/Library/Application Support/Mailvec/archive.sqlite` by default. WAL mode, `foreign_keys=ON`.

```sql
-- Core messages table
CREATE TABLE messages (
    id                INTEGER PRIMARY KEY,
    message_id        TEXT UNIQUE NOT NULL,   -- RFC 5322 Message-ID, with angle brackets stripped
    thread_id         TEXT,                   -- Root Message-ID of the References chain, or self
    maildir_path      TEXT NOT NULL,          -- relative path under MAILDIR_ROOT, without filename
    maildir_filename  TEXT NOT NULL,          -- full filename (flags may change — tracked separately)
    folder            TEXT NOT NULL,          -- derived from maildir_path, e.g. "INBOX", "Archive.2023"
    subject           TEXT,
    from_address      TEXT,
    from_name         TEXT,
    to_addresses      TEXT,                   -- JSON array of objects: [{name, address}, ...]
    cc_addresses      TEXT,                   -- JSON array
    date_sent         TEXT,                   -- ISO 8601
    date_received     TEXT,                   -- ISO 8601
    size_bytes        INTEGER,
    has_attachments   INTEGER DEFAULT 0,
    body_text         TEXT,                   -- plain text body, extracted from text/plain OR html->text
    body_html         TEXT,                   -- raw html if present (may be large — consider separate table)
    raw_headers       TEXT,                   -- serialized full header block for fidelity
    indexed_at        TEXT NOT NULL,
    embedded_at       TEXT,                   -- NULL = needs embedding
    deleted_at        TEXT                    -- soft-delete when Maildir file disappears
);

CREATE INDEX idx_messages_thread    ON messages(thread_id);
CREATE INDEX idx_messages_folder    ON messages(folder);
CREATE INDEX idx_messages_date_sent ON messages(date_sent);
CREATE INDEX idx_messages_to_embed  ON messages(embedded_at) WHERE embedded_at IS NULL;

-- Full-text index
CREATE VIRTUAL TABLE messages_fts USING fts5(
    subject, from_name, from_address, body_text,
    content='messages', content_rowid='id',
    tokenize='porter unicode61'
);

-- Triggers to keep FTS5 in sync with messages (INSERT/UPDATE/DELETE)
-- ... standard fts5 external content trigger pattern

-- Chunks for embedding
CREATE TABLE chunks (
    id           INTEGER PRIMARY KEY,
    message_id   INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    chunk_index  INTEGER NOT NULL,
    chunk_text   TEXT NOT NULL,
    token_count  INTEGER,
    UNIQUE(message_id, chunk_index)
);

CREATE INDEX idx_chunks_message ON chunks(message_id);

-- Vector index (sqlite-vec)
-- Dimension matches the configured embedding model.
-- mxbai-embed-large = 1024; nomic-embed-text = 768.
CREATE VIRTUAL TABLE chunk_embeddings USING vec0(
    chunk_id   INTEGER PRIMARY KEY,
    embedding  FLOAT[1024]
);

-- Tracks Maildir state for reconciliation
CREATE TABLE sync_state (
    maildir_full_path TEXT PRIMARY KEY,
    message_id        TEXT,
    last_seen_at      TEXT NOT NULL,
    content_hash      TEXT   -- sha256 of file body; used to detect flag-only vs content changes
);

-- Schema + embedding-model metadata
CREATE TABLE metadata (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
-- Seed rows: schema_version, embedding_model, embedding_dimensions, last_sync_at
```

### Schema considerations

- **Embedding model is part of the schema.** Mixing vectors from different models silently produces garbage. The `metadata` table records which model was used; the embedder refuses to start if the config disagrees. Switching models requires `mailvec reindex --all`.
- **`body_html` can be large.** Consider splitting into a `message_bodies` table keyed on `message_id` if the main table gets unwieldy. Optimize later if needed.
- **Attachment filenames *and* content are indexed.** Phase 3 added an `attachments` table (filename, content_type, size, partIndex) plus FTS5 coverage of the filename column, so a query like `"mortgage_statement_2024.pdf"` matches the email it's attached to. Phase 4.5 (schema v4) added per-format **content** indexing for native PDFs (PdfPig), DOCX (DocumentFormat.OpenXml), and plain text — extracted text lives in `attachments.extracted_text` and is chunked + embedded alongside the parent message body. Search results carry `matchedAttachment` so Claude can identify which document drove the match. **OCR remains out of scope** — scanned/image-only PDFs are stamped `extraction_status='no_text'` and not retried.
- **Content-change detection.** `messages.content_hash` is the SHA-256 of `MimeMessage.Body` bytes (added in migration `003_message_body_hash.sql`). Used to invalidate embeddings when the body changes — distinct from `sync_state.content_hash`, which is keyed on Maildir path and would false-positive on rename/move. See CLAUDE.md Phase 2 gotchas for the full reasoning.

---

## 5. Dependencies

### Runtime

- .NET 10 SDK (stable as of late 2025; required by MCP C# SDK templates)
- SQLite 3.46+ (ships with macOS; Ollama-side is fine)
- `sqlite-vec` loadable extension, installed as a `.dylib` under `./runtimes/osx-arm64/native/`
  - Repo: https://github.com/asg017/sqlite-vec
  - Distribute the prebuilt binary with the solution, or pull via a build step
- Ollama, installed via https://ollama.com, running as a launchd service
- mbsync (`brew install isync`)

### NuGet packages

**Mailvec.Core**
- `Microsoft.Data.Sqlite.Core` + `SQLitePCLRaw.bundle_e_sqlite3` — SQLite provider; `vec0.dylib` is loaded at runtime via the extension-loading API.
- `MimeKit` — MIME parsing.
- `AngleSharp` — HTML→text conversion. The original doc referenced MimeKit's `HtmlToText`, which doesn't exist; AngleSharp lets us strip marketing-email noise (hidden preheader text, tracking pixels, footer boilerplate). See CLAUDE.md Phase 1 gotchas.
- `Serilog` + `Serilog.Extensions.Hosting` + `Serilog.Sinks.Console` + `Serilog.Sinks.File` + `Serilog.Settings.Configuration` — in-process rolling-file logging shared across the three .NET services via `SerilogSetup`. Replaced an earlier custom bash rotator + dedicated launchd plist.
- `Microsoft.Extensions.Configuration.Binder`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Logging.Abstractions`.

**Mailvec.Indexer**
- References Mailvec.Core
- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Logging.Console`

**Mailvec.Embedder**
- References Mailvec.Core
- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Http.Resilience` — retry/backoff for Ollama calls

**Mailvec.Mcp**
- References Mailvec.Core
- `ModelContextProtocol` (1.2.0+) — core MCP SDK
- `ModelContextProtocol.AspNetCore` — HTTP transport
- `Microsoft.AspNetCore.App` (framework reference)

**Mailvec.Cli**
- References Mailvec.Core
- `System.CommandLine` (2.0) — argument parsing

**Testing**
- `xunit`, `xunit.runner.visualstudio` — assertions use plain `Assert.*` (no FluentAssertions; one less dependency to keep current).

---

## 6. Solution layout

```
Mailvec/
├── Mailvec.slnx                        # .NET 10 emits XML solution format by default
├── Directory.Build.props               # shared: net10.0, Nullable, TreatWarningsAsErrors
├── Directory.Packages.props            # central package management (CPM)
├── manifest.json                       # Claude Desktop MCPB manifest
├── README.md
├── src/
│   ├── Mailvec.Core/                   # shared types, SQLite + Ollama clients, search, health
│   ├── Mailvec.Indexer/                # Maildir → SQLite worker
│   ├── Mailvec.Embedder/               # SQLite → Ollama → vectors worker
│   ├── Mailvec.Mcp/                    # AspNetCore MCP server (HTTP and stdio)
│   └── Mailvec.Cli/                    # admin commands (status, search, reindex, etc.)
├── tests/
│   ├── Mailvec.Core.Tests/
│   ├── Mailvec.Indexer.Tests/
│   └── Mailvec.Mcp.Tests/
├── ops/
│   ├── mbsyncrc.example
│   ├── launchd/
│   │   ├── com.mailvec.mbsync.plist
│   │   ├── com.mailvec.indexer.plist
│   │   ├── com.mailvec.embedder.plist
│   │   └── com.mailvec.mcp.plist       # for HTTP transport (cross-vendor); Claude Desktop uses MCPB instead
│   ├── install.sh                      # Phase 4: publishes services, loads plists
│   ├── fetch-sqlite-vec.sh             # one-shot: pulls vec0.dylib
│   ├── build-mcpb.sh                   # packages dist/mailvec-<version>.mcpb
│   └── dev-fetch-imap.py               # dev-only: fetches recent mail without mbsync
├── runtimes/
│   └── osx-arm64/native/vec0.dylib     # sqlite-vec extension
└── schema/
    ├── 001_initial.sql
    └── migrations/
        └── 003_message_body_hash.sql
```

---

## 7. Configuration

Each runnable service has its **own** `appsettings.json` containing only the sections it needs. POCOs live in `Mailvec.Core/Options/`. Local overrides go in `appsettings.Local.json` (gitignored). There is no central shared config file.

Section ownership:

- `Archive` (`DatabasePath`, `SqliteVecExtensionPath`) — bound by all four executables.
- `Ingest` (`MaildirRoot`) — bound by the Indexer, the CLI, and the MCP server (`get_attachment` reads `.eml` bytes off disk). The Embedder is pure-SQLite. Originally on `Archive`, split out in Phase 1 so the MCPB manifest doesn't prompt the embedder for a Maildir path it never reads.
- `Ollama` — bound by the Embedder (does the embedding) and the MCP server (embeds search queries on the fly).
- `Mcp` — MCP server only.
- `Fastmail` — optional; opt-in webmail deep-links on search results.

Representative shape (sections and key fields):

```json
{
  "Archive":  { "DatabasePath": "~/Library/Application Support/Mailvec/archive.sqlite",
                "SqliteVecExtensionPath": "./runtimes/osx-arm64/native/vec0.dylib" },
  "Ingest":   { "MaildirRoot": "~/Mail/Fastmail" },
  "Ollama":   { "BaseUrl": "http://localhost:11434",
                "EmbeddingModel": "mxbai-embed-large", "EmbeddingDimensions": 1024,
                "KeepAlive": "30m", "MaxBatchSize": 16, "RequestTimeoutSeconds": 60 },
  "Indexer":  { "ScanIntervalSeconds": 300, "DebounceMilliseconds": 500, "MaxHtmlBodyBytes": 1048576 },
  "Embedder": { "PollIntervalSeconds": 30, "ChunkSizeTokens": 200, "ChunkOverlapTokens": 32,
                "MaxConcurrentRequests": 2 },
  "Mcp":      { "BindAddress": "127.0.0.1", "Port": 3333,
                "SearchDefaultLimit": 20, "SearchMaxLimit": 100,
                "AttachmentDownloadDir": "~/Downloads/mailvec",
                "AttachmentInlineTextMaxBytes": 262144,
                "LogToolCalls": false },
  "Fastmail": { "AccountId": "u1234abcd", "WebUrl": "https://app.fastmail.com" }
}
```

Env-var overrides use the standard ASP.NET double-underscore convention with **no prefix**: `Ingest__MaildirRoot=/path` works for any binary that binds the section.

---

## 8. Phased build plan

Recommended order for Claude Code to scaffold and for incremental development.

### Phase 0 — Repo scaffold
Solution, projects, CPM, Directory.Build.props, README, gitignore, CI stub.

### Phase 1 — Ingest pipeline (value: searchable archive)
1. Schema migration runner in `Mailvec.Core`.
2. MimeKit-based parser with tests against fixture `.eml` files.
3. `MaildirScanner` — one-shot full scan that populates `messages` + `sync_state`.
4. `MaildirWatcher` — incremental updates via FileSystemWatcher.
5. FTS5 triggers and a working `Mailvec.Cli search "query"` command using BM25.

**Exit criteria:** can point at a Maildir and do useful keyword search.

### Phase 2 — Semantic layer
1. Ollama HTTP client + integration test against a running Ollama.
2. `ChunkingService` — token-aware splitter (use a simple heuristic like ~4 chars/token for v1; upgrade to a real tokenizer later).
3. `EmbeddingWorker` that processes unembedded rows in batches.
4. Hybrid search in `Mailvec.Core/Search` using RRF.
5. `Mailvec.Cli search --semantic` command for comparison.

**Exit criteria:** semantic queries return relevant results the FTS layer would have missed.

### Phase 3 — MCP exposure
1. Bare-bones MCP server with a single `search_emails` tool.
2. Register with Claude Desktop via its MCP config, verify end-to-end. Final shape: an MCPB bundle (`ops/build-mcpb.sh` → `dist/mailvec-<version>.mcpb`) installed via Settings → Extensions, since Claude Desktop's Custom Connectors GUI requires HTTPS we can't provide locally and its config schema only supports stdio.
3. Add remaining tools — landed as 4 search/fetch tools (`search_emails`, `get_email`, `get_thread`, `list_folders`) plus `get_attachment`. The original `recent_emails` and `find_by_sender` collapsed into `search_emails` (`query` omitted / `fromExact` filter respectively).
4. HTTP transport on `127.0.0.1:3333` for Claude Code and smoke tests. Note: Claude.ai Custom Connectors and other cloud LLMs require HTTPS + OAuth — that's Phase 5, not Phase 3.

**Exit criteria:** Claude can answer "when did Acme last quote me for the tree work?" without being told where to look. **Met.**

### Phase 4 — Operationalization
1. ✓ launchd plists. Templates in `ops/launchd/` for mbsync + indexer + embedder + MCP HTTP server (Ollama installed separately, e.g. via `brew services`). The MCP HTTP plist is the cross-vendor path; Claude Desktop spawns its stdio binary on demand via the MCPB bundle and doesn't need a plist. The three .NET plists carry placeholders (`__DOTNET__`, `__INSTALL_PREFIX__`, `__LOG_DIR__`, `__DB_PATH__`, `__MAILDIR_ROOT__`, `__OLLAMA_URL__`, `__FASTMAIL_ACCOUNT_ID__`) and surface the user-tunable config as `EnvironmentVariables` so changes are a plist edit, not a republish. The mbsync plist substitutes `__MBSYNC__` and `__MBSYNCRC__` so its config path is pinned at install time.
2. ✓ `install.sh` publishes the three .NET services into per-service subdirs under `~/.local/share/mailvec/{indexer,embedder,mcp}/` (framework-dependent — uses the `dotnet` already on PATH), prompts for the four site-specific values (Maildir root, DB path, Ollama URL, optional Fastmail account ID), substitutes the placeholders, boots out any existing agents (idempotent reinstall), `launchctl bootstrap`s them, and polls `/health` for up to 15 s. `--uninstall` reverses everything while preserving the published binaries, the database, and the logs.
3. ✓ Health endpoint on the MCP server. `GET /health` returns a structured snapshot (DB path / message counts / last-indexed timestamp, schema vs config embedding model, embedding coverage %, Ollama reachability). HTTP 200 when all green, 503 when degraded so monitors can alert without parsing the body. Local-only by virtue of `Mcp:BindAddress=127.0.0.1`.
4. ✓ Log rotation. Done in-process via Serilog's File sink — `Mailvec.Core/Logging/SerilogSetup.cs` is wired in each service's `Program.cs`. Primary log: `~/Library/Logs/Mailvec/mailvec-<service>-<YYYYMMDD>.log`, daily rolling, 10 MB cap per file, 14 files retained. The launchd plist sets `MAILVEC_LAUNCHD=1` so Serilog's Console sink stays off in production (avoids duplicating into `StandardOutPath`); a small `<service>.launchd.log` still captures pre-Serilog startup output and panics. mbsync is the only non-.NET service and writes to its own small launchd-captured files (no rotation needed; volume is negligible). See CLAUDE.md Phase 4 gotchas.
5. ✓ `mailvec status` surfaces message count, embedding coverage, schema/config model match.

**Exit criteria:** reboot the Mac mini; everything comes back without intervention. **Met.**

### Phase 5 — Support for non-Claude local agents (Gemini CLI, Codex / ChatGPT)

The MCP server today is exercised end-to-end by Claude Desktop (stdio via the MCPB bundle) and Claude Code (HTTP on `127.0.0.1:3333`). Other locally-running agents that speak MCP — Google's Gemini CLI, OpenAI's Codex CLI, and the ChatGPT desktop app once its MCP-server registration ships — should work over the same two transports without protocol changes, but each has its own config shape, environment-variable conventions, and process-spawning quirks that need a dedicated pass (the equivalent of the Claude Desktop sanitized-env / TCC-block findings captured in CLAUDE.md Phase 3 gotchas).

All three stdio clients point at the same generic launcher: `~/.local/bin/mailvec-mcp-stdio`, written by `ops/install-stdio-launcher.sh`. The launcher centralizes the env workarounds (DOTNET_ROOT, sanitized PATH, default config values) so each client's config block reduces to "command = the launcher, args = [], env = whatever you want to override". `docs/clients/` carries the per-client snippets — `claude-desktop.md` and `claude-code.md` are templates; the three Phase 5 entries fill in around them.

1. **Gemini CLI.** Register Mailvec via `~/.gemini/settings.json`'s `mcpServers` block, pointing at `~/.local/bin/mailvec-mcp-stdio`. The launcher already handles `DOTNET_ROOT` / PATH; the Phase 5 task is to verify there are no *additional* spawning quirks beyond the Claude Desktop set captured in CLAUDE.md.
2. **Codex CLI.** Register Mailvec under `[mcp_servers.mailvec]` in `~/.codex/config.toml`, again pointing at the launcher. Validate stdio framing under Codex's spawned environment.
3. **ChatGPT desktop app.** Contingent on the local MCP-server registration UI being available in the user's release; document the path when it is.
4. **Documentation + smoke tests.** Add three more docs under `docs/clients/` (one per agent) following the `claude-desktop.md` template. Add an integration tier exercised during release prep.

Concretely, no new server-side code is expected — the work is per-client configuration documentation, capturing each agent's launch-environment quirks, and a repeatable smoke recipe.

**Exit criteria:** Mailvec answers "when did Acme last quote me?" from at least one non-Claude local agent.

---

## 9. Operational notes

- **Bootstrapping.** Initial sync of a full IMAP archive is slow (hours to a day for large archives). Budget for this and run mbsync manually the first time. The embedder can run against the partial archive as it grows.
- **Model changes.** If the configured embedding model changes, the embedder must refuse to start and print a `mailvec reindex --all` instruction. Never silently mix vector spaces.
- **Database backup.** The SQLite file is the only persistent state produced by this system (the Maildir is reconstructable from IMAP). Should be included in Time Machine / existing backup regimen. WAL checkpointing should be configured sensibly so the `-wal` file doesn't grow unbounded.
- **Ollama lifecycle.** Ollama runs as its own launchd service (installed separately, e.g. via `brew services start ollama`). Our services assume it's reachable at `localhost:11434`. The embedder uses `Microsoft.Extensions.Http.Resilience` for retry/backoff on transient failures.
- **File permissions.** Maildir readable by the user account running the indexer. SQLite file writable by indexer, embedder, and MCP server (all run as the same user on this machine).

---

## 10. Security model

Single-user, single-Mac. The trust boundary is the macOS user account. Inside that boundary, every local process runs with full access to the archive; outside, Mailvec is unreachable.

### What's exposed

| Surface | Binding | Auth | Who can reach it |
| --- | --- | --- | --- |
| MCP HTTP | `127.0.0.1:3333` (configurable via `Mcp:BindAddress`) | none | any process on the same machine running as the user |
| MCP stdio | child process of the spawning agent | inherits agent's identity | the agent (Claude Desktop, Claude Code, or a Phase 5 client) and whatever it spawned |
| `/health` | same Kestrel as MCP HTTP | none | same as MCP HTTP |
| Ollama (outbound) | `127.0.0.1:11434` (configurable) | none | the embedder + MCP query embeddings only — read-only against Ollama |
| SQLite file | filesystem | unix permissions | the user (and root) |
| Maildir | filesystem | unix permissions | the user (and root) |

### Tools and data flow

All five MCP tools (`search_emails`, `get_email`, `get_thread`, `list_folders`, `get_attachment`) are **read-only against the database**. None mutate `messages`, `chunks`, or `attachments`. The only write any tool performs is `get_attachment` extracting bytes from the Maildir to `~/Downloads/mailvec/<msgId>-<part>-<safe-name>` — covered by [defense-in-depth path checks](src/Mailvec.Core/Attachments/AttachmentExtractor.cs):

- `Path.GetFileName` strips directory components from caller-supplied filenames
- canonical-path containment refuses any target outside the configured download dir
- a `ReparsePoint` check refuses to overwrite an existing symlink at the destination (TOCTOU mitigation)
- write-then-rename via `.part` sibling so a concurrent reader never sees a partial file

`AttachmentDownloadDir` is intentionally `~/Downloads/mailvec/` (visible to the user). Don't move it to a hidden directory or `~/Library/Caches/` — that hides forensic evidence if a tool ever does write something unexpected.

### What's accepted

These are explicit decisions, not oversights:

- **Any local process running as the user can call any tool.** The HTTP server has no auth, so a malicious local process (e.g. a compromised npm install in another shell) can `curl http://127.0.0.1:3333/` and read your mail. The mitigation is the same one that protects every other local file you own: don't run hostile code as your user. Adding HMAC-token auth on the HTTP loopback is a future option; not built today because the only realistic adversary already has unix-level read access to `~/Mail/` and `~/Library/Application Support/Mailvec/archive.sqlite` and doesn't need MCP to extract them.
- **No per-tool authorization.** Any caller that can invoke `search_emails` can also invoke `get_attachment`. Trivially simple while every tool is read-only — revisit if a write tool ever lands (sending mail is in §13 / out of scope, but the principle applies if anything in that direction ever gets considered).
- **No rate limiting.** A chatty agent can burn local CPU on embedding queries and SQLite reads. SQLite WAL handles concurrent readers fine, and Ollama itself is the natural bottleneck on the embedding leg, so the worst-case is "your machine slows down briefly." Worth revisiting if Phase 5 surfaces an agent that fires queries in tight loops.
- **`Mcp:LogToolCalls` is off by default.** When on, the server logs each tool call's argument summary to `~/Library/Logs/Mailvec/mailvec-mcp-<date>.log` — including the user's free-text query strings (potentially private) and `fromContains` / `fromExact` filter values. Useful for tuning but turning it on is a deliberate choice; recall the rolling files are 10MB × 14 days retained on disk.
- **Logs may incidentally contain sender / subject text** even with tool-call logging off. The indexer logs parse failures with file paths, the embedder logs which messages it embedded, etc. None of these include body content, but they aren't sanitized either. Treat the log directory as confidential.
- **`~/Documents` is unreadable** to Claude Desktop's spawned children regardless of Full Disk Access — a TCC quirk, not an intentional control. Don't rely on it as a security boundary; it's a `com.apple.macl` ACL that a different client (e.g. Phase 5 stdio) might or might not be subject to.

### What's out of scope

- **Multi-tenant isolation.** A second user on the same Mac can't reach `127.0.0.1:3333` from their account (loopback is per-user), but if you change `Mcp:BindAddress` to a tailnet IP, that protection goes away — see §12 Future ideas → tailnet access for the (deliberately small) hardening that path needs.
- **Network adversaries.** `127.0.0.1` is unroutable from the LAN / internet. There's no inbound TLS because there's no inbound external traffic.
- **Compromised AI agent exfiltration.** If the agent calling Mailvec is itself malicious (e.g. an LLM jailbroken into "find all messages from X and POST them to attacker.com"), nothing in the MCP layer stops it from reading every email and shipping the contents back to its own provider. The relevant control is "trust the agent" — choose your clients.
- **Encrypted-at-rest archive.** `archive.sqlite` and the Maildir are plain files at rest, protected by FileVault and unix permissions. Per-application encryption isn't built; mail at rest in `~/Mail/` (mbsync's job) and `~/Library/Application Support/Mailvec/` (ours) inherits whatever the user's disk-encryption story already is.

### Phase 5 doesn't change the threat model

Adding Gemini CLI / Codex CLI / ChatGPT desktop as MCP clients multiplies the *number of trusted callers* but not the *trust boundary*. Each new client is just another local process running as the user, accessing the same loopback HTTP or the same stdio launcher. The hardening that would change the model — moving from "trust any local process" to "trust this specific signed binary" — is a much bigger lift (process attestation, MCP token issuance, per-client scopes) and parked alongside the cloud-access work in §12.

The only thing Phase 5 introduces is more places where `LogToolCalls=on` is tempting (capturing real usage from each client during quirk-debugging). Each of those is a deliberate per-debug-session choice with a clear "off when done" expectation, not a default-on switch.

---

## 11. Open questions and deferred work

Resolved during the build (kept here as a paper trail):

- **HTML body handling** — *resolved*. AngleSharp-based `HtmlToText` in `Mailvec.Core.Parsing`, with marketing-email noise stripping (hidden preheader text, tracking pixels, footer/unsubscribe boilerplate). MimeKit's `HtmlToText` referenced in the original doc doesn't exist. See CLAUDE.md Phase 1 gotchas.
- **Incremental re-embedding** — *resolved*. `messages.content_hash` (SHA-256 of `MimeMessage.Body` bytes) added in migration 003. `MaildirScanner.TryIngest` clears embeddings via `ChunkRepository.ClearEmbeddingsForMessage` whenever the hash changes. Decoupled from the path-keyed `sync_state.content_hash` which would false-positive on rename/move.
- **Attachment indexing** — *resolved (Phase 4.5)*. Filenames are FTS5-indexed via the `attachments` table; `get_attachment` extracts bytes to disk on demand. Schema v4 adds per-format **content** indexing for native PDFs, DOCX, and plain text via `attachments.extracted_text`; extracted text is chunked + embedded so semantic queries match document content. Search responses include `matchedAttachment` (partIndex + filename) when an attachment drove the match. OCR for scanned PDFs is still out of scope — those land at `extraction_status='no_text'`.

Still open:

- **Thread reconstruction.** Simple `In-Reply-To` / `References` heuristic. Acceptable today; revisit if mismatches with Fastmail's JMAP threading become a usability issue.
- **JMAP-specific metadata.** IMAP flags are available via mbsync; JMAP-only fields (masked email, server-side labels) require a separate JMAP path. Not currently planned.
- **WAL checkpointing strategy.** No periodic auto-checkpoint configured beyond SQLite's default (every 1000 frames). For one-off cleanup after a bulk embed, `mailvec checkpoint` runs `PRAGMA wal_checkpoint(TRUNCATE)` and reports before/after sizes. Worth measuring `-wal` file growth on a long-running install before deciding whether automatic periodic checkpoints are needed.

---

## 12. Future ideas (not in current scope)

These were considered, then deferred. Captured here so the reasoning isn't lost.

### Cross-vendor / cloud-LLM access via public HTTPS

The Anthropic / Google / OpenAI cloud clients (Claude.ai web app, Gemini in the browser, ChatGPT Connectors) cannot reach `127.0.0.1` since they're themselves cloud services. Exposing Mailvec to them would need three things on top of today's HTTP transport:

1. **Public reachability.** Cloudflare Tunnel (`cloudflared`) or Tailscale **Funnel** (the public variant — ordinary tailnet doesn't reach those clients) terminates TLS so the MCP server can stay bound to `127.0.0.1` and the tunnel connects locally.
2. **OAuth 2.1 (PKCE).** Cloud connectors expect MCP's standard OAuth flow. The .NET MCP SDK has authentication scaffolding; the open call is the issuer — self-hosted, Cloudflare Access, or Tailscale identity in front are all viable, with different implications for who can approve a new login.
3. **Per-tool authorization.** All current tools are read-only against the local DB and Maildir, so the simplest scope is "any authenticated user can call any tool." Revisit if mutating tools are added.

Deferred because the value of "Claude.ai / ChatGPT / Gemini in the browser searching my email" is real but lower than the operational cost of running OAuth + a public tunnel for a single-user system. Phase 5's local-agent path covers most of the same use cases without the auth surface or external tunnel dependency.

### Tailnet-only access from another personal machine

A middle ground between local-only and public — laptop on the same Tailscale tailnet hitting the Mac mini's MCP server. Tailscale ACLs gate at the network layer, so no OAuth is needed; the change is one config knob (`Mcp:BindAddress` from `127.0.0.1` to the tailnet IP) plus a launchd plist re-render. Cheap when wanted; not built today.

### Multi-user / federated identity

Implied by any cloud-access path. Out of scope for this single-user system.

---

## 13. Out of scope entirely

- Sending mail
- Modifying server-side state (marking read, moving, deleting)
- Multi-account support
- Calendar, contacts, files — even though Fastmail offers these via CalDAV/CardDAV/WebDAV, this project is mail-only
- Web UI
- Real-time push notifications (mbsync is timer-driven, not IDLE/JMAP push)
