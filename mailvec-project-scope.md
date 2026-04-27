# Fastmail Local Archive with Semantic Search

A local-first email archive and search system for a single Fastmail account, running entirely on a Mac mini (Apple Silicon, macOS). Syncs mail via `mbsync`, indexes it into SQLite with full-text and vector search, and exposes it to Claude via an MCP server.

---

## 1. Goals and non-goals

### Goals

- **Durable local archive.** Every message in the Fastmail account exists as both a Maildir file on disk and a row in a SQLite database on the Mac mini.
- **Two search modes.** Keyword/boolean search via SQLite FTS5, and semantic search via locally-generated embeddings.
- **Claude access.** An MCP server exposing search and retrieval tools that Claude Desktop and Claude.ai can connect to.
- **Runs unattended.** All components run as launchd services and survive reboot without intervention.
- **No cloud dependencies for search/storage.** IMAP sync to Fastmail is the only network hop; embeddings are generated locally via Ollama.

### Non-goals

- Not a mail client. No reading UI, no composing, no marking as read.
- Not bidirectional. The archive is read-only from the user's perspective; changes in Fastmail flow down, never up.
- Not multi-user. Single account, single machine.
- Not real-time. Sync runs on a timer (every 5 minutes is fine). No IDLE/push.
- No authentication on the MCP server in v1. Localhost-only binding.

---

## 2. Architecture

```
┌────────────┐    IMAP     ┌─────────┐     Maildir     ┌──────────────────┐
│  Fastmail  │◀───────────▶│ mbsync  │────────────────▶│  ~/Mail/Fastmail │
└────────────┘             └─────────┘                 └──────────────────┘
                                                                │
                                                                │ FileSystemWatcher
                                                                ▼
                                                       ┌────────────────────┐
                                                       │ Archive.Indexer    │
                                                       │ (BackgroundService)│
                                                       └────────────────────┘
                                                                │
                                                                │ writes rows + FTS
                                                                ▼
                ┌────────────────────┐       reads/writes    ┌─────────────────────┐
                │ Archive.Embedder   │──────────────────────▶│   archive.sqlite    │
                │ (BackgroundService)│                       │   + sqlite-vec      │
                └────────────────────┘                       │   + FTS5            │
                           │                                 └─────────────────────┘
                           │ embeds text                                │
                           ▼                                            │
                  ┌────────────────┐                                    │ reads
                  │    Ollama      │                                    ▼
                  │ localhost:11434│                         ┌──────────────────────┐
                  └────────────────┘                         │    Archive.Mcp       │
                                                             │ (AspNetCore server)  │
                                                             └──────────────────────┘
                                                                        │ MCP over HTTP
                                                                        ▼
                                                               ┌─────────────────┐
                                                               │     Claude      │
                                                               └─────────────────┘
```

Four independent processes, communicating only through the filesystem (Maildir) and the SQLite database. Each can be restarted or replaced without affecting the others.

---

## 3. Components

### 3.1 mbsync (external, not our code)

`isync`/`mbsync` handles the IMAP sync. Installed via Homebrew, configured via `~/.mbsyncrc`, scheduled via launchd. Produces a Maildir tree at a configurable root (e.g. `~/Mail/Fastmail`).

**Our responsibility:** ship an `mbsyncrc.example` template and a launchd plist.

### 3.2 Archive.Core (class library)

Shared types and infrastructure used by all other projects.

- Domain models (`Message`, `Chunk`, `SearchResult`, etc.)
- `ArchiveDbContext` or equivalent data-access layer
- SQLite connection factory with `sqlite-vec` extension loading
- Ollama HTTP client wrapper
- Configuration POCOs (`ArchiveOptions`, `OllamaOptions`, `IndexerOptions`, `McpOptions`)
- Shared logging setup

### 3.3 Archive.Indexer (worker service)

A `BackgroundService` that keeps the SQLite `messages` table in sync with the Maildir on disk.

