# Mailvec — Local Archive with Semantic Search

A local-first email archive and search system for a single IMAP account, running entirely on a Mac mini (Apple Silicon, macOS). Syncs mail via `mbsync`, indexes it into SQLite with full-text and vector search, and exposes it to AI agents via an MCP server. Originally designed against Fastmail — and the included `mbsyncrc.example` reflects that — but any IMAP server works since `mbsync` is the sync layer.

> **About this document.** This is the original design doc, written at Phase 0 to scope the build. It captures the intended shape of the system and the reasoning behind major decisions, and is updated when scope changes (new phases, dropped non-goals). **It is not a blow-by-blow reflection of the current codebase** — for that, see [`README.md`](README.md) (user-facing layout, setup, current Phase status) and [`CLAUDE.md`](CLAUDE.md) (operational gotchas accumulated phase-by-phase). When the three documents drift, README + CLAUDE.md are authoritative on what's actually built; this doc is authoritative on what we set out to build and what's still planned.

---

## 1. Goals and non-goals

### Goals

- **Durable local archive.** Every message in the IMAP account exists as both a Maildir file on disk and a row in a SQLite database on the Mac mini.
- **Two search modes.** Keyword/boolean search via SQLite FTS5, and semantic search via locally-generated embeddings.
- **AI-agent access.** An MCP server exposing search and retrieval tools. Claude Desktop is the v1 target via an MCPB bundle; Claude Code, Claude.ai, ChatGPT Connectors, and Gemini are all reachable via the HTTP transport (Phase 5 adds the HTTPS + OAuth needed for the cloud LLMs).
- **Runs unattended.** All components run as launchd services and survive reboot without intervention.
- **No cloud dependencies for search/storage.** IMAP sync is the only network hop; embeddings are generated locally via Ollama.

### Non-goals

- Not a mail client. No reading UI, no composing, no marking as read.
- Not bidirectional. The archive is read-only from the user's perspective; changes on the IMAP server flow down, never up.
- Not multi-user. Single account, single machine.
- Not real-time. Sync runs on a timer (every 5 minutes is fine). No IDLE/push.
- No authentication on the MCP server in v1 (localhost-only binding). OAuth + HTTPS land in Phase 5 alongside cross-vendor access.

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
                                                       │ Claude Code, Claude.ai,    │
                                                       │ ChatGPT, Gemini (Phase 5)  │
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

**Transports**: HTTP (default, `127.0.0.1:3333`) for Claude Code and smoke tests; stdio (`--stdio`) packaged as an `.mcpb` bundle for Claude Desktop. Phase 5 adds HTTPS + OAuth on the HTTP transport for ChatGPT, Gemini, and Claude.ai.

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
- **Attachment filenames *are* indexed.** Phase 3 added an `attachments` table (filename, content_type, size, partIndex) plus FTS5 coverage of the filename column, so a query like `"mortgage_statement_2024.pdf"` matches the email it's attached to. Per-format **content** indexing (PDF text extraction, OCR, DOCX) is still out of scope — let downstream tools interpret extracted bytes.
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
│   └── Mailvec.Mcp.Tests/              # project exists; cases TBD
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

**Exit criteria:** Claude can answer "when did Bartlett last quote me for the tree work?" without being told where to look. **Met.**

### Phase 4 — Operationalization
1. launchd plists. Templates exist in `ops/launchd/` for mbsync + indexer + embedder + MCP HTTP server (Ollama installed separately, e.g. via `brew services`). The MCP HTTP plist is the cross-vendor path; Claude Desktop spawns its stdio binary on demand via the MCPB bundle and doesn't need a plist. Wiring them into `~/Library/LaunchAgents` is `install.sh`'s job.
2. `install.sh` that publishes services, rewrites plist `__INSTALL_PREFIX__` placeholders, copies them to `~/Library/LaunchAgents`, loads the agents, and verifies health. Currently a stub.
3. ✓ Health endpoint on the MCP server. `GET /health` returns a structured snapshot (DB path / message counts / last-indexed timestamp, schema vs config embedding model, embedding coverage %, Ollama reachability). HTTP 200 when all green, 503 when degraded so monitors can alert without parsing the body. Local-only by virtue of `Mcp:BindAddress=127.0.0.1`.
4. Log rotation via launchd stdout/stderr paths.
5. ✓ `mailvec status` surfaces message count, embedding coverage, schema/config model match.

### Phase 5 — Cross-vendor MCP access (ChatGPT, Gemini, Claude.ai)

The MCPB bundle is Anthropic-specific (Claude Desktop only). Stdio works for any client that can spawn a child process locally — Claude Code can, but ChatGPT / Gemini / Claude.ai cannot, since they're cloud services. **HTTP is the only portable transport for those clients**, and they all require the same three things on top of what we have today:

