# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Source of truth

`mailvec-project-scope.md` is the design document and the canonical reference for goals, non-goals, schema, and the phased build plan. Read it before making non-trivial changes.

Operator/release docs (read on demand, not loaded into context):
- `ops/UPGRADING.md` — bumping NuGet packages, the .NET SDK, sqlite-vec, SQLite, Ollama floor.
- `ops/mcpb-release.md` — building and shipping the MCPB bundle.

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
ops/redeploy.sh [indexer|embedder|mcp ...]    # republish + kickstart launchd agents after a code change
```

`Mailvec.slnx` (not `.sln`) is the solution file — .NET 10 emits the new XML format by default.

`dotnet run --project src/Mailvec.<svc>` runs the working-tree code under your terminal — useful for one-off debugging, but **the launchd agents installed by `ops/install.sh` are separate processes** running the published binaries under `~/.local/share/mailvec/<svc>/`. After editing service code, run `ops/redeploy.sh` to push the new binaries and restart the agents — otherwise the live services keep running the old code while `dotnet build` looks like it succeeded. Use `ops/install.sh` only when plist templates change or a config knob needs updating.

## Build conventions

- **Central Package Management is on.** All NuGet versions live in `Directory.Packages.props`. csproj files use `<PackageReference Include="..." />` *without* a `Version=` attribute. Adding a new dependency means editing both files.
- **`TreatWarningsAsErrors=true`** in `Directory.Build.props` — a build warning fails the build.
- **`net10.0`, nullable enabled, implicit usings on** are inherited from `Directory.Build.props`; do not redeclare them in individual csprojs.

## Architecture

Four independent processes, communicating only through the filesystem (Maildir) and the SQLite database. Each can be restarted or replaced without affecting the others — keep this isolation when extending.

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
                           ▲
                           │ writes chunks + vectors
                           │
        Mailvec.Embedder  ──► Ollama (localhost:11434)
```

Project map:

- **Mailvec.Core** — shared library. Domain models, SQLite access (with `sqlite-vec` extension loading), Ollama HTTP client, hybrid (FTS+vector RRF) search logic, and `Options/` POCOs (`ArchiveOptions`, `IngestOptions`, `OllamaOptions`, `IndexerOptions`, `EmbedderOptions`, `McpOptions`). All four executables reference Core.
- **Mailvec.Indexer** — `BackgroundService` worker. Scans Maildir, parses with MimeKit, upserts `messages`. Does *not* call Ollama.
- **Mailvec.Embedder** — `BackgroundService` worker. Polls for `messages WHERE embedded_at IS NULL`, chunks bodies, calls Ollama, writes `chunks` + `chunk_embeddings`.
- **Mailvec.Mcp** — AspNetCore app exposing MCP tools over HTTP on `127.0.0.1:3333`. Read-only against the database (except `get_attachment`, which reads `.eml` files out of the Maildir).
- **Mailvec.Cli** — admin commands (status, search, reindex, rebuild-fts, purge-deleted) hitting the same DB.

## Data model invariants

`schema/001_initial.sql` is the full schema. Two non-obvious constraints:

1. **The embedding model is part of the schema, enforced at runtime.** `chunk_embeddings` declares a fixed dimension (`FLOAT[1024]` for `mxbai-embed-large`). `OllamaClient` validates returned vector lengths against `Ollama:EmbeddingDimensions`; `EmbeddingWorker` refuses to start if `metadata.embedding_model` disagrees with config. Switching models requires a full reindex. **Never bypass this check** — mixing vector spaces silently corrupts similarity scores in ways that look plausible.
2. **FTS5 sync is trigger-driven.** `messages_ai`/`messages_ad`/`messages_au` keep `messages_fts` in lockstep with `messages`. Don't bypass the triggers with raw inserts into `messages_fts`.

## Configuration

Each runnable service has its own `appsettings.json` containing only the sections it needs. Configuration POCOs live in `Mailvec.Core/Options/` and are bound in each service's `Program.cs`. Local overrides go in `appsettings.Local.json` (gitignored).

`ArchiveOptions` (`DatabasePath`, `SqliteVecExtensionPath`) is bound by all four executables (consumed by `ConnectionFactory`). `IngestOptions` (`MaildirRoot`) is bound by the Indexer (scans the Maildir), the CLI (prints the path in `mailvec status`), and the MCP server (`get_attachment` reads attachment bytes out of Maildir source files). The Embedder is the only executable that never reads the filesystem.

