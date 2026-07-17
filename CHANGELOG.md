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
- Tools: `search_emails` (keyword / semantic / hybrid with folder/date/sender filters), `get_email`, `get_thread`, `list_folders`, `get_attachment` *(later renamed `view_attachment` — the name [docs/attachments.md](docs/attachments.md) documents it under)*. The original 6-tool design merged to 4 search/fetch tools — `recent_emails` is `search_emails` with `query` omitted, and `find_by_sender` is `search_emails` with `fromExact` — plus `get_attachment` added later for attachment delivery.
- Hybrid search reused from Phase 2.
- Attachment **filename** indexing: filenames are stored in the `attachments` table and surfaced through FTS5 (so a query like `"mortgage statement"` matches an email whose only mention is in `mortgage_statement_2024.pdf`). Per-attachment metadata (filename, content type, size, partIndex) is returned by `get_email`. Attachment **content** indexing landed in Phase 4.5 below.

## ✅ Phase 4 — Operationalization

Makes the system survive reboots unattended. **Exit criterion met:** rebooted, all four agents (mbsync + indexer + embedder + MCP) came back without intervention; `/health` returns 200 and `mailvec status` shows the pipeline still progressing.

- ✅ launchd plist templates in [`ops/launchd/`](ops/launchd/) for mbsync + indexer + embedder + MCP HTTP server, with placeholders that `install.sh` substitutes at install time. *(Historical note: the config-as-`EnvironmentVariables` scheme described here — `__DB_PATH__`, `__MAILDIR_ROOT__`, `__OLLAMA_URL__`, `__FASTMAIL_ACCOUNT_ID__` in the plists — was superseded by the shared `~/Library/Application Support/Mailvec/appsettings.Local.json`; the current templates carry only `__DOTNET__`, `__INSTALL_PREFIX__`, `__LOG_DIR__` and the mbsync paths. See "Configuration" in CLAUDE.md.)*
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
- Read-side delivery beyond `get_attachment`'s file-on-disk: `get_attachment_text` returns the stored `extracted_text` inline (pure DB read — works over a remote connection, the universal "what does this say" path), and `get_attachment_page_image` renders a PDF page to JPEG via PDFtoImage/PDFium (the one binary type every client renders; covers tables/forms/signatures and scanned PDFs that have no text layer). The renderer caps the long edge at ~1536px and encodes JPEG q85. PDFium is the only native dep in the read path besides sqlite-vec, referenced at this point solely by `Mailvec.Mcp` (the embedder's OCR pass later added a second reference) — see [`ops/UPGRADING.md`](ops/UPGRADING.md).

## ✅ Deployment — homelab containers + public OAuth-gated MCP

Moves the pipeline off the Mac and onto a Proxmox homelab Docker VM, then exposes MCP to the internet so non-local clients (the driver: **Claude iOS**) can reach it. **Exit criterion met:** every Claude surface — Code, Desktop, iOS, claude.ai — searches the archive through one OAuth-gated remote connector, with nothing depending on the Mac being online.

