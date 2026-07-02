# Changelog

Built in phases. Each phase has a hard exit criterion and shipped standalone value before the next began.

## ✅ Phase 0 — Repo scaffold

Solution, projects, central package management, shared MSBuild settings, ops + schema folders, gitignore, README.

## ✅ Phase 1 — Ingest pipeline

A searchable local archive. **Exit criterion met:** point the indexer at a Maildir and `mailvec search "<query>"` returns BM25-ranked hits with snippets.

- Schema migration runner (`Mailvec.Core.Data.SchemaMigrator`) over an embedded `001_initial.sql`.
- MimeKit-based parser with fixture-driven tests (plain text, multipart, html-only, unicode headers, attachments).
- `MaildirScanner` — recursive full scan, soft-delete reconciliation, mbsync `new/`→`cur/` rename detection.
- `MaildirWatcher` — debounced `FileSystemWatcher` with `tmp/` filtering.
- `MessageIngestService` — `BackgroundService` that runs an initial scan, then reacts to watcher pulses + a periodic safety-net rescan.
- FTS5 with BM25 ordering and bracketed snippets.
- `mailvec status | search | rebuild-fts` CLI.

## ✅ Phase 2 — Semantic layer

Adds locally-generated embeddings and hybrid (FTS + vector) search. **Exit criterion met:** semantic + hybrid search return chunk-level results from sqlite-vec; behaviour validated against hand-injected vectors in tests. Real-world quality validation is on you once you have an embedded archive.

- `OllamaClient` — typed `HttpClient` against `POST /api/embed` with `Microsoft.Extensions.Http.Resilience` retry/circuit-breaker.
- `ChunkingService` — paragraph-aware splitter (~4 chars/token heuristic) with configurable size + overlap; hard-splits unbroken blocks.
- `ChunkRepository` — atomic chunk + vector writes (so a message is never half-embedded).
- `EmbeddingWorker` — `BackgroundService` that polls `messages WHERE embedded_at IS NULL`, prepends subject to body, batches Ollama calls, and refuses to start if `metadata.embedding_model` disagrees with config.
- `VectorSearchService` — sqlite-vec `MATCH/k` query, returns one row per message (best-matching chunk), filters soft-deleted.
- `HybridSearchService` — Reciprocal Rank Fusion (k=60) over BM25 + vector legs.
- CLI: `search --semantic`, `search --hybrid`, `reindex --all | --folder=NAME`. `status` now surfaces embedding coverage, chunk count, and schema/config model mismatches.

## ✅ Phase 3 — MCP exposure

Wires the archive up to Claude. **Exit criterion met.**

- `Mailvec.Mcp` runs in two transports sharing the same Core wiring:
  - **HTTP** (default) on `127.0.0.1:3333` for Claude Code and the smoke tests.
  - **stdio** (`--stdio` flag) for Claude Desktop, packaged as an `.mcpb` bundle (see [docs/clients/claude-desktop.md](docs/clients/claude-desktop.md)).
- Tools: `search_emails` (keyword / semantic / hybrid with folder/date/sender filters), `get_email`, `get_thread`, `list_folders`, `get_attachment`. The original 6-tool design merged to 4 search/fetch tools — `recent_emails` is `search_emails` with `query` omitted, and `find_by_sender` is `search_emails` with `fromExact` — plus `get_attachment` added later for attachment delivery (see [docs/attachments.md](docs/attachments.md)).
- Hybrid search reused from Phase 2.
- Attachment **filename** indexing: filenames are stored in the `attachments` table and surfaced through FTS5 (so a query like `"mortgage statement"` matches an email whose only mention is in `mortgage_statement_2024.pdf`). Per-attachment metadata (filename, content type, size, partIndex) is returned by `get_email`. Attachment **content** indexing landed in Phase 4.5 below.

## ✅ Phase 4 — Operationalization

Makes the system survive reboots unattended. **Exit criterion met:** rebooted, all four agents (mbsync + indexer + embedder + MCP) came back without intervention; `/health` returns 200 and `mailvec status` shows the pipeline still progressing.