**Responsibilities:**
- On startup, scan the Maildir root and reconcile against `sync_state`.
- Watch the Maildir with `FileSystemWatcher` (recursive, debounced by ~500ms — mbsync writes to `tmp/` then renames into `new/` or `cur/`).
- Parse each file with MimeKit, extract headers, text body, and HTML body.
- Upsert rows into `messages` keyed by Message-ID, with Maildir path tracked separately so file moves (e.g. `new/` → `cur/`) don't cause duplicates.
- Handle deletions: mark rows as `deleted_at` rather than hard-delete (optional; can also hard-delete).
- Trigger FTS5 updates via triggers on `messages`.

**Does not** call Ollama or produce embeddings. Pure ingest.

### 3.4 Archive.Embedder (worker service)

A `BackgroundService` that generates embeddings for messages lacking them.

**Responsibilities:**
- Periodically (every 30s when idle, immediately when the indexer signals new rows — optional SQLite trigger or file-based event) query for `messages WHERE embedded_at IS NULL`.
- For each message, chunk the body text into overlapping windows sized to fit the embedding model's context.
- Call `POST http://localhost:11434/api/embed` with batches of chunks.
- Write chunks and their vectors into `chunks` and `chunk_embeddings`.
- Update `messages.embedded_at`.
- On startup, verify the `embedding_meta` table matches the configured model; if not, log loudly (schema incompatibility — user must decide to reindex).

### 3.5 Archive.Mcp (AspNetCore server)

An MCP server over HTTP, implemented with `ModelContextProtocol.AspNetCore`. Binds to `localhost:3333` by default.

**Tools exposed:**

| Tool | Purpose |
|---|---|
| `search_emails` | Hybrid FTS + vector search with optional filters (folder, date range, sender) |
| `get_email` | Fetch full message body + headers by ID |
| `get_thread` | Fetch all messages in a thread |
| `list_folders` | List Maildir folders with message counts |
| `find_by_sender` | Exact-match lookup by sender address |
| `recent_emails` | N most recent messages, optionally filtered |

**Hybrid search approach (v1):**
1. Run FTS5 query → top 50 candidates with BM25 scores.
2. Run vector similarity query → top 50 chunks with distances, join back to messages.
3. Reciprocal rank fusion (RRF) across the two lists → final ranked set.
4. Return top N with snippets.

### 3.6 Archive.Cli (console app, optional but recommended)

Admin/operator commands that run against the same database.

- `archive status` — counts, last sync time, embedding coverage %
- `archive reindex` — clear `embedded_at` for matching rows, triggering re-embed
- `archive rebuild-fts` — drop and recreate FTS5 content
- `archive search "query"` — run the same hybrid search the MCP server uses, for debugging

---

## 4. Data model

SQLite file at `~/Library/Application Support/FastmailArchive/archive.sqlite` by default. WAL mode, `foreign_keys=ON`.

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

- **Embedding model is part of the schema.** Mixing vectors from different models silently produces garbage. The `metadata` table records which model was used; the embedder refuses to start if the config disagrees.
- **`body_html` can be large.** Consider splitting into a `message_bodies` table keyed on `message_id` if the main table gets unwieldy. Optimize later if needed.
- **Attachments are not indexed in v1.** Store metadata (filenames, sizes, content-types) but not content. Follow-on work.

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

**Archive.Core**
- `Microsoft.Data.Sqlite.Core` — SQLite provider without bundled native lib (we ship our own for `sqlite-vec` compatibility; OR keep bundled version if it works with runtime extension loading)
- `SQLitePCLRaw.bundle_e_sqlite3` — companion to the above
- `MimeKit` — MIME parsing; same author as MailKit
- `Microsoft.Extensions.Configuration.Binder`
- `Microsoft.Extensions.Options`
- `Microsoft.Extensions.Logging.Abstractions`

**Archive.Indexer**
- References Archive.Core
- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Logging.Console`

**Archive.Embedder**
- References Archive.Core
- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.Http`
- `Microsoft.Extensions.Http.Resilience` — retry/backoff for Ollama calls

**Archive.Mcp**
- References Archive.Core
- `ModelContextProtocol` (1.2.0+) — core MCP SDK
- `ModelContextProtocol.AspNetCore` — HTTP transport
- `Microsoft.AspNetCore.App` (framework reference)

