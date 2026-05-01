# Glossary

Quick reference for terms, libraries, and acronyms used throughout the Mailvec codebase. Aimed at new contributors — entries link out to upstream docs where useful, and note where the term shows up in this repo.

## Storage and indexing

**SQLite** — embedded SQL database, used as the single store for messages, chunks, and vectors. One file: `archive.sqlite` (path configured via `Archive:DatabasePath`). Accessed through `Microsoft.Data.Sqlite`.

**WAL (Write-Ahead Logging)** — SQLite journaling mode that lets readers and a single writer work concurrently without blocking each other. Enabled per-connection in [`ConnectionFactory.Open`](src/Mailvec.Core/Data/ConnectionFactory.cs) (not via the schema, see Phase 4 gotchas in [CLAUDE.md](CLAUDE.md)). The on-disk artefact is a `.wal` sidecar file alongside the main DB; `mailvec checkpoint` flushes and truncates it.

**FTS5** — SQLite's [built-in full-text search extension](https://www.sqlite.org/fts5.html). Backs keyword search via the `messages_fts` virtual table in [`schema/001_initial.sql`](schema/001_initial.sql). Mailvec uses the `porter unicode61` tokenizer (Porter stemming + Unicode-aware word splitting). Kept in lockstep with the `messages` table by `messages_ai`/`messages_ad`/`messages_au` triggers — never insert into `messages_fts` directly.

**BM25** — the relevance-ranking function FTS5 uses to score keyword matches. Higher term frequency boosts a doc, common terms across the corpus get downweighted. SQLite returns it via the `bm25()` function; we use it as the keyword-leg ranking in hybrid search.