1. **Public reachability over HTTPS.** Cloudflare Tunnel (`cloudflared`) or Tailscale Funnel (the *Funnel* variant — exposes a tailnet service to the public internet over HTTPS with a Tailscale-issued cert; ordinary tailnet doesn't reach ChatGPT/Gemini). Either terminates TLS for us, so the MCP server can stay bound to `127.0.0.1` and the tunnel connects locally.
2. **OAuth 2.1 (PKCE).** ChatGPT Connectors, Gemini, and Claude.ai Custom Connectors all expect MCP's standard OAuth flow. The .NET MCP SDK has authentication scaffolding; the open question is the issuer — self-hosted, Cloudflare Access, or Tailscale identity in front are all viable. Each has different implications for who can approve a new Claude/ChatGPT login (only the user vs. anyone with tunnel access).
3. **Per-tool authorization model.** All current tools are read-only against the local DB and Maildir, so the simplest scope is "any authenticated user can call any tool." Revisit if mutating tools are added later.

Practical sequencing: (a) add OAuth scaffolding to the HTTP server; (b) stand up a Cloudflare Tunnel pointed at `localhost:3333`; (c) register the resulting HTTPS URL as a connector in each target client. The MCPB bundle stays as the fast path for Claude Desktop — `Program.cs` already shares Core wiring between the two transports, so nothing about the bundle path changes.

**Out of scope for v1**: federated identity, multi-user support, fine-grained per-tool scopes. This is a single-user system; the auth layer exists to keep random internet traffic out, not to model permissions.

---

## 9. Operational notes

- **Bootstrapping.** Initial sync of a full IMAP archive is slow (hours to a day for large archives). Budget for this and run mbsync manually the first time. The embedder can run against the partial archive as it grows.
- **Model changes.** If the configured embedding model changes, the embedder must refuse to start and print a `mailvec reindex --all` instruction. Never silently mix vector spaces.
- **Database backup.** The SQLite file is the only persistent state produced by this system (the Maildir is reconstructable from IMAP). Should be included in Time Machine / existing backup regimen. WAL checkpointing should be configured sensibly so the `-wal` file doesn't grow unbounded.
- **Ollama lifecycle.** Ollama runs as its own launchd service (installed separately, e.g. via `brew services start ollama`). Our services assume it's reachable at `localhost:11434`. The embedder uses `Microsoft.Extensions.Http.Resilience` for retry/backoff on transient failures.
- **File permissions.** Maildir readable by the user account running the indexer. SQLite file writable by indexer, embedder, and MCP server (all run as the same user on this machine).

---

## 10. Open questions and deferred work

Resolved during the build (kept here as a paper trail):

- **HTML body handling** — *resolved*. AngleSharp-based `HtmlToText` in `Mailvec.Core.Parsing`, with marketing-email noise stripping (hidden preheader text, tracking pixels, footer/unsubscribe boilerplate). MimeKit's `HtmlToText` referenced in the original doc doesn't exist. See CLAUDE.md Phase 1 gotchas.
- **Incremental re-embedding** — *resolved*. `messages.content_hash` (SHA-256 of `MimeMessage.Body` bytes) added in migration 003. `MaildirScanner.TryIngest` clears embeddings via `ChunkRepository.ClearEmbeddingsForMessage` whenever the hash changes. Decoupled from the path-keyed `sync_state.content_hash` which would false-positive on rename/move.
- **Attachment indexing** — *partially resolved*. Filenames are FTS5-indexed via the `attachments` table; `get_attachment` extracts bytes to disk on demand. Per-format **content** indexing (PDF text, OCR) remains out of scope — delegated to downstream tools (Claude Code's `Read`, a filesystem MCP server).

Still open:

- **Thread reconstruction.** Simple `In-Reply-To` / `References` heuristic. Acceptable today; revisit if mismatches with Fastmail's JMAP threading become a usability issue.
- **JMAP-specific metadata.** IMAP flags are available via mbsync; JMAP-only fields (masked email, server-side labels) require a separate JMAP path. Not currently planned.
- **WAL checkpointing strategy.** No periodic checkpoint configured; relies on SQLite's automatic checkpoint at 1000 frames. Worth measuring `-wal` file growth on a long-running install.

---

## 11. Out of scope entirely

- Sending mail
- Modifying server-side state (marking read, moving, deleting)
- Multi-account support
- Calendar, contacts, files — even though Fastmail offers these via CalDAV/CardDAV/WebDAV, this project is mail-only
- Web UI
- Real-time push notifications (mbsync is timer-driven, not IDLE/JMAP push)
