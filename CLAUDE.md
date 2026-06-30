# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository. Stays focused on invariants whose violation is **silent** (silent data corruption, silent contract break, silent quality regression). Loud failures (compile errors, missing files) don't earn a spot.

## Source of truth

The schema is `schema/001_initial.sql` + migrations under `schema/migrations/` — silent-corruption-prone invariants are captured in this file under "Schema & data invariants". `CHANGELOG.md` is the phase-by-phase build history. Design rationale that didn't make it into code lives in `docs/security.md` (threat model) and `docs/future-ideas.md` (deferred work + still-open questions).

Operator/release/contributor docs (read on demand):
- `ops/UPGRADING.md` — bumping NuGet packages, the .NET SDK, sqlite-vec, SQLite, Ollama floor.
- `ops/mcpb-release.md` — building and shipping the MCPB bundle.
- `docs/contributing/tray.md` — SwiftUI / macOS gotchas; read before editing `src/Mailvec.Tray/`.
- `docs/contributing/attachments.md` — `get_attachment` implementation quirks.
- `docs/contributing/search-performance.md` — diagnosing search latency (tray vs CLI, warm vs cold vector cache, the `/tray/warm` pre-warm); read before "optimising" search.
- `docs/contributing/mcpb.md` — `manifest.json` quirks + tool-shape rules that break the Claude Desktop install.
- `docs/contributing/embedding-experiments.md` — A/B-testing embedding models / chunk sizes against a parallel DB copy with `mailvec switch-model` + env-var overrides; read before changing the embedding model.
- `src/Mailvec.Tray/project.yml` — XcodeGen spec for the menu-bar tray app; regenerate the `.xcodeproj` with `xcodegen generate` whenever files are added/renamed.

## Common commands

```sh
./ops/fetch-sqlite-vec.sh    # one-time: downloads vec0.dylib into runtimes/<rid>/native/
dotnet build                 # builds via Mailvec.slnx (the .NET 10 XML solution format)
dotnet test                  # runs all xUnit projects
dotnet test tests/Mailvec.Core.Tests --filter "FullyQualifiedName~MaildirScanner"   # single test class
ops/coverage.sh              # runs tests with coverage; HTML at coverage/index.html, Markdown at coverage/SummaryGithub.md
dotnet run --project src/Mailvec.Indexer
dotnet run --project src/Mailvec.Mcp
dotnet run --project src/Mailvec.Cli -- <args>
ops/redeploy.sh [indexer|embedder|mcp|cli ...]   # republish + kickstart launchd agents after a code change
ops/stop.sh                                      # bootout the launchd agents without uninstalling
ops/install-all.sh [--no-tray|--no-fetch]        # single-command bootstrap for a new machine
ops/install-tray.sh                              # build + copy to /Applications + launch (calls build-tray.sh)
ops/build-tray.sh                                # XcodeGen → xcodebuild archive → build/Mailvec.Tray.app
```

`Mailvec.slnx` (not `.sln`) is the solution file — .NET 10 emits the new XML format by default.

`dotnet run --project src/Mailvec.<svc>` runs the working-tree code under your terminal — useful for one-off debugging, but **the launchd agents installed by `ops/install.sh` are separate processes** running the published binaries under `~/.local/share/mailvec/<svc>/`. After editing service code, run `ops/redeploy.sh` to push the new binaries and restart the agents — otherwise the live services keep running the old code while `dotnet build` looks like it succeeded. Use `ops/install.sh` only when plist templates change or a config knob needs updating.

## Build conventions

- **Central Package Management is on.** All NuGet versions live in `Directory.Packages.props`. csproj files use `<PackageReference Include="..." />` *without* a `Version=` attribute. Adding a new dependency means editing both files.
- **`TreatWarningsAsErrors=true`** in `Directory.Build.props` — a build warning fails the build.
- **`net10.0`, nullable enabled, implicit usings on** are inherited from `Directory.Build.props`; do not redeclare them in individual csprojs.

## Architecture

Four .NET services + a SwiftUI tray app, communicating only through the filesystem (Maildir) and the SQLite database. The four .NET processes are independent — each can be restarted or replaced without affecting the others; keep this isolation when extending. The tray is a thin GUI client that talks to the MCP server's plain-REST `/tray/*` surface.