## sqlite-vec

Fetched as a prebuilt `vec0.dylib` by `ops/fetch-sqlite-vec.sh` and loaded at runtime via `Microsoft.Data.Sqlite`'s extension-loading API. Path comes from `Archive:SqliteVecExtensionPath`. Fetch script supports `osx-arm64` and `osx-x64`; add other RIDs there if needed. **Don't switch to the NuGet wrapper** — see `ops/UPGRADING.md`.

## Current status

Phases 1–4 complete. The indexer ingests, the embedder embeds, keyword/semantic/hybrid search all work, and the MCP server exposes four tools — `search_emails`, `get_email`, `get_thread`, `list_folders` — over both Streamable HTTP (`127.0.0.1:3333`, default) and stdio (`--stdio` flag). Claude Desktop integration ships as an MCPB bundle built by `ops/build-mcpb.sh`. Phase 4 is reboot-validated. Phase 5 (non-Claude local agents — Gemini CLI, Codex CLI, ChatGPT desktop) not yet started; it's local-agent expansion, not cloud access — see scope doc §11 for the cloud-access framing.

The doc's original 6-tool list got merged to 4: `recent_emails` is `search_emails` with `query` omitted (date-sorted browse path via `MessageRepository.BrowseByFilters`), and `find_by_sender` is `search_emails` with `fromExact: "..."` (exact-match alternative to the existing `fromContains` substring filter).

## MCPB bundle (code-relevant gotchas)

Release/build steps live in `ops/mcpb-release.md`. The gotchas below stay here because they bite at code-edit time:

- **`manifest.json` user_config defaults must use `~/...`, not `${HOME}/...`.** Claude Desktop's MCPB host substitutes its own `${user_config.X}` tokens but passes shell-style `${HOME}` through verbatim. `PathExpansion.Expand` defensively handles `${HOME}` / `$HOME` regardless; tests in `PathExpansionTests.cs`.
- **Don't add `required: true` user_config fields in an upgrade.** The upgrade flow doesn't pre-fill new required fields, so the extension lands disabled and invisible in the connector picker. Make new fields `required: false` with a sensible default.
- **Never set `UseStructuredContent = true` on an `[McpServerTool]` whose return type is `CallToolResult`.** The .NET SDK can't infer a meaningful schema from the generic return type and emits an invalid one; Claude Desktop's Zod validator then rejects the *entire extension*. Structured content still flows back at runtime via `CallToolResult.StructuredContent` regardless of the flag. If you need an advertised output schema, return a strongly-typed POCO instead.
- **First place to look when something's wrong:** `~/Library/Logs/Claude/mcp-server-mailvec.log`.

## Phase 1 gotchas