**Archive.Cli**
- References Archive.Core
- `System.CommandLine` (2.0) — argument parsing

**Testing**
- `xunit`, `xunit.runner.visualstudio`
- `FluentAssertions`
- `Microsoft.Extensions.Hosting.Testing` where helpful

---

## 6. Solution layout

```
FastmailArchive/
├── FastmailArchive.sln
├── Directory.Build.props              # shared: LangVersion, Nullable, TreatWarningsAsErrors
├── Directory.Packages.props           # central package management (CPM)
├── README.md
├── src/
│   ├── Archive.Core/
│   │   ├── Archive.Core.csproj
│   │   ├── Models/
│   │   ├── Data/                      # SQLite access, schema migrations
│   │   ├── Ollama/                    # HTTP client for embed API
│   │   ├── Options/                   # configuration POCOs
│   │   └── Search/                    # hybrid search logic used by both Mcp and Cli
│   ├── Archive.Indexer/
│   │   ├── Archive.Indexer.csproj
│   │   ├── Program.cs
│   │   ├── Services/
│   │   │   ├── MaildirWatcher.cs
│   │   │   ├── MaildirScanner.cs
│   │   │   └── MessageIngestService.cs
│   │   └── appsettings.json
│   ├── Archive.Embedder/
│   │   ├── Archive.Embedder.csproj
│   │   ├── Program.cs
│   │   ├── Services/
│   │   │   ├── EmbeddingWorker.cs
│   │   │   └── ChunkingService.cs
│   │   └── appsettings.json
│   ├── Archive.Mcp/
│   │   ├── Archive.Mcp.csproj
│   │   ├── Program.cs
│   │   ├── Tools/                     # one file per [McpServerTool]
│   │   │   ├── SearchEmailsTool.cs
│   │   │   ├── GetEmailTool.cs
│   │   │   ├── GetThreadTool.cs
│   │   │   └── ListFoldersTool.cs
│   │   └── appsettings.json
│   └── Archive.Cli/
│       ├── Archive.Cli.csproj
│       └── Commands/
├── tests/
│   ├── Archive.Core.Tests/
│   ├── Archive.Indexer.Tests/
│   └── Archive.Mcp.Tests/
├── ops/
│   ├── mbsyncrc.example
│   ├── launchd/
│   │   ├── com.dan.mbsync.plist
│   │   ├── com.dan.archive-indexer.plist
│   │   ├── com.dan.archive-embedder.plist
│   │   └── com.dan.archive-mcp.plist
│   └── install.sh                     # copies plists, loads services
└── schema/
    ├── 001_initial.sql
    └── migrations/
```

---

## 7. Configuration

Single `appsettings.json` pattern per service, with `appsettings.Local.json` overrides gitignored. Shared settings are loaded from a central file at `~/Library/Application Support/FastmailArchive/config.json`.

```json
{
  "Archive": {
    "MaildirRoot": "~/Mail/Fastmail",
    "DatabasePath": "~/Library/Application Support/FastmailArchive/archive.sqlite",
    "SqliteVecExtensionPath": "./runtimes/osx-arm64/native/vec0.dylib"
  },
  "Ollama": {
    "BaseUrl": "http://localhost:11434",
    "EmbeddingModel": "mxbai-embed-large",
    "EmbeddingDimensions": 1024,
    "KeepAlive": "30m",
    "MaxBatchSize": 16,
    "RequestTimeoutSeconds": 60
  },
  "Indexer": {
    "ScanIntervalSeconds": 300,
    "DebounceMilliseconds": 500,
    "MaxHtmlBodyBytes": 1048576
  },
  "Embedder": {
    "PollIntervalSeconds": 30,
    "ChunkSizeTokens": 200,
    "ChunkOverlapTokens": 32,
    "MaxConcurrentRequests": 2
  },
  "Mcp": {
    "BindAddress": "127.0.0.1",
    "Port": 3333,
    "SearchDefaultLimit": 20,
    "SearchMaxLimit": 100
  }
}
```

---

## 8. Phased build plan

Recommended order for Claude Code to scaffold and for incremental development.