- ✅ launchd plist templates in [`ops/launchd/`](ops/launchd/) for mbsync + indexer + embedder + MCP HTTP server. Use placeholders (`__DOTNET__`, `__INSTALL_PREFIX__`, `__LOG_DIR__`, `__DB_PATH__`, `__MAILDIR_ROOT__`, `__OLLAMA_URL__`, `__MBSYNC__`, `__MBSYNCRC__`, `__FASTMAIL_ACCOUNT_ID__`) that `install.sh` substitutes at install time. The three .NET plists surface the user-tunable config (DB path, Maildir, Ollama URL, Fastmail) as `EnvironmentVariables` so changes don't require a republish.
- ✅ [`ops/install.sh`](ops/install.sh) — preflights `dotnet`/`mbsync`/`vec0.dylib`, prompts for the site-specific values, `dotnet publish`-es indexer/embedder/mcp into per-service subdirs under `~/.local/share/mailvec/`, boots out any existing agents (idempotent), renders the plists, `launchctl bootstrap`s them, and polls `/health` for up to 15 s. `--uninstall` reverses all of that while preserving the published binaries, the database, and the logs.
- ✅ `/health` endpoint on the MCP server — `GET /health` returns DB / embedding / Ollama status (200 healthy, 503 degraded). HTTP-mode only; stdio sees failures via the MCP `initialize` handshake.
- ✅ Log rotation — handled in-process by Serilog. The three .NET services write rolling daily files to `~/Library/Logs/Mailvec/mailvec-<service>-<YYYYMMDD>.log` (10 MB per-file cap, 14 files retained). Wiring lives in [src/Mailvec.Core/Logging/SerilogSetup.cs](src/Mailvec.Core/Logging/SerilogSetup.cs); the launchd plist sets `MAILVEC_LAUNCHD=1` to suppress the Console sink in production. mbsync (the only non-.NET service) writes to small launchd-captured files that don't need rotation.
- ✅ `mailvec status` surfaces message count, embedding coverage, schema/config model match.

**Exit criterion:** reboot the Mac mini; everything comes back without intervention.

## ✅ Phase 4.5 — Attachment content indexing

Extends keyword + semantic + hybrid search to the *contents* of attached documents, not just their filenames. **Exit criterion met:** a query that only appears in the body of an attached PDF / DOCX returns the parent email, and the result identifies which attachment drove the hit.

- Schema v4: `attachments.extracted_text` / `extracted_at` / `extraction_status` hold the recovered plain text; `chunks.source` ('body' | 'attachment') + `chunks.attachment_id` let search results trace back to a specific document. In-place migrations live in [`schema/migrations/004_attachment_text.sql`](schema/migrations/004_attachment_text.sql) and [`schema/migrations/005_attachment_text_fts.sql`](schema/migrations/005_attachment_text_fts.sql); v3→v4 backfill via `mailvec extract-attachments` (re-walks `.eml` files, populates `extracted_text` in place, clears chunks for affected messages so the embedder regenerates with attachment content).
- Pure-managed extractors: PDF via `PdfPig`, DOCX via `DocumentFormat.OpenXml`, plain text inline. No native deps, no shell-out, no OCR. Scanned PDFs come back as `extraction_status='no_text'` and are intentionally not retried.
- The embedder stitches body + per-attachment chunks into one chunk stream per message; vector search dedups to one row per message and surfaces `matchedAttachment { partIndex, fileName }` when the winning chunk came from a document — exactly the inputs `get_attachment` needs.
- FTS5 column `attachment_text` carries the extracted text alongside `body_text`, so keyword and hybrid searches surface document-content hits without any extra wiring.
- Read-side delivery beyond `get_attachment`'s file-on-disk: `get_attachment_text` returns the stored `extracted_text` inline (pure DB read — works over a remote connection, the universal "what does this say" path), and `get_attachment_page_image` renders a PDF page to JPEG via PDFtoImage/PDFium (the one binary type every client renders; covers tables/forms/signatures and scanned PDFs that have no text layer). The renderer caps the long edge at ~1536px and encodes JPEG q85. PDFium is the only native dep in the read path besides sqlite-vec, referenced solely by `Mailvec.Mcp` — see [`ops/UPGRADING.md`](ops/UPGRADING.md).

## ⬜ Phase 5 — Support for non-Claude local agents

Mailvec works end-to-end with Claude Desktop (MCPB stdio) and Claude Code (HTTP). Phase 5 extends the same two transports to other locally-running MCP-capable agents — no protocol changes, just per-client config and quirk capture (the equivalent of the Claude Desktop sanitized-env / TCC-block findings in CLAUDE.md):

- **Gemini CLI** via `~/.gemini/settings.json` `mcpServers`.
- **Codex CLI** via `~/.codex/config.toml` `[mcp_servers.mailvec]`.
- **ChatGPT desktop** once its MCP-server registration ships in the user's release.
- Per-client config snippets checked into `docs/clients/`, exercised in an integration tier during release prep.

Public-HTTPS / OAuth access for cloud LLMs (Claude.ai web, ChatGPT Connectors, Gemini in the browser) is parked in [`docs/future-ideas.md`](docs/future-ideas.md) — the operational cost of a public tunnel + OAuth issuer for a single-user system is higher than the value over local-agent coverage.

## Out of scope

Sending mail, modifying Fastmail state, multi-account support, calendar/contacts/files, web UI, real-time push. Attachment **filenames** are indexed (FTS5), attachments themselves are extractable to disk via `get_attachment`, and attachment **content** is extracted at index time — PDF / DOCX / XLSX / PPTX / iCalendar / vCard / plain text — and fed into both FTS5 and the vector index. Scanned / image-only PDFs and images are recovered out of band by the embedder's local-vision-model OCR pass (status `ocr`). Archive formats (zip/tar) remain out of scope — let downstream tools (Claude Code's `Read`, a filesystem MCP, your existing PDF skills) interpret those once `get_attachment` has dropped them on disk.