**sqlite-vec / vec0** — [SQLite extension](https://github.com/asg017/sqlite-vec) by Alex Garcia that adds vector search via `vec0` virtual tables. Loaded at runtime from `runtimes/<rid>/native/vec0.dylib` (fetched once by [`ops/fetch-sqlite-vec.sh`](ops/fetch-sqlite-vec.sh)). Backs the `chunk_embeddings` table.

**KNN (k-nearest neighbours)** — given a query vector, find the `k` rows with smallest distance. In sqlite-vec syntax: `SELECT … FROM chunk_embeddings WHERE embedding MATCH $vec AND k = $k ORDER BY distance` (see [`VectorSearchService.cs:61`](src/Mailvec.Core/Search/VectorSearchService.cs:61)). vec0 currently does brute-force KNN — fine at our scale.

**Embedding / vector** — a fixed-length array of floats representing the "meaning" of a piece of text. Similar texts produce vectors that are close in the vector space, so nearest-neighbour lookup approximates semantic search. Mailvec stores `FLOAT[1024]` vectors produced by the `mxbai-embed-large` model.

**Cosine distance** — the default distance function vec0 uses for `FLOAT[N]` columns. Returns 0 for identical-direction vectors and 2 for opposite. Smaller is more similar.

**Chunking** — splitting a long message body into smaller overlapping pieces before embedding, because embedding models have a fixed context window. Lives in [`ChunkingService`](src/Mailvec.Core/Embedding/ChunkingService.cs); default ~200 tokens per chunk.

**Hybrid search** — combining keyword (BM25) and vector (KNN) result lists into one ranked list. Mailvec uses RRF (below). Implementation: [`HybridSearchService`](src/Mailvec.Core/Search/HybridSearchService.cs).

**RRF (Reciprocal Rank Fusion)** — a simple, well-studied way to merge ranked lists without needing to normalise the underlying scores. For each result list, every doc contributes `1 / (k + rank)`; sum across lists. Standard `k=60` from the literature. Robust because it ignores absolute scores (which differ wildly between BM25 and cosine).

## Email pipeline

**IMAP** — the standard remote-mailbox protocol. Mailvec is read-only against IMAP — `mbsync` pulls messages, nothing pushes back.

**JMAP** — modern JSON-based replacement for IMAP, native to Fastmail. Not used by Mailvec today; mentioned in Phase 5 design notes for resolving direct webmail deep-links to Fastmail's UI.

**Maildir** — disk format where each email is one file under `cur/`, `new/`, or `tmp/` directories per folder. Atomic creation, no locking, easy to back up. `mbsync` writes Maildir; the indexer scans it.

**mbsync (isync)** — the tool that pulls IMAP into Maildir on disk. Runs as a launchd agent on a 5-minute timer. Config template at [`ops/mbsyncrc.example`](ops/mbsyncrc.example).

**MimeKit** — .NET MIME parser ([jstedfast/MimeKit](https://github.com/jstedfast/MimeKit)). Reads the raw RFC 5322 message bytes and gives us headers, threading info, body parts, and attachments.

**AngleSharp** — .NET HTML parser with a real DOM ([anglesharp.github.io](https://anglesharp.github.io/)). Used by [`HtmlToText`](src/Mailvec.Core/Parsing/HtmlToText.cs) to convert HTML email bodies to plain text while stripping marketing-email noise (preheaders, tracking pixels, unsubscribe footers). The design doc originally suggested MimeKit's `HtmlToText`, which doesn't exist — see Phase 1 gotchas.

**Message-ID / thread_id** — RFC 5322 message identifier (e.g. `<abc123@example.com>`), unique per email. `thread_id` is derived from `In-Reply-To` / `References` chains by MimeKit. Both are stable across Maildir renames, which is why we key dedup off Message-ID and not file path.

## LLM and MCP

**Ollama** — [local LLM runtime](https://ollama.com/) that exposes an HTTP API on `localhost:11434`. Mailvec uses only the `/api/embed` endpoint to turn email text into vectors. Run as a launchd service via `brew services start ollama`.

**mxbai-embed-large** — the embedding model Mailvec uses by default ([Mixedbread.ai](https://www.mixedbread.com/)). Outputs 1024-dim vectors with a 512-token context window. Schema-coupled — switching models requires a full reindex.

**MCP (Model Context Protocol)** — [Anthropic's open protocol](https://modelcontextprotocol.io/) that lets Claude call external tools. Mailvec exposes `search_emails`, `get_email`, `get_thread`, `list_folders`, and `get_attachment` over MCP. Implemented with the `ModelContextProtocol` and `ModelContextProtocol.AspNetCore` NuGet packages.

**Streamable HTTP transport** — the HTTP-based MCP transport. Server binds to `127.0.0.1:3333`, clients establish a session via the `initialize` handshake and pass `Mcp-Session-Id` on subsequent calls. Used by Claude Code and (eventually) cross-vendor clients via Tailscale/Cloudflare Tunnel.

**stdio transport** — MCP over stdin/stdout, used by Claude Desktop because its connector config only supports `command`+`args`. All logging must go to stderr in this mode — a single byte on stdout corrupts the JSON-RPC framing. See `Mailvec.Mcp/Program.cs::RunStdio`.

**MCPB (MCP Bundle)** — Claude Desktop's `.mcpb` extension format: a zip containing `manifest.json`, the published binary, and any native deps. Built by [`ops/build-mcpb.sh`](ops/build-mcpb.sh). User installs by double-clicking the file. See the "MCPB bundle for Claude Desktop" section in [CLAUDE.md](CLAUDE.md) for gotchas.

**OAuth 2.1 / PKCE** — auth flow MCP requires for cross-vendor clients (ChatGPT, Gemini, Claude.ai web). Not yet implemented; on the Phase 5 roadmap.

## .NET and tooling

**.NET 10** — current target framework, declared once in `Directory.Build.props` as `<TargetFramework>net10.0</TargetFramework>`.

**TFM (target framework moniker)** — the string identifying which .NET runtime/version a project compiles against (e.g. `net10.0`).

**RID (runtime identifier)** — string identifying a target OS+CPU combo for native deps and self-contained publishes (e.g. `osx-arm64`, `osx-x64`). The `vec0.dylib` lives under `runtimes/<rid>/native/`.

**Central Package Management (CPM)** — NuGet feature where every package version is declared once in `Directory.Packages.props` and csproj files reference packages without `Version=`. Adding a new dependency means editing both files.

**Generic Host / `BackgroundService`** — .NET hosting abstraction for long-running services. Both the indexer and embedder are `BackgroundService` workers. Provides DI, configuration, logging, and graceful shutdown out of the box.

**Serilog** — structured logging library with rolling-file output. Configured uniformly across services via `SerilogSetup.Configure`. Logs land in `~/Library/Logs/Mailvec/`.

**xUnit + Shouldly** — the test framework (`xunit`) and assertion library (`Shouldly`, e.g. `result.ShouldBe(expected)`) used across the test projects.

## macOS-specific

**launchd** — macOS's init system. Background services are described by plist files; user-level agents live in `~/Library/LaunchAgents/`. Used to keep mbsync, the indexer, and the embedder running. Templates in [`ops/launchd/`](ops/launchd/).

**TCC (Transparency, Consent, and Control)** — macOS's permission system that gates access to Documents, Downloads, Photos, etc. Claude Desktop's spawned MCP servers can't read `~/Documents` even with Full Disk Access — that's why the published MCP binary lives at `~/.local/share/mailvec/`, not in the repo.

**Tailscale / Cloudflare Tunnel** — services for exposing a local HTTP endpoint to the internet over an encrypted tunnel. Required for Phase 5 cross-vendor MCP access (ChatGPT/Gemini/Claude.ai can't reach `127.0.0.1`).

## Security / correctness

**TOCTOU (time-of-check to time-of-use)** — class of race-condition bugs where a check (e.g. "this path is safe") and the use (e.g. "open this path") happen at different times, letting an attacker swap the target in between. Relevant to attachment extraction: see `AttachmentExtractor.ResolveSafeOutputPath`, which refuses to follow symlinks at the destination.