### Phase 0 — Repo scaffold
Solution, projects, CPM, Directory.Build.props, README, gitignore, CI stub.

### Phase 1 — Ingest pipeline (value: searchable archive)
1. Schema migration runner in `Archive.Core`.
2. MimeKit-based parser with tests against fixture `.eml` files.
3. `MaildirScanner` — one-shot full scan that populates `messages` + `sync_state`.
4. `MaildirWatcher` — incremental updates via FileSystemWatcher.
5. FTS5 triggers and a working `Archive.Cli search "query"` command using BM25.

**Exit criteria:** can point at a Maildir and do useful keyword search.

### Phase 2 — Semantic layer
1. Ollama HTTP client + integration test against a running Ollama.
2. `ChunkingService` — token-aware splitter (use a simple heuristic like ~4 chars/token for v1; upgrade to a real tokenizer later).
3. `EmbeddingWorker` that processes unembedded rows in batches.
4. Hybrid search in `Archive.Core/Search` using RRF.
5. `Archive.Cli search --semantic` command for comparison.

**Exit criteria:** semantic queries return relevant results the FTS layer would have missed.

### Phase 3 — MCP exposure
1. Bare-bones MCP server with a single `search_emails` tool.
2. Register with Claude Desktop via its MCP config, verify end-to-end.
3. Add remaining tools (`get_email`, `get_thread`, `list_folders`, `find_by_sender`, `recent_emails`).
4. Add HTTP transport and confirm Claude.ai can connect over `http://localhost:3333/mcp`.

**Exit criteria:** Claude can answer "when did Bartlett last quote me for the tree work?" without being told where to look.

### Phase 4 — Operationalization
1. launchd plists for all three services + Ollama.
2. `install.sh` that sets up config, loads services, verifies health.
3. Health endpoint on the MCP server.
4. Log rotation via launchd stdout/stderr paths.
5. Simple status command for coverage metrics.

---

## 9. Operational notes

- **Bootstrapping.** Initial sync of a full Fastmail archive via IMAP is slow (hours to a day for large archives). Budget for this and run mbsync manually the first time. The embedder can run against the partial archive as it grows.
- **Model changes.** If the configured embedding model changes, the embedder must refuse to start and print a `archive reindex --all` instruction. Never silently mix vector spaces.
- **Database backup.** The SQLite file is the only persistent state produced by this system (the Maildir is reconstructable from Fastmail). Should be included in Time Machine / existing backup regimen. WAL checkpointing should be configured sensibly so the `-wal` file doesn't grow unbounded.
- **Ollama lifecycle.** Ollama runs as its own launchd service (installed separately). Our services assume it's reachable at `localhost:11434` and fail gracefully with retries if it's temporarily down.
- **File permissions.** Maildir readable by the user account running the indexer. SQLite file writable by indexer, embedder, and MCP server (all run as the same user on this machine).

---

## 10. Open questions and deferred work

- **HTML body handling.** Extracting clean text from marketing HTML is harder than it looks. v1 uses MimeKit's `HtmlToText` helper; revisit if quality is poor.
- **Attachment indexing.** PDFs, docs, and images are entirely out of scope for v1. A future `Archive.Attachments` project could add this.
- **Thread reconstruction.** v1 uses simple `In-Reply-To` / `References` heuristics. Fastmail threads may not match perfectly; acceptable for now.
- **Access from outside the local network.** Tailscale is the intended path (tailnet IP + MCP server bind to `0.0.0.0`), not public internet exposure. Defer until v1 is working.
- **Incremental re-embedding.** If a message body is edited upstream, we don't currently detect it. The `content_hash` in `sync_state` supports this but the embedder doesn't use it yet.
- **Fastmail-specific metadata.** IMAP flags are available via mbsync; JMAP-specific fields (masked email, labels) are not. Probably acceptable.

---

## 11. Out of scope entirely

- Sending mail
- Modifying Fastmail state (marking read, moving, deleting)
- Multi-account support
- Calendar, contacts, files — Fastmail offers these via CalDAV/CardDAV/WebDAV but this project is mail-only
- Web UI
- Real-time push notifications