- **HTML body parsing uses AngleSharp, not MimeKit.** The design doc's reference to MimeKit's `HtmlToText` was wrong (MimeKit doesn't ship that helper). We use AngleSharp's DOM via `Mailvec.Core.Parsing.HtmlToText`, which strips marketing-email noise (hidden preheader text, tracking pixels, image-only and unsubscribe/preferences links, `<address>` and `<footer>` blocks). Output runs through `TextNormalize` and `BoilerplateFilter`. **Tuning detail to remember:** `font-size:0`, `max-height:0`, and `max-width:0` on outer `<td>` wrappers are *layout hacks* (defeating inter-cell whitespace) where the real text lives in inner elements that override the property — never treat these as hide signals or you'll nuke whole branches of legitimate content. Migration when the converter changes: `mailvec rebuild-bodies --reembed` re-derives `body_text` from stored `body_html` and clears `embedded_at`.
- **`Microsoft.Data.Sqlite.ExecuteNonQuery` silently stops at the first `CREATE TRIGGER ... BEGIN ... END;`.** The internal trigger semicolons confuse its statement iterator. We tokenise scripts ourselves in `SqlScriptSplitter` (BEGIN/END depth-tracking) and execute one statement at a time.
- **`vec0` must load before the schema applies**, because `001_initial.sql` declares `chunk_embeddings` as a `vec0(...)` virtual table. `ConnectionFactory` loads the extension on every `Open()`. The dylib is copied into each project's `bin/.../runtimes/<rid>/native/` by `Directory.Build.props` (with an `Exists` guard).
- **Rename detection.** When mbsync renames `new/foo` → `cur/foo:2,S` between scans, the same Message-ID appears at a fresh path while the old `sync_state` row still references the old path. The scanner uses `SyncStateRepository.FreshMessageIds(since)` to exclude renamed messages from the soft-delete pass.
- **Config env-var convention.** Both indexer and CLI read environment variables with no prefix, so `Ingest__MaildirRoot=/path` works for either. The CLI looks for `appsettings.json` in `AppContext.BaseDirectory`, not the current working directory.
- **`MaildirRoot` lives on `IngestOptions`, not `ArchiveOptions`.** Only the Indexer (scanner + watcher), CLI, and MCP `get_attachment` need the Maildir path; carrying it on the shared `ArchiveOptions` POCO leaked into the MCPB manifest as a required user_config prompt the bundle didn't always need. Section name is `Ingest`. Env var: `Ingest__MaildirRoot` (was `Archive__MaildirRoot`).

## Phase 2 gotchas

- **`ChunkingService` lives in `Mailvec.Core/Embedding/`, not `Mailvec.Embedder/Services/`** as the design doc shows. It's pure logic with no I/O; keeping it in Core lets us test it from `Mailvec.Core.Tests`.
- **Vector serialization to sqlite-vec.** `FLOAT[N]` columns are stored as packed little-endian float32 bytes. `VectorBlob.Serialize/Deserialize` in `ChunkRepository.cs` is the only place that does this conversion — keep all sqlite-vec writes/reads going through it.
- **`chunk_embeddings` has no foreign key to `chunks`** because vec0 virtual tables don't support FKs. `ChunkRepository.DeleteChunks` deletes from both tables explicitly inside one transaction. Don't `DELETE FROM chunks` directly without also cleaning `chunk_embeddings`.
- **One row per message** in vector search results. `VectorSearchService` uses a window function (`ROW_NUMBER() OVER ... rn=1`) so the same message can't appear twice from different chunks. Hybrid search relies on this invariant when fusing.
- **RRF k=60** in `HybridSearchService.RrfK` — standard from the literature. Don't tune without benchmarking against a real query set.
- **Subject is prepended to body before embedding** in `EmbeddingWorker.BuildEmbeddingText`. Newsletters often have a meaningful subject and thin body; replies the other way.
- **Empty-body messages get a stamped `embedded_at` with zero chunks** so the worker doesn't loop on them forever. `ChunkRepository.ReplaceChunksForMessage` accepts empty lists for this reason.
- **Content-change detection lives in `MessageRepository.Upsert`.** Each parsed message carries a SHA-256 of its body section (computed by `MessageBodyHasher.Hash` over `MimeMessage.Body.WriteTo` — explicitly excludes the top-level header block so post-delivery rewrites like DKIM-Verified don't trigger spurious re-embeds). On upsert we compare against stored `messages.content_hash` in the same transaction and return `UpsertOutcome { Id, ContentChanged }`. The scanner ([`MaildirScanner.TryIngest`](src/Mailvec.Indexer/Services/MaildirScanner.cs)) calls `ChunkRepository.ClearEmbeddingsForMessage(id)` when `ContentChanged == true`. Keying off Message-ID (not Maildir path) avoids false positives from renames; hashing `MimeMessage.Body` bytes (not `parsed.BodyText`) decouples from HTML-converter changes — those are handled separately by `mailvec rebuild-bodies --reembed`. Legacy NULL hashes are treated as "not changed" so the migration doesn't mass-clear.
- **Chunk size + two layers of overflow handling.** Default `ChunkSizeTokens=200` (chars/4 = 800 char ceiling). The 4-chars/token heuristic is unreliable on real email — punctuation, embedded URLs, base64, marketing-email HTML residue, and non-ASCII can push BPE token counts to 1-2 chars/token, exceeding `mxbai-embed-large`'s 512-token context. Two safety nets:
  1. **Server-side: Ollama's `truncate: true` flag** works for batched `/api/embed` as of Ollama **0.21.2** (this is the floor). The server logs `llm embedding error: the input length exceeds the context length` (INFO-level, not an error response), silently truncates, and returns HTTP 200. Verified empirically by `mailvec audit-embeddings`.
  2. **Client-side fallback in `OllamaClient.EmbedAsync` for HTTP 400**: oversize batches get split in half and recursed; a singleton that still doesn't fit is truncated by 50% repeatedly until it succeeds (down to a 64-char floor). Don't disable this without replacing it with a real tokenizer-based pre-check — it's the safety net if a future Ollama version regresses.

## Phase 3 gotchas

- **Search filters live on the search services in Core, not on the MCP tool.** `KeywordSearchService.Search`, `VectorSearchService.SearchAsync`, `HybridSearchService.SearchAsync`, and `MessageRepository.BrowseByFilters` all accept a `SearchFilters?` (`Folder` / `DateFrom` / `DateTo` / `FromContains` / `FromExact`). The shared `SearchFilterSql.Append` helper appends WHERE clauses and binds params so filter semantics stay identical across the BM25 leg, the vector leg, and the query-less browse path. Hybrid RRF fuses two ranked lists, so post-filtering only one side would skew scores. **Don't** post-filter in the MCP layer. `FromExact` takes precedence over `FromContains` when both are set.
- **Vector KNN runs before the filter join.** `chunk_embeddings MATCH $vec AND k=$k` returns the raw nearest neighbours; the filter is applied in the joined CTE. With a restrictive filter, a small `k` can leave the post-filter set empty even when good matches exist further down the KNN list. `HybridSearchService` inflates `k` to `candidatesPerLeg * 10` (vs `* 2` unfiltered).
- **Date filtering uses `datetime()` for comparison.** Stored `date_sent` is `DateTimeOffset.ToString("O")`, which mixes UTC `Z` and `+HH:mm` offsets. Wrapping both sides in SQLite's `datetime()` normalises them; a raw string compare would silently miss matches across offsets. Messages with NULL `date_sent` are excluded when any date bound is set.
- **MCP runs in two transport modes** sharing the same Core wiring (`AddMailvecServices` helper in `Program.cs`):
  - **HTTP (default)**: `MapMcp()` mounted at `/` over Streamable HTTP. Clients must `initialize` first, capture the `Mcp-Session-Id` response header, send `notifications/initialized`, and include the session header on every subsequent call. Without the session header, calls 404 silently.
  - **Stdio (`--stdio` flag)**: Generic `Host` + `WithStdioServerTransport()`. Used by Claude Desktop because its connector schema only supports stdio. Wired up via `ops/run-mcp-stdio.sh`.
- **In stdio mode, all logging must go to stderr.** Stdout is the JSON-RPC channel; a single byte on stdout corrupts the protocol. `Program.cs` calls `ClearProviders()` and sets `LogToStandardErrorThreshold = LogLevel.Trace`; `SerilogSetup.Configure(..., stdioMode: true)` passes `standardErrorFromLevel: LogEventLevel.Verbose` to the Console sink so even Verbose/Debug events go to stderr. Don't add any stdout writer in `RunStdio`. Don't use `dotnet run` for stdio launching — its build chatter goes to stdout. `ops/run-mcp-stdio.sh` builds quietly to a log file (`$TMPDIR/mailvec-mcp-stdio-build.log`) and execs the compiled DLL directly.
- **Claude Desktop launches MCP servers with a sanitized environment AND a hard read block on `~/Documents`.** Three quirks:
  1. **`~/Documents` is unreadable for content even with Full Disk Access.** Spawned children can `stat()` files but `open()` returns EPERM (likely a per-app `com.apple.macl` ACL). FDA + Documents toggle in System Settings does not fix this. **Workaround**: don't run anything from inside `~/Documents`. The MCP binary is `dotnet publish`-ed to `~/.local/share/mailvec/`; the launcher lives at `~/.local/bin/mailvec-mcp-stdio`. Run `ops/publish-mcp-stdio.sh` after Core/Mcp changes. Diagnostic distinction: `ls -l <file>` succeeding doesn't mean Claude can open the file (`ls` is just `lstat()`); have the diag script `head -1 <file>` and check exit status.
  2. **PATH excludes `/usr/local/share/dotnet`.** Spawned PATH is `~/.nvm/.../bin:/opt/homebrew/bin:~/.local/bin:/usr/bin:/bin:/usr/sbin:/sbin`. `~/.local/bin/mailvec-mcp-stdio` exports `DOTNET_ROOT` and prepends it to PATH so the runtime resolves.
  3. Use `/bin/bash <script>` form in `claude_desktop_config.json` (not the script directly) — avoids depending on the shebang interpreter being on Claude's allowed-exec list. Diagnostic recipe: have the script `echo` to `>&2`, end with `cat >/dev/null` so stdin stays open and logs land in `~/Library/Logs/Claude/mcp-server-mailvec.log` before SIGTERM.
- **The full `McpException` type lives in the `ModelContextProtocol` namespace, not `ModelContextProtocol.Server`** where the `[McpServerTool]` attributes live. Easy to miss — the build error points only at the missing type.
- **MCP and CLI must use identical Core wiring.** `Mailvec.Mcp/Program.cs` mirrors `CliServices` (same singletons, same HttpClient setup for `OllamaClient`). If you add a search-affecting service, register it in both — drift means CLI debugging stops matching MCP behaviour.
- **`get_thread` resolves via thread_id, with a special case for lone messages.** `MessageRepository.GetThreadByMessageId` first looks up the requested message, then queries by its `thread_id`. If `thread_id` is NULL (a message that wasn't part of any reference chain), the method returns just that one message rather than empty. Don't "fix" this to require non-null thread_id — singletons are common (notifications, marketing).
- **Fastmail webmail deep-links are opt-in.** Setting `Fastmail:AccountId` (env: `Fastmail__AccountId`) enables a `webmailUrl` field on `search_emails`, `get_email`, and `get_thread` responses. Current implementation uses Fastmail's `msgid:` search-URL syntax (zero JMAP, zero auth, lands on a search-results pane). The "proper" upgrade (JMAP `Email/query` for direct conversation links) needs a Fastmail API token and two new nullable columns on `messages` to cache results — see `WebmailLinkBuilder`. JMAP Email-ids are stable across mailbox moves (RFC 8621 §4.1.1), so caching is safe. Don't confuse the JMAP Email-id with the IMAP UID baked into Maildir filenames.
- **`SchemaMigrator.EnsureUpToDate()` runs at MCP startup.** If the configured `DatabasePath` doesn't exist, the migrator silently creates a fresh empty schema there. When debugging "no results", first check `mailvec status` to confirm message counts on the same path the MCP server resolved.

## Phase 4 gotchas

- **`/health` is HTTP-mode only.** Stdio has no analog — Claude Desktop sees failures via the `initialize` handshake instead. Endpoint is registered before `MapMcp()` in `Program.cs::RunHttp` and bypasses MCP framing. Logic lives in `Mailvec.Core/Health/HealthService.cs` so the CLI can reuse the snapshot if needed.
- **Returns 503 (not 200) when degraded** so external monitors can alert without parsing the body. "Degraded" today means Ollama unreachable OR `metadata.embedding_model` disagrees with `Ollama:EmbeddingModel`. Don't broaden the degraded set without thinking about false-positive paging cost.
- **Ollama ping is bounded by a 2 s linked-CTS, not the embedder's 60 s timeout.** `OllamaClient.PingAsync` wraps the call in `CancellationTokenSource.CreateLinkedTokenSource(ct)` with `CancelAfter(2s)` and only swallows `OperationCanceledException` when the *outer* token wasn't cancelled. If you raise the cap, don't go above ~5 s.
- **No auth on `/health`.** Local-only by virtue of `Mcp:BindAddress=127.0.0.1`. If the bind address is ever broadened, OAuth scaffolding would go in front of `MapMcp()` *and* `/health`.
- **Log rotation is in-process (Serilog), not external.** All three .NET services wire `SerilogSetup.Configure(builder.Services, builder.Configuration, builder.Logging, "<name>")`. Output: `~/Library/Logs/Mailvec/mailvec-<service>-<YYYYMMDD>.log`, daily rolling, also rolls if a single day exceeds 10 MB, 14 most recent files retained. Serilog's File sink owns the file handle, so rolling is atomic.
- **`MAILVEC_LAUNCHD=1` suppresses Serilog's Console sink.** Set in the launchd plist `EnvironmentVariables`. Without it, every log line writes to both the rolling file and stdout/stderr, where launchd captures it into `StandardOutPath`/`StandardErrorPath` — doubling disk usage. With the env var set, the launchd-captured `<service>.launchd.log` only catches things that bypass `ILogger`: pre-Serilog startup output, unhandled native stderr, panics.
- **The mbsync agent has no Serilog wiring** because mbsync is a non-.NET binary. Its launchd-captured `mailvec-mbsync.{out,err}.log` files grow at most a few lines per 5-minute sync — small enough that not rotating them is the right choice.
- **`PRAGMA journal_mode = WAL` must be applied via `ExecuteScalar`, not `ExecuteNonQuery`.** Microsoft.Data.Sqlite silently no-ops result-returning pragmas under `ExecuteNonQuery`, so the WAL pragma in `001_initial.sql` was *not actually setting WAL mode* — DBs were running in plain rollback-journal `delete` mode. Confirmed by reading header bytes 18-19 (`01 01` for journal, `02 02` for WAL). Fix lives in `ConnectionFactory.Open()`: a per-connection `PRAGMA journal_mode = WAL;` via `ExecuteScalar`, idempotent and self-healing for any DB created before the fix landed. Don't move this back into the migration script.
- **`mailvec checkpoint` runs `PRAGMA wal_checkpoint(TRUNCATE)`** for one-off WAL cleanup after bulk operations. Reports `-wal` file size before and after. TRUNCATE needs an exclusive moment with no readers — if the indexer/embedder/MCP server are all running, the call returns `busy=1` and falls back to a passive checkpoint that flushes pages but doesn't shrink the file. Against a non-WAL DB the pragma returns `(-1, -1, -1)`, so the command short-circuits with a clear "not WAL" message.

## get_attachment

- **Don't ship binary bytes to Claude over MCP.** Current design writes the file to a user-visible directory and returns a text response with the path. Claude Code's built-in `Read` handles PDFs/text/images natively; on Claude.ai or Claude Desktop, a filesystem MCP server (e.g. `@modelcontextprotocol/server-filesystem`) pointed at the download dir picks up the read. (Earlier attempts to send bytes via `EmbeddedResourceBlock` foundered on Claude.ai's bridge mapping every blob to `image` regardless of MIME.)
- **Where to write.** `Mcp:AttachmentDownloadDir` defaults to `~/Downloads/mailvec/`. Avoid `~/Library/Caches/` (hidden) and `~/Documents/` (TCC-blocked).
- **Output filename is `{messageId}-{partIndex}-{sanitized-name}`.** The id+index prefix guarantees no collisions and keeps the originating email greppable.
- **`get_email` advertises the `partIndex` Claude needs.** Don't rename `partIndex` without updating both tools — Claude reads the field name from `get_email`'s output schema and passes it back to `get_attachment`.
- **Filename sanitization + path containment.** `AttachmentExtractor.ResolveSafeFileName` runs `Path.GetFileName` on the claimed filename, which strips any directory component regardless of separator style. `ResolveSafeOutputPath` does a defence-in-depth canonical-path containment check (`Path.GetFullPath` of target inside `Path.GetFullPath` of download dir) and refuses to overwrite an existing symlink at the destination (TOCTOU vector). Don't relax either.
- **Override `application/octet-stream` from the filename extension.** Many mailers attach PDFs / DOCX / images with `Content-Type: application/octet-stream`. `AttachmentExtractor.ResolveContentType` substitutes a real MIME when the declared type is generic and the extension is known. Specific declared MIMEs are preserved.
- **Images and small text-ish files are also inlined as native MCP blocks.** `image/*` → `ImageContentBlock` so Claude vision works in one round trip without a filesystem MCP. `text/*` + a few application MIMEs (json, xml, yaml) under `Mcp:AttachmentInlineTextMaxBytes` → an extra `TextContentBlock` with strict UTF-8 decoding (`UTF8Encoding(throwOnInvalidBytes: true)` so a CSV that claims `text/*` but isn't valid UTF-8 just omits the inline text). The file lands on disk in both cases regardless.
- **`Blob` / `Data` setters take `ReadOnlyMemory<byte>` of the *base64 string's UTF-8 encoding*** (matters for `ImageContentBlock.Data`). Use `Encoding.UTF8.GetBytes(Convert.ToBase64String(rawBytes))`. Don't pass raw bytes (the SDK won't base64-encode for you) and don't pass a `string` (won't compile).
- **Re-extraction is idempotent.** If the target file already exists with matching size, we skip the rewrite and set `WasReused: true`. Hashing would be more rigorous but Maildir parsing is the dominant cost; size is a good-enough fingerprint.