```
Fastmail (or any IMAP)
    │ IMAP (Pull-only, read-only)
    ▼
mbsync ──► ~/Mail/<account>/  (Maildir)
                │ FileSystemWatcher
                ▼
        Mailvec.Indexer  ──┐
                           │ writes messages + FTS5
                           ▼
                    archive.sqlite  ◄── reads ── Mailvec.Mcp ──► Claude (over MCP/HTTP)
                           ▲                       │
                           │ writes chunks         │ /tray/* (REST)
                           │ + vectors             ▼
        Mailvec.Embedder  ──► Ollama         Mailvec.Tray (SwiftUI menu-bar app)
                              (localhost:11434)
```

Project map:

- **Mailvec.Core** — shared library. Domain models, SQLite access (with `sqlite-vec` extension loading), Ollama HTTP client, hybrid (FTS+vector RRF) search logic, `Tray/` services (status / system / search / launchd inspector / sampling ring buffer), and `Options/` POCOs (`ArchiveOptions`, `IngestOptions`, `OllamaOptions`, `IndexerOptions`, `EmbedderOptions`, `McpOptions`). All four .NET executables reference Core.
- **Mailvec.Indexer** — `BackgroundService` worker. Scans Maildir, parses with MimeKit, upserts `messages`. Does *not* call Ollama.
- **Mailvec.Embedder** — `BackgroundService` worker. Polls for `messages WHERE embedded_at IS NULL`, chunks bodies, calls Ollama, writes `chunks` + `chunk_embeddings`.
- **Mailvec.Mcp** — AspNetCore app exposing MCP tools over HTTP on `127.0.0.1:3333`. Read-only against the database (except `get_attachment` and `get_attachment_page_image`, which read `.eml` files out of the Maildir). Also serves plain-REST `/tray/*` endpoints for the tray app and `/health` for monitors.
- **Mailvec.Cli** — admin commands (status, doctor, search, get, reindex, switch-model, rebuild-fts, rebuild-bodies, purge-deleted, checkpoint, audit-embeddings, extract-attachments, plus the `eval` / `eval-add` / `eval-import` family for retrieval-quality benchmarking) hitting the same DB.
- **Mailvec.Tray** — SwiftUI menu-bar app (macOS 14+). A `MenuBarExtra` with a dashboard, inline search popover, and Settings window. Polls `/tray/status` every 5s; never touches the SQLite database directly. See [`docs/contributing/tray.md`](docs/contributing/tray.md).

## Configuration

Each runnable service has its own `appsettings.json` containing only the sections it needs. Configuration POCOs live in `Mailvec.Core/Options/` and are bound in each service's `Program.cs`. Local overrides go in `appsettings.Local.json` (gitignored).

**Single source of truth for user-specific settings** lives at `~/Library/Application Support/Mailvec/appsettings.Local.json`, written by `ops/install.sh` and read by every Mailvec binary via `SharedConfig.AddMailvecSharedConfig()` ([SharedConfig.cs](src/Mailvec.Core/Options/SharedConfig.cs)). Holds the four settings that used to be duplicated across the launchd plist's `EnvironmentVariables` and the MCPB manifest's `user_config`: `Archive:DatabasePath`, `Ingest:MaildirRoot`, `Ollama:BaseUrl`, `Fastmail:AccountId`. **Precedence**: per-binary appsettings → shared file → env vars (highest). Env-var wins means the MCPB bundle's `Mcp__LogToolCalls` (passed in by Claude Desktop's user_config UI) still overrides, and a developer can one-off `Ingest__MaildirRoot=...` in their shell for testing without editing the file.

`ArchiveOptions` (`DatabasePath`, `SqliteVecExtensionPath`) is bound by all four executables (consumed by `ConnectionFactory`). `IngestOptions` (`MaildirRoot`) is bound by the Indexer (scans the Maildir), the CLI (prints the path in `mailvec status`), the MCP server (`get_attachment` reads attachment bytes out of Maildir source files), and — as of the scanned-PDF OCR feature — the **Embedder** (its OCR pass reads `.eml` bytes via `MaildirAttachmentReader` to render scanned PDFs). The Embedder *used* to be the only executable that never touched the filesystem; OCR changed that. The bytes-decode logic is shared: `MaildirAttachmentReader` owns the containment-guarded read, and `AttachmentExtractor` (MCP) delegates to it.

Both indexer and CLI read environment variables with no prefix, so `Ingest__MaildirRoot=/path` works for either. The CLI looks for `appsettings.json` in `AppContext.BaseDirectory`, not the current working directory.

## sqlite-vec