- **One image, four binaries** ([`Dockerfile`](Dockerfile)) + a compose stack ([`compose.yml`](compose.yml)) with Ollama external on a GPU VM. `TARGETARCH`→RID mapping builds the amd64 image from Apple Silicon; `vec0.so` is fetched inside the build. Prebuilt images publish to GHCR on green-main and on `v*` tags; production pins a `v*` tag (never pruned, unlike `sha-`). See [`docs/deploy-docker.md`](docs/deploy-docker.md).
- **Seeded by snapshot, not rebuild.** A checkpointed `ops/export-db.sh` copy of the Mac archive dropped at the bind mount — bit-identical embedding model/dimensions, so nothing re-embedded. `MAILVEC_REQUIRE_SEEDED_DB=1` makes the entrypoint refuse to start against a missing/empty archive, since `SchemaMigrator` would otherwise silently create a healthy-looking empty one. Eval parity against the baseline confirmed no ranking drift from the .NET-on-Linux swap.
- **Public access: Cloudflare Tunnel → Cloudflare Access self-hosted app with Managed OAuth.** Forced by a hard constraint — connectors are called from *Anthropic's cloud* (`160.79.104.0/21`, IPv4-only), not the phone, so Tailscale/LAN/loopback could never serve iOS. No inbound ports; the sidecar dials out. See [`docs/remote-access-cloudflare.md`](docs/remote-access-cloudflare.md).
- **The unblocker was a redirect-URI allowlist.** Zero Trust rejected claude.ai's dynamic client registration with `400 invalid_client_metadata` until `https://claude.ai/api/mcp/auth_callback` was allowlisted. This had previously read as a discovery-metadata problem and stalled the whole plan. The MCP Server Portal and the Worker + `workers-oauth-provider` fallback both turned out to be unnecessary — and skipping the portal also sidestepped its Code Mode default, which would have collapsed the locked tool surface into one code-execution tool.
- **`Mcp:DisabledTools`** ([`ToolSurface.Resolve`](src/Mailvec.Mcp/)) lands as a per-deployment tool-surface trim — unknown names fail startup rather than silently leaving a tool exposed. Currently **unused** in the homelab: the native-parser tools stay exposed as a documented accepted risk with explicit invalidating conditions ([`docs/security.md`](docs/security.md)).
- **The security model changed shape**, from "single-user, single-Mac; unreachable from outside" to a two-layer boundary (Access identity gate outside, compose network inside). `docs/security.md` was rewritten rather than patched — several accepted risks had rested on "everything is local."
- **`HostGuard`** becomes load-bearing for the tunnel: cloudflared forwards the public `Host` header, so `MCP_PUBLIC_HOSTNAME` must be allowlisted or every tunnelled request 403s. It is *not* an auth boundary.
- **The Mac pipeline was decommissioned** (2026-07-16) once parity held — agents and plists removed via `ops/install.sh --uninstall`, mbsync included, so nothing re-bootstraps at login. The Mac becomes the dev machine and its archive a **frozen corpus** (~80k messages, fully embedded) against which the ~70 labeled eval queries still resolve — which is the whole point: ranking work needs a corpus that doesn't move under the baseline. See [`docs/contributing/local-dev-dataset.md`](docs/contributing/local-dev-dataset.md).

## ⬜ Phase 5 — Support for non-Claude local agents

Mailvec works end-to-end with every Claude surface via the remote connector; the local transports (MCPB stdio, loopback HTTP) still ship for single-machine installs. Phase 5 extends those two local transports to other locally-running MCP-capable agents — no protocol changes, just per-client config and quirk capture (the equivalent of the Claude Desktop sanitized-env / TCC-block findings in CLAUDE.md):

- **Gemini CLI** via `~/.gemini/settings.json` `mcpServers`.
- **Codex CLI** via `~/.codex/config.toml` `[mcp_servers.mailvec]`.
- **ChatGPT desktop** once its MCP-server registration ships in the user's release.
- Per-client config snippets checked into `docs/clients/`, exercised in an integration tier during release prep.

Cross-vendor *cloud* access (ChatGPT Connectors, Gemini in the browser) stays parked in [`docs/future-ideas.md`](docs/future-ideas.md) — but the reason has changed. The tunnel + OAuth front now exist and are vendor-agnostic; the blocker is that nothing scopes access per-client, so a second vendor's connector would get the same unscoped read of the entire mailbox.

## Out of scope

Sending mail, modifying Fastmail state, multi-account support, calendar/contacts/files, web UI, real-time push. Attachment **filenames** are indexed (FTS5), attachment content is viewable inline via `view_attachment` / `get_attachment_text` / `get_attachment_page_image` (save-to-disk is the tray Save button or `mailvec extract-attachments`), and attachment **content** is extracted at index time — PDF / DOCX / XLSX / PPTX / iCalendar / vCard / plain text — and fed into both FTS5 and the vector index. Scanned / image-only PDFs and images are recovered out of band by the embedder's local-vision-model OCR pass (status `ocr`). Archive formats (zip/tar) remain out of scope — save one to disk via the tray Save button or `mailvec extract-attachments` and let downstream tools (Claude Code's `Read`, a filesystem MCP, your existing PDF skills) interpret it from there.