Fetched as a prebuilt `vec0.dylib` by `ops/fetch-sqlite-vec.sh` and loaded at runtime via `Microsoft.Data.Sqlite`'s extension-loading API. Path comes from `Archive:SqliteVecExtensionPath`. Fetch script supports `osx-arm64` and `osx-x64`; add other RIDs there if needed. **Don't switch to the NuGet wrapper** — see `ops/UPGRADING.md`.

## Schema & data invariants

`schema/001_initial.sql` is the full schema (v6 baseline). These invariants are enforced at runtime and silent-corruption-prone if bypassed.

- **`messages.attachment_text` is a denormalized space-join of `attachments.extracted_text`** (status='done'), built by `MessageRepository.BuildAttachmentText` and written in the same transaction as `attachment_names`. It feeds the 6th `messages_fts` column so keyword search hits attachment bodies; if a write path updates `attachments.extracted_text` without rebuilding `messages.attachment_text`, FTS silently drifts and dual-signal RRF fusion stops boosting documents that should match both the BM25 and vector legs.
- **The embedding model is part of the schema.** `chunk_embeddings` declares a fixed dimension — substituted from `Ollama:EmbeddingModel`/`EmbeddingDimensions` on fresh-DB creation by `SchemaMigrator.SubstituteEmbeddingConfig` (each substitution token must appear exactly once in `001_initial.sql`; the migrator throws otherwise). `OllamaClient` validates returned vector lengths against `Ollama:EmbeddingDimensions` and L2-normalizes any unnormalized vector (vec0 KNN is L2; unit norm is what makes it rank like cosine); `EmbeddingWorker` refuses to start if `metadata.embedding_model` disagrees with config. **`mailvec switch-model` is the only sanctioned way to change an existing DB's model** — it rebuilds the vec0 table at the new dimension, clears chunks, re-queues every message, and updates metadata in one transaction (see `docs/contributing/embedding-experiments.md`). **Never bypass these checks** — mixing vector spaces silently corrupts similarity scores in ways that look plausible. The embedding seam is `IEmbeddingClient` (`Mailvec.Core/Embedding/`); `OllamaClient` is the sole implementation, and all consumers + DI registrations go through the interface.
- **FTS5 sync is trigger-driven.** `messages_ai`/`messages_ad`/`messages_au` keep `messages_fts` in lockstep with `messages`. Don't bypass the triggers with raw inserts into `messages_fts`.
- **`vec0` must load before the schema applies**, because `001_initial.sql` declares `chunk_embeddings` as a `vec0(...)` virtual table. `ConnectionFactory` loads the extension on every `Open()`. The dylib is copied into each project's `bin/.../runtimes/<rid>/native/` by `Directory.Build.props` (with an `Exists` guard).
- **`chunk_embeddings` has no foreign key to `chunks`** because vec0 virtual tables don't support FKs. `ChunkRepository.DeleteChunks` deletes from both tables explicitly inside one transaction. Don't `DELETE FROM chunks` directly without also cleaning `chunk_embeddings`.
- **Vector serialization to sqlite-vec.** `FLOAT[N]` columns are stored as packed little-endian float32 bytes. `VectorBlob.Serialize/Deserialize` in `ChunkRepository.cs` is the only place that does this conversion — keep all sqlite-vec writes/reads going through it.
- **`PRAGMA journal_mode = WAL` must be applied via `ExecuteScalar`, not `ExecuteNonQuery`.** Microsoft.Data.Sqlite silently no-ops result-returning pragmas under `ExecuteNonQuery`, so the WAL pragma in `001_initial.sql` was *not actually setting WAL mode* — DBs were running in plain rollback-journal `delete` mode. Confirmed by reading header bytes 18-19 (`01 01` for journal, `02 02` for WAL). Fix lives in `ConnectionFactory.Open()`: a per-connection `PRAGMA journal_mode = WAL;` via `ExecuteScalar`, idempotent and self-healing for any DB created before the fix landed. Don't move this back into the migration script.
- **`Microsoft.Data.Sqlite.ExecuteNonQuery` silently stops at the first `CREATE TRIGGER ... BEGIN ... END;`.** The internal trigger semicolons confuse its statement iterator. `SqlScriptSplitter` tokenises scripts itself (BEGIN/END depth-tracking) and executes one statement at a time. Don't go back to `ExecuteNonQuery`-on-the-whole-script.

Attachment text:

- **`attachments.extracted_text` / `extracted_at` / `extraction_status`** hold the recovered plain text. `chunks.source` ('body' | 'attachment') + `chunks.attachment_id` let search results trace back to a specific document.
- **`extraction_status` is a stable enum** declared as constants on `AttachmentTextExtractor`: `done`, `ocr`, `no_text`, `unsupported`, `oversize`, `encrypted`, `failed`. Persist these values verbatim — they're surfaced through `get_email`'s `AttachmentInfo.ExtractionStatus` so Claude can tell the user "I couldn't read this PDF because it was encrypted". **`ocr`** means a scanned PDF stuck at `no_text` was later recovered by the **embedder's** vision-model OCR pass (written by `MessageRepository.SaveOcrText`, not the indexer); it's treated as searchable like `done` (both the FTS `attachment_text` rebuild and `get_email`'s `IndexedForSearch` include it). The embedder OCRs anything at `no_text` that's a PDF, so a re-index that resets a doc to `no_text` self-heals on the next OCR pass. See `docs/contributing/attachment-ocr.md`.
- **Extraction libraries are pure-managed.** PDF via `PdfPig` (Apache 2.0); DOCX via `DocumentFormat.OpenXml` (MIT). No native deps, no shell-out, no OCR. Scanned PDFs come back as `extraction_status='no_text'` and are intentionally not retried. **This applies to the indexer's text-extraction path only** — and "no OCR" is no longer absolute: the **embedder** OCRs scanned PDFs out of band (see "scanned-PDF OCR" below). PDF page rasterisation lives in **`Mailvec.Pdf`** (`PdfRenderer`, via `PDFtoImage` = PDFium + SkiaSharp, **native**) — the only native dep in the read path besides `sqlite-vec`, referenced **only** by `Mailvec.Mcp` (`get_attachment_page_image`) and `Mailvec.Embedder` (the OCR pass), keeping it out of Core/Indexer/Cli. Platform-gated with `[SupportedOSPlatform]` to macOS/Linux/Windows; native assets for `osx-arm64`/`osx-x64`/`linux-x64`/`linux-arm64` come transitively through NuGet (no fetch script, unlike `vec0.dylib`) and bloat the self-contained MCPB bundle. The renderer caps the long edge at 1536px (downscale-only) and encodes JPEG q85 on a white background — a fixed-DPI PNG let a large-MediaBox scan balloon to ~20MB, pointless since Claude downsamples to ~1568px anyway.

- **Scanned-PDF OCR (the embedder).** Image-only PDFs land at `extraction_status='no_text'` from the indexer (pure-managed PdfPig can't read them). The embedder's OCR pass (`AttachmentOcrService`, behind `Embedder:OcrEnabled`, default on) then renders each page (`PdfRenderer`) and transcribes it with a local Ollama **vision** model (`Ollama:VisionModel`, default `qwen2.5vl:7b`) via `IVisionClient`/`OllamaVisionClient`, writes the text back as `status='ocr'`, and re-queues the message so it's embedded + FTS-indexed. Runs *before* the embed pass each cycle. The vision model is **not** pinned (loads on demand, `Ollama:VisionKeepAlive` empty → Ollama's default), so it and the embedding model never need to coexist in RAM. Degrades gracefully: if the model isn't pulled it logs + skips (leaves `no_text`); `mailvec doctor` warns. Full design + rationale in `docs/contributing/attachment-ocr.md`.

## Indexing & change detection

- **Content-change detection lives in `MessageRepository.Upsert`.** Each parsed message carries a SHA-256 of its body section (computed by `MessageBodyHasher.Hash` over `MimeMessage.Body.WriteTo` — explicitly excludes the top-level header block so post-delivery rewrites like DKIM-Verified don't trigger spurious re-embeds). On upsert we compare against stored `messages.content_hash` in the same transaction and return `UpsertOutcome { Id, ContentChanged }`. The scanner ([`MaildirScanner.TryIngest`](src/Mailvec.Indexer/Services/MaildirScanner.cs)) calls `ChunkRepository.ClearEmbeddingsForMessage(id)` when `ContentChanged == true`. Keying off Message-ID (not Maildir path) avoids false positives from renames; hashing `MimeMessage.Body` bytes (not `parsed.BodyText`) decouples from HTML-converter changes — those are handled separately by `mailvec rebuild-bodies --reembed`. Legacy NULL hashes are treated as "not changed" so the migration doesn't mass-clear.
- **Rename detection.** When mbsync renames `new/foo` → `cur/foo:2,S` between scans, the same Message-ID appears at a fresh path while the old `sync_state` row still references the old path. The scanner uses `SyncStateRepository.FreshMessageIds(since)` to exclude renamed messages from the soft-delete pass.
- **The scanner has an mtime fast-path.** At the top of [`MaildirScanner.TryIngest`](src/Mailvec.Indexer/Services/MaildirScanner.cs): if `sync_state` already remembers this exact path AND `File.GetLastWriteTimeUtc(file) <= prior.LastSeenAt.UtcDateTime`, the scan refreshes `last_seen_at` and returns without re-parsing. Without this guard, every 5-minute periodic rescan would re-extract every PDF in the archive — the dominant cost on a real corpus. Mbsync flag rewrites bump mtime, so the optimization can't miss real content changes; the worst case is one wasted re-extraction per flag flip. **Don't bypass this** unless you've added a cheaper way to detect "file actually changed". Test: [`MaildirScannerTests.Rescan_skips_unchanged_files_via_mtime_fast_path`](tests/Mailvec.Indexer.Tests/MaildirScannerTests.cs).
- **Attachment rows are NOT replaced on no-op rescans.** `MessageRepository.Upsert` only calls `ReplaceAttachments` when `IsNewInsert || ContentChanged` (surfaced via `UpsertOutcome.AttachmentsReset`). Without this guard, the `extracted_text` column would be wiped and rebuilt on every scan even when the parent body hadn't changed. The body's `content_hash` covers the multipart structure, so any real attachment delta forces a hash change → reset → re-extraction.
- **Extraction runs inside the indexer's parser.** `MessageParser` takes an optional `AttachmentTextExtractor` ctor dep; the production DI graph in `Mailvec.Indexer/Program.cs` wires it in, while the parameterless ctor (used by tests / the MCP `get_attachment` flow) skips extraction and produces metadata-only attachments. Keep this split — extraction is the only Maildir-touching step that the embedder and MCP server should never invoke.
- **`AttachmentMaxBytes` (default 25MB) is the per-attachment extraction cap.** Anything larger gets `extraction_status='oversize'` and is skipped without decoding. Don't drop this much — a 200MB image-heavy PDF can exhaust RAM during PdfPig parse. Caller declared sizes are checked first (cheap pre-filter), then bytes are re-checked after MIME decode to catch under-reported sizes.

## HTML parsing

- **HTML body parsing uses AngleSharp, not MimeKit.** The design doc's reference to MimeKit's `HtmlToText` was wrong (MimeKit doesn't ship that helper). We use AngleSharp's DOM via `Mailvec.Core.Parsing.HtmlToText`, which strips marketing-email noise (hidden preheader text, tracking pixels, image-only and unsubscribe/preferences links, `<address>` and `<footer>` blocks). Output runs through `TextNormalize` and `BoilerplateFilter`. **Tuning detail to remember:** `font-size:0`, `max-height:0`, and `max-width:0` on outer `<td>` wrappers are *layout hacks* (defeating inter-cell whitespace) where the real text lives in inner elements that override the property — never treat these as hide signals or you'll nuke whole branches of legitimate content. Migration when the converter changes: `mailvec rebuild-bodies --reembed` re-derives `body_text` from stored `body_html` and clears `embedded_at`.

## Embedding & chunking

- **Subject is prepended to body before embedding** in `EmbeddingWorker.BuildEmbeddingText`. Newsletters often have a meaningful subject and thin body; replies the other way.
- **Empty-body messages get a stamped `embedded_at` with zero chunks** so the worker doesn't loop on them forever. `ChunkRepository.ReplaceChunksForMessage` accepts empty lists for this reason.
- **Embedder stitches body + attachment chunks into a single per-message chunk stream.** `EmbeddingWorker.BuildChunksForMessage` emits body chunks first (so `chunk_index = 0` is always body when present), then per-attachment chunks tagged `source='attachment'` + `attachment_id=<id>`. `chunk_index` is renumbered sequentially across both sources to satisfy `UNIQUE(message_id, chunk_index)`. Attachment chunks include the filename as a prefix so a query matching the document name still ranks even if the filename token isn't repeated in the body.
- **Chunk size + two layers of overflow handling.** Default `ChunkSizeTokens=200` (chars/4 = 800 char ceiling). The 4-chars/token heuristic is unreliable on real email — punctuation, embedded URLs, base64, marketing-email HTML residue, and non-ASCII can push BPE token counts to 1-2 chars/token, exceeding `mxbai-embed-large`'s 512-token context. Two safety nets:
  1. **Server-side: Ollama's `truncate: true` flag** works for batched `/api/embed` as of Ollama **0.21.2** (this is the floor). The server logs `llm embedding error: the input length exceeds the context length` (INFO-level, not an error response), silently truncates, and returns HTTP 200. Verified empirically by `mailvec audit-embeddings`.
  2. **Client-side fallback in `OllamaClient.EmbedAsync` for HTTP 400**: oversize batches get split in half and recursed; a singleton that still doesn't fit is truncated by 50% repeatedly until it succeeds (down to a 64-char floor). Don't disable this without replacing it with a real tokenizer-based pre-check — it's the safety net if a future Ollama version regresses.

## Search

- **Search filters live on the search services in Core, not on the MCP tool.** `KeywordSearchService.Search`, `VectorSearchService.SearchAsync`, `HybridSearchService.SearchAsync`, and `MessageRepository.BrowseByFilters` all accept a `SearchFilters?` (`Folder` / `DateFrom` / `DateTo` / `FromContains` / `FromExact`). The shared `SearchFilterSql.Append` helper appends WHERE clauses and binds params so filter semantics stay identical across the BM25 leg, the vector leg, and the query-less browse path. Hybrid RRF fuses two ranked lists, so post-filtering only one side would skew scores. **Don't** post-filter in the MCP layer. `FromExact` takes precedence over `FromContains` when both are set.
- **Vector KNN runs before the filter join.** `chunk_embeddings MATCH $vec AND k=$k` returns the raw nearest neighbours; the filter is applied in the joined CTE. With a restrictive filter, a small `k` leaves the post-filter set short even when good matches exist further down the KNN list (this once made bare `mode=semantic` + a folder filter return ~1 result). **`VectorSearchService.SearchByVector` is the single source of truth for the workaround**: when filters are present it escalates `k` (×8 per round, starting at `max(k, limit)`, bounded by the chunk count) until it has `limit` post-filter hits or has fetched every chunk. Callers (`HybridSearchService`, the MCP/tray/CLI search paths, the eval ranking source) just pass a base `k` and no longer pre-inflate — don't reintroduce per-caller `k` inflation, it drifts. **sqlite-vec hard-rejects `k > 4096`** ("k value in knn query too large"); `Vec0MaxK = 4096` caps the escalation and clamps every KNN call. Escalating deeper than the old fixed `k` changes which candidates feed RRF on corpus-selective filters, so re-baseline (`mailvec eval`) after touching this.
- **Date filtering uses `datetime()` for comparison.** Stored `date_sent` is `DateTimeOffset.ToString("O")`, which mixes UTC `Z` and `+HH:mm` offsets. Wrapping both sides in SQLite's `datetime()` normalises them; a raw string compare would silently miss matches across offsets. Messages with NULL `date_sent` are excluded when any date bound is set.
- **One row per message** in vector search results. `VectorSearchService` uses a window function (`ROW_NUMBER() OVER ... rn=1`) so the same message can't appear twice from different chunks. Hybrid search relies on this invariant when fusing. Attachment matches don't add rows — the chunk's `source` + `attachment_id` ride along as `VectorHit.ChunkSource` / `MatchedAttachment*`.
- **RRF k=60** in `HybridSearchService.RrfK` — standard from the literature. Don't tune without benchmarking against a real query set.
- **`get_thread` resolves via thread_id, with a special case for lone messages.** `MessageRepository.GetThreadByMessageId` first looks up the requested message, then queries by its `thread_id`. If `thread_id` is NULL (a message that wasn't part of any reference chain), the method returns just that one message rather than empty. Don't "fix" this to require non-null thread_id — singletons are common (notifications, marketing).
- **`SchemaMigrator.EnsureUpToDate()` runs at MCP startup.** If the configured `DatabasePath` doesn't exist, the migrator silently creates a fresh empty schema there. When debugging "no results", first check `mailvec status` to confirm message counts on the same path the MCP server resolved.

## MCP API stability

Once Phase 5 starts (Gemini CLI / Codex CLI / ChatGPT desktop pointing at the same server), tool names, parameter names, and response field names become a **contract** — renames break every client at once. Treat this list as locked unless you're deliberately bumping the version:

- **Tool names**: `search_emails`, `get_email`, `get_thread`, `list_folders`, `get_attachment`, `get_attachment_text`, `get_attachment_page_image`. Set via `[McpServerTool(Name = "...")]` on each tool class — don't let the SDK infer from the C# method name.
- **Parameter names** that travel back as references between tools: `partIndex` (returned by `get_email`, consumed by `get_attachment`); `id` and `messageId` everywhere; `mode` ∈ {`hybrid`, `keyword`, `semantic`}; `fromContains` / `fromExact` / `dateFrom` / `dateTo` / `folder` filter set.
- **Response field names** that clients narrate to users: `matchedAttachment.{partIndex,fileName}`, `archiveStats.{totalMessages,oldestDate,latestDate}`, `appliedFilters.*`, `webmailUrl`.
- **Server identity**: `serverInfo.name = "mailvec"` (lowercase, the protocol identifier — Phase 5 client configs key off it). Bump `serverInfo.version` whenever you ship a tool-surface change so a client log line of "I'm talking to mailvec 0.1.16" tells you which build you're seeing.

Source-of-truth for version: `<Version>` in [Mailvec.Mcp.csproj](src/Mailvec.Mcp/Mailvec.Mcp.csproj), kept in lockstep with `manifest.json` by `ops/build-mcpb.sh --bump`. Read at runtime via `Assembly.GetEntryAssembly().GetName().Version` in `Program.cs::ConfigureServerInfo`.

Before any change that could shift retrieval ranking (chunk size, RRF k, embedding model, tool-shape tweaks), capture an eval baseline (`mailvec eval --json baselines/<date>.json`) — see `baselines/README.md`. Without it, a quality regression that "looked like a refactor" is invisible until the user notices.

## MCP transport quirks

- **MCP runs in two transport modes** sharing the same Core wiring (`AddMailvecServices` helper in `Program.cs`):
  - **HTTP (default)**: `MapMcp()` mounted at `/` over Streamable HTTP. Clients must `initialize` first, capture the `Mcp-Session-Id` response header, send `notifications/initialized`, and include the session header on every subsequent call. Without the session header, calls 404 silently.
  - **Stdio (`--stdio` flag)**: Generic `Host` + `WithStdioServerTransport()`. Used by Claude Desktop because its connector schema only supports stdio. Wired up via `ops/run-mcp-stdio.sh`.
- **In stdio mode, all logging must go to stderr.** Stdout is the JSON-RPC channel; a single byte on stdout corrupts the protocol. `Program.cs` calls `ClearProviders()` and sets `LogToStandardErrorThreshold = LogLevel.Trace`; `SerilogSetup.Configure(..., stdioMode: true)` passes `standardErrorFromLevel: LogEventLevel.Verbose` to the Console sink so even Verbose/Debug events go to stderr. Don't add any stdout writer in `RunStdio`. Don't use `dotnet run` for stdio launching — its build chatter goes to stdout. `ops/run-mcp-stdio.sh` builds quietly to a log file (`$TMPDIR/mailvec-mcp-stdio-build.log`) and execs the compiled DLL directly.
- **Claude Desktop launches MCP servers with a sanitized environment AND a hard read block on `~/Documents`.** Three quirks:
  1. **`~/Documents` is unreadable for content even with Full Disk Access.** Spawned children can `stat()` files but `open()` returns EPERM (likely a per-app `com.apple.macl` ACL). FDA + Documents toggle in System Settings does not fix this. **Workaround**: don't run anything from inside `~/Documents`. The MCP binary is `dotnet publish`-ed to `~/.local/share/mailvec/mcp/` (by `ops/install-stdio-launcher.sh` for non-Claude stdio clients, by `ops/install.sh` / `ops/redeploy.sh` for the launchd HTTP service). Diagnostic distinction: `ls -l <file>` succeeding doesn't mean Claude can open the file (`ls` is just `lstat()`); have the diag script `head -1 <file>` and check exit status.
  2. **PATH excludes `/usr/local/share/dotnet`.** Spawned PATH is `~/.nvm/.../bin:/opt/homebrew/bin:~/.local/bin:/usr/bin:/bin:/usr/sbin:/sbin`. The Claude Desktop MCPB bundle dodges this by being self-contained (`--self-contained true` in `ops/build-mcpb.sh`). For non-Claude stdio clients (Phase 5 — Gemini CLI, Codex CLI, ChatGPT desktop), `ops/install-stdio-launcher.sh` writes a `~/.local/bin/mailvec-mcp-stdio` shim that exports `DOTNET_ROOT` and prepends it to `PATH` so the framework-dependent binary resolves a runtime.
  3. Use `/bin/bash <script>` form in `claude_desktop_config.json` (not the script directly) — avoids depending on the shebang interpreter being on Claude's allowed-exec list. Diagnostic recipe: have the script `echo` to `>&2`, end with `cat >/dev/null` so stdin stays open and logs land in `~/Library/Logs/Claude/mcp-server-mailvec.log` before SIGTERM.
- **MCP and CLI must use identical Core wiring.** `Mailvec.Mcp/Program.cs` mirrors `CliServices` (same singletons, same HttpClient setup for `OllamaClient`). If you add a search-affecting service, register it in both — drift means CLI debugging stops matching MCP behaviour.

## Operations

- **`/health` is HTTP-mode only.** Stdio has no analog — Claude Desktop sees failures via the `initialize` handshake instead. Endpoint is registered before `MapMcp()` in `Program.cs::RunHttp` and bypasses MCP framing. Logic lives in `Mailvec.Core/Health/HealthService.cs` so the CLI can reuse the snapshot if needed. Returns 503 (not 200) when degraded ("degraded" = Ollama unreachable OR `metadata.embedding_model` disagrees with `Ollama:EmbeddingModel`); don't broaden the degraded set without thinking about false-positive paging cost. Auth boundary is "bind to 127.0.0.1" — see the README Security model section before changing the bind address.
- **Ollama ping is bounded by a 2 s linked-CTS, not the embedder's 60 s timeout.** `OllamaClient.PingAsync` wraps the call in `CancellationTokenSource.CreateLinkedTokenSource(ct)` with `CancelAfter(2s)` and only swallows `OperationCanceledException` when the *outer* token wasn't cancelled. If you raise the cap, don't go above ~5 s.
- **Log rotation is in-process (Serilog), not external.** Each launchd-installed service wires `SerilogSetup.Configure(...)`. Output: `~/Library/Logs/Mailvec/mailvec-<service>-<YYYYMMDD>.log`, daily rolling, also rolls if a single day exceeds 10 MB, 14 most recent files retained. Rolling is atomic *within a single writer per service* — true because the launchd HTTP MCP / indexer / embedder are each one process. **MCP in stdio mode does NOT write to this file** — Claude Desktop spawns one `Mailvec.Mcp --stdio` child per session (main chat + one per Cowork session), all named `Mailvec.Mcp`, concurrent with the launchd HTTP MCP. If they shared the rolling file, they'd race on size-cap rolling and retention prune: `shared: false` only enforces single-writer on Windows, but POSIX `O_APPEND` happily admits multiple writers and Serilog swallows the resulting `IOException`s via `SelfLog`. Stdio output goes to stderr only; the client captures it (Claude Desktop → `~/Library/Logs/Claude/mcp-server-mailvec.log`). Source: [`SerilogSetup.Configure`](src/Mailvec.Core/Logging/SerilogSetup.cs) wraps the file sink in `if (!stdioMode)`.
- **`MAILVEC_LAUNCHD=1` suppresses Serilog's Console sink.** Set in the launchd plist `EnvironmentVariables`. Without it, every log line writes to both the rolling file and stdout/stderr, where launchd captures it into `StandardOutPath`/`StandardErrorPath` — doubling disk usage. With the env var set, the launchd-captured `<service>.launchd.log` only catches things that bypass `ILogger`: pre-Serilog startup output, unhandled native stderr, panics.

## mailvec CLI shim

`ops/install.sh` publishes the CLI to `~/.local/share/mailvec/cli/` (alongside the three .NET services) and drops a shim at `~/.local/bin/mailvec` that execs `dotnet ~/.local/share/mailvec/cli/Mailvec.Cli.dll`. The shim sets `DOTNET_ROOT` + `PATH` so it works under Claude Desktop's sanitised-PATH child processes too.

- **Why a shim, not a symlink to the .dll**: .NET requires `dotnet <dll>`, not direct invocation. The shim hides that detail.
- **Why a shim, not `/usr/local/bin/mailvec`**: writing to `/usr/local/bin` needs sudo or a Homebrew tap. `~/.local/bin/mailvec` is sudo-free and the tray UI's [`CliRunner.swift`](src/Mailvec.Tray/Sources/CliRunner.swift) prefers it anyway.
- **Re-run `ops/install.sh` (or `ops/redeploy.sh cli`) after CLI source changes** — the shim is generated at install time and points at a published .dll, not the working-tree source. `dotnet build` alone won't update it.
- **Eight tray buttons depend on this**: Dashboard's Doctor, Embedding's Audit/Reindex, Advanced's Doctor/RebuildFTS/Checkpoint/Audit/Purge. All route through `CliRunner.runInTerminal(...)`.

## Status

End-to-end working with Claude Desktop (MCPB stdio) and Claude Code (HTTP). Phase 5 (other local agents) not yet started. See [`CHANGELOG.md`](CHANGELOG.md) for the phase-by-phase build history.
