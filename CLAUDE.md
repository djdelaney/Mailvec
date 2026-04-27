# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Source of truth

`mailvec-project-scope.md` is the design document and the canonical reference for goals, non-goals, schema, and the phased build plan. Read it before making non-trivial changes — Phase 0 (current state) is a skeleton, and the doc describes what each project is *meant* to do once filled in.

## Common commands

```sh
./ops/fetch-sqlite-vec.sh    # one-time: downloads vec0.dylib into runtimes/<rid>/native/
dotnet build                 # builds via Mailvec.slnx (the .NET 10 XML solution format)
dotnet test                  # runs all xUnit projects
dotnet test tests/Mailvec.Core.Tests --filter "FullyQualifiedName~MaildirScanner"   # single test class
dotnet run --project src/Mailvec.Indexer
dotnet run --project src/Mailvec.Mcp
dotnet run --project src/Mailvec.Cli -- <args>
```

`Mailvec.slnx` (not `.sln`) is the solution file — .NET 10 emits the new XML format by default. Tooling treats it equivalently.

## Build conventions

- **Central Package Management is on.** All NuGet versions live in `Directory.Packages.props`. csproj files use `<PackageReference Include="..." />` *without* a `Version=` attribute. Adding a new dependency means editing both files.
- **`TreatWarningsAsErrors=true`** in `Directory.Build.props` — a build warning fails the build. Unused imports / nullable issues will block CI-equivalent local builds.
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
- **Mailvec.Mcp** — AspNetCore app exposing MCP tools over HTTP on `127.0.0.1:3333`. Read-only against the database.
- **Mailvec.Cli** — admin commands (status, search, reindex, rebuild-fts) hitting the same DB.

## Data model invariants

`schema/001_initial.sql` is the full schema. Two non-obvious constraints:

1. **The embedding model is part of the schema.** `chunk_embeddings` is declared with a fixed dimension (`FLOAT[1024]` for `mxbai-embed-large`). Mixing vectors from different models silently produces garbage. The `metadata` table records the model + dimensions; the embedder must refuse to start if `Ollama:EmbeddingModel` disagrees, and switching models requires a full reindex.
2. **FTS5 sync is trigger-driven.** `messages_ai`/`messages_ad`/`messages_au` keep `messages_fts` in lockstep with `messages`. Don't bypass the triggers with raw inserts into `messages_fts`.

## Configuration

Each runnable service has its own `appsettings.json` containing only the sections it needs. Configuration POCOs live in `Mailvec.Core/Options/` and are bound in each service's `Program.cs`. Local overrides go in `appsettings.Local.json` (gitignored).

`ArchiveOptions` (`DatabasePath`, `SqliteVecExtensionPath`) is genuinely shared — both fields are consumed by `ConnectionFactory`, so all four executables bind it. `IngestOptions` (`MaildirRoot`) is bound only by the Indexer (which scans the Maildir) and the CLI (which prints the path in `mailvec status`). The Embedder and MCP server never read the filesystem and do not bind it — keep this isolation when adding new options, the MCP-bundle install flow doesn't need to prompt for a Maildir path the server never reads.

## sqlite-vec — not a NuGet dependency

The `sqlite-vec` extension is fetched as a prebuilt `vec0.dylib` by `ops/fetch-sqlite-vec.sh` and loaded at runtime via `Microsoft.Data.Sqlite`'s extension-loading API. The path comes from `Archive:SqliteVecExtensionPath` in config. There is a NuGet wrapper (`sqlite-vec` 0.1.7-alpha.2.1) but it has been a prerelease for over a year and lags upstream — do not add it back. The fetch script supports `osx-arm64` and `osx-x64`; add other RIDs there if needed.

## Current status

Phases 1, 2, and 3 complete. The indexer ingests, the embedder embeds, keyword/semantic/hybrid search all work, and the MCP server exposes four tools — `search_emails`, `get_email`, `get_thread`, `list_folders` — over both Streamable HTTP (`127.0.0.1:3333`, default) and stdio (`--stdio` flag). **Claude Desktop integration ships as an MCPB bundle** built by `ops/build-mcpb.sh`; the older `~/.local/bin/mailvec-mcp-stdio` launcher still works but is superseded (the gotcha notes below are kept for reference and for the HTTP transport / smoke-test path). Phase 4 (launchd plists, install.sh, log rotation) hasn't started yet.

The doc's original 6-tool list got merged to 4: `recent_emails` is `search_emails` with `query` omitted (date-sorted browse path via `MessageRepository.BrowseByFilters`), and `find_by_sender` is `search_emails` with `fromExact: "..."` (exact-match alternative to the existing `fromContains` substring filter). One tool with sharper semantics beats two overlapping ones.

## MCPB bundle for Claude Desktop

`ops/build-mcpb.sh` produces `dist/mailvec-<version>.mcpb`. It runs `dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=false`, copies `manifest.json` next to the published `server/` directory, and zips the result. The bundle extracts to `~/Library/Application Support/Claude/extensions/<id>/`, which is *not* under `~/Documents` and so sidesteps the TCC read block documented in the Phase 3 gotchas below.

- **`manifest.json` user_config defaults must use `~/...`, not `${HOME}/...`.** Claude Desktop's MCPB host substitutes its own `${user_config.X}` tokens but passes shell-style `${HOME}` through verbatim. Learned the hard way during install: a default of `${HOME}/Library/...` made `Path.GetDirectoryName` produce `/${HOME}` and the migrator tried to mkdir at the filesystem root, crashing during DI construction. `PathExpansion.Expand` was hardened to also handle `${HOME}` / `$HOME` defensively in case any future host repeats the mistake; tests are in `PathExpansionTests.cs`.
- **Self-contained, NOT single-file.** `PublishSingleFile=true` would still leave `vec0.dylib` outside the apphost (it's added via `<None CopyToOutputDirectory>` not as a managed dep), but turning it off keeps the layout debuggable: `server/Mailvec.Mcp` plus `server/runtimes/osx-arm64/native/vec0.dylib` are visibly co-located, and `ConnectionFactory.ResolveVecExtension` resolves the relative path against `AppContext.BaseDirectory` exactly as it does in dev builds. Single-file would also make `xattr`/Gatekeeper triage harder.
- **Bundle size is ~50 MB.** The .NET 10 runtime is the bulk. Fine for personal install. Don't switch to framework-dependent — that brings back the `DOTNET_ROOT` / PATH problem the bundle was built to eliminate.
- **The bundle is the read-side only.** Indexer + embedder still run as your own processes against the same DB. Updating the bundle does not require restarting them.
- **Updating an installed bundle:** `ops/build-mcpb.sh --bump` patch-bumps `manifest.json`, rebuilds, and `open`s the new `.mcpb` (which Claude Desktop intercepts as an install prompt). Then in Settings → Extensions toggle Mailvec off and confirm the install, quit + relaunch. Toggling off (vs uninstalling) preserves user_config values across upgrades. Without a version bump, Claude Desktop silently ignores the re-install — so plain `build-mcpb.sh` is fine for "rebuild and inspect locally" but `--bump` is what you need to actually swap the running binary.
- **First place to look when something's wrong:** `~/Library/Logs/Claude/mcp-server-mailvec.log`. The stdio binary's stderr lands there; Claude Desktop's own "Server disconnected" toast tells you nothing useful.

## Phase 1 gotchas (worth remembering)

- **MimeKit does not ship `HtmlToText`.** The design doc's reference is wrong. We use `HtmlTokenizer` with a small custom walker (`MessageParser.ConvertHtmlToText`) — quality is "good enough for FTS"; revisit if marketing-email noise hurts embedding quality.
- **`Microsoft.Data.Sqlite.ExecuteNonQuery` silently stops at the first `CREATE TRIGGER ... BEGIN ... END;`.** The internal trigger semicolons confuse its statement iterator. We tokenise scripts ourselves in `SqlScriptSplitter` (BEGIN/END depth-tracking) and execute one statement at a time. Don't replace this with a single multi-statement `ExecuteNonQuery`.
- **`vec0` must load before the schema applies**, because `001_initial.sql` declares `chunk_embeddings` as a `vec0(...)` virtual table. `ConnectionFactory` loads the extension on every `Open()`. The dylib is copied into each project's `bin/.../runtimes/<rid>/native/` by `Directory.Build.props` (with an `Exists` guard, so the build doesn't break if `ops/fetch-sqlite-vec.sh` hasn't run).
- **Rename detection.** When mbsync renames `new/foo` → `cur/foo:2,S` between scans, the same Message-ID appears at a fresh path while the old `sync_state` row still references the old path. The scanner uses `SyncStateRepository.FreshMessageIds(since)` to exclude renamed messages from the soft-delete pass — don't bypass this when changing reconciliation logic.
- **Config env-var convention.** Both indexer and CLI read environment variables with no prefix, so `Ingest__MaildirRoot=/path` works for either. The CLI looks for `appsettings.json` in `AppContext.BaseDirectory`, not the current working directory.
- **`MaildirRoot` lives on `IngestOptions`, not `ArchiveOptions`.** Originally on the shared `ArchiveOptions` POCO, but only the Indexer (scanner + watcher) actually reads from the Maildir filesystem; the Embedder and MCP server are pure-SQLite. Carrying the field on the shared POCO leaked into the MCPB manifest as a required user_config prompt the server never used. Split out into `Mailvec.Core/Options/IngestOptions.cs` (section name `Ingest`) and bound only by `Mailvec.Indexer/Program.cs` and `Mailvec.Cli/Commands/CliServices.cs`. **Breaking env-var rename:** `Archive__MaildirRoot` → `Ingest__MaildirRoot` for the indexer and CLI. Anyone with shell snippets / launchd plists / docker envs from before the split needs to update.

## Phase 2 gotchas

- **`ChunkingService` lives in `Mailvec.Core/Embedding/`, not `Mailvec.Embedder/Services/`** as the design doc shows. It's pure logic with no I/O; keeping it in Core lets us test it from `Mailvec.Core.Tests` without spinning up an `Embedder.Tests` project.
- **Embedding model is part of the schema, enforced at runtime.** The `chunk_embeddings` virtual table declares a fixed `FLOAT[1024]` dimension; `OllamaClient` validates returned vector lengths against `Ollama:EmbeddingDimensions`; `EmbeddingWorker` refuses to start if `metadata.embedding_model` disagrees with config. Switching models requires `mailvec reindex --all` followed by re-embedding. **Never bypass this check** — mixing vector spaces silently corrupts similarity scores in ways that look plausible.
- **Vector serialization to sqlite-vec.** `FLOAT[N]` columns are stored as packed little-endian float32 bytes, not BLOBs of any other shape. `VectorBlob.Serialize/Deserialize` in `ChunkRepository.cs` is the only place that does this conversion — keep all sqlite-vec writes/reads going through it.
- **`chunk_embeddings` has no foreign key to `chunks`** because vec0 virtual tables don't support FKs. `ChunkRepository.DeleteChunks` deletes from both tables explicitly inside one transaction. Don't `DELETE FROM chunks` directly without also cleaning `chunk_embeddings`.
- **One row per message** in vector search results. `VectorSearchService` uses a window function (`ROW_NUMBER() OVER ... rn=1`) so the same message can't appear twice from different chunks. Hybrid search relies on this invariant when fusing.
- **RRF k=60** in `HybridSearchService.RrfK` — standard from the literature. Don't tune without benchmarking against a real query set.
- **Subject is prepended to body before embedding** in `EmbeddingWorker.BuildEmbeddingText`. Newsletters often have a meaningful subject and thin body; replies the other way. Including both is robust.
- **Empty-body messages get a stamped `embedded_at` with zero chunks** so the worker doesn't loop on them forever. `ChunkRepository.ReplaceChunksForMessage` accepts empty lists for this reason.
- **Chunk size + two layers of overflow handling.** Default `ChunkSizeTokens=200` (chars/4 = 800 char ceiling). The 4-chars/token heuristic is unreliable on real email — heavy punctuation, embedded URLs, base64, marketing-email HTML residue, and non-ASCII can push real BPE token counts to 1-2 chars/token, exceeding `mxbai-embed-large`'s 512-token context. Two things keep us safe:
  1. **Server-side: Ollama's `truncate: true` flag works for batched `/api/embed` as of Ollama 0.21.2.** When an input exceeds the context window, the server logs `llm embedding error: the input length exceeds the context length` (INFO-level — not an error response), silently truncates, and returns HTTP 200 with a valid embedding. Verified empirically by `mailvec audit-embeddings` finding 0/1427 zero/NaN/abnormal-norm vectors after a run with these log lines. Earlier Ollama versions ignored the flag for batched embed, which is why we still keep layer 2.
  2. **Client-side fallback in `OllamaClient.EmbedAsync` for HTTP 400 responses**: oversize batches get split in half and recursed; a singleton that still doesn't fit is truncated by 50% repeatedly until it succeeds (down to a 64-char floor). Test coverage is in `OllamaClientTests`. Don't disable this fallback without replacing it with a real tokenizer-based pre-check — it's the safety net if a future Ollama version regresses, or you switch to a backend that returns 400 on overflow.

## Phase 3 gotchas

- **Search filters live on the search services in Core, not on the MCP tool.** `KeywordSearchService.Search`, `VectorSearchService.SearchAsync`, `HybridSearchService.SearchAsync`, and `MessageRepository.BrowseByFilters` all accept a `SearchFilters?` (`Folder` / `DateFrom` / `DateTo` / `FromContains` / `FromExact`). The shared `SearchFilterSql.Append` helper appends WHERE clauses and binds the params so filter semantics stay identical across the BM25 leg, the vector leg, and the query-less browse path. Important because hybrid RRF fuses two ranked lists, and post-filtering only one side would skew scores. **Don't** post-filter results in the MCP layer. `FromExact` takes precedence over `FromContains` when both are set — exact match is strictly narrower and Claude can pass either depending on what it knows.
- **Vector KNN runs before the filter join.** `chunk_embeddings MATCH $vec AND k=$k` returns the raw nearest neighbours; the filter is applied in the joined CTE. With a restrictive filter, a small `k` can leave the post-filter set empty even when good matches exist further down the KNN list. `HybridSearchService` inflates `k` to `candidatesPerLeg * 10` (vs `* 2` unfiltered) to compensate. Mirror this if you add filtered vector searches elsewhere.
- **Date filtering uses `datetime()` for comparison.** Stored `date_sent` is `DateTimeOffset.ToString("O")`, which mixes UTC `Z` and `+HH:mm` offsets. Wrapping both sides in SQLite's `datetime()` normalises them; a raw string compare would silently miss matches across offsets. Messages with NULL `date_sent` are excluded when any date bound is set.
- **MCP runs in two transport modes** sharing the same Core wiring (`AddMailvecServices` helper in `Program.cs`):
  - **HTTP (default)**: `MapMcp()` mounted at `/` over Streamable HTTP. Clients must `initialize` first, capture the `Mcp-Session-Id` response header, send `notifications/initialized`, and include the session header on every subsequent call. Without the session header, calls 404 silently. Used for Claude Code, our smoke tests, and (eventually) Tailscale-fronted Claude.ai.
  - **Stdio (`--stdio` flag)**: Generic `Host` + `WithStdioServerTransport()`. Used by Claude Desktop because its Custom Connectors GUI requires HTTPS (which we can't provide locally without a real cert) and its `claude_desktop_config.json` schema only supports stdio (`command`+`args`). Wired up via `ops/run-mcp-stdio.sh`.
- **In stdio mode, ALL logging MUST go to stderr.** Stdout is the JSON-RPC channel; a single log line on stdout corrupts the protocol. `Program.cs` sets `LogToStandardErrorThreshold = LogLevel.Trace` and `ClearProviders()` first to nuke any default stdout logger. Don't add any provider that defaults to stdout. Don't use `dotnet run` for stdio launching — its build chatter goes to stdout. `ops/run-mcp-stdio.sh` builds quietly to a log file (`$TMPDIR/mailvec-mcp-stdio-build.log`) and execs the compiled DLL directly.
- **Claude Desktop launches MCP servers with a sanitized environment AND a hard read block on `~/Documents`.** Three specific gotchas hit during Phase 3 sub-step 2 wiring:
  1. **`~/Documents` is unreadable for content even with Full Disk Access.** Claude Desktop's spawned children can `stat()` files under `~/Documents` but `open()` returns EPERM ("Operation not permitted"). FDA + Documents toggle in System Settings did *not* fix this in our testing — likely a per-app `com.apple.macl` ACL on the directory. **Workaround**: don't run anything from inside `~/Documents`. The MCP binary is `dotnet publish`-ed to `~/.local/share/mailvec/` (a non-TCC location) and the launcher lives at `~/.local/bin/mailvec-mcp-stdio`. Source stays in the repo for development; run `ops/publish-mcp-stdio.sh` after any Core/Mcp change to refresh the published binary, then restart Claude Desktop. **Diagnostic distinction**: `ls -l <file>` succeeding doesn't mean Claude can open the file — `ls` is just `lstat()`. To test true readability, have the diag script `head -1 <file>` and check exit status.
  2. **PATH excludes `/usr/local/share/dotnet`.** Claude's spawned PATH is `~/.nvm/.../bin:/opt/homebrew/bin:~/.local/bin:/usr/bin:/bin:/usr/sbin:/sbin` — no `/usr/local/share/dotnet`, where the official .NET macOS installer lives. `~/.local/bin/mailvec-mcp-stdio` exports `DOTNET_ROOT` and prepends it to PATH so the runtime resolves. If you switch runtimes, do the equivalent for it.
  3. Use the `/bin/bash <script>` form in `claude_desktop_config.json` (not the script as `command` directly). It avoids depending on the shebang interpreter being on Claude's allowed-exec list. Diagnostic recipe: have the script `echo` info to `>&2`, end with `cat >/dev/null` so stdin stays open and logs land in `~/Library/Logs/Claude/mcp-server-mailvec.log` before Claude SIGTERMs.
- **The full `McpException` type lives in the `ModelContextProtocol` namespace, not `ModelContextProtocol.Server`** where the `[McpServerTool]` attributes live. Easy to miss — the build error points only at the missing type.
- **MCP and CLI must use identical Core wiring.** `Mailvec.Mcp/Program.cs` mirrors `CliServices` (same singletons, same HttpClient setup for `OllamaClient`). If you add a search-affecting service, register it in both — drift here means CLI debugging stops matching MCP behaviour.
- **`get_thread` resolves via thread_id, with a special case for lone messages.** `MessageRepository.GetThreadByMessageId` first looks up the requested message, then queries by its `thread_id`. If `thread_id` is NULL (a message that wasn't part of any reference chain), the method returns just that one message rather than empty. Don't "fix" this to require non-null thread_id — singletons are common (notifications, marketing) and the tool should still work for them.
- **Fastmail webmail deep-links are opt-in.** Setting `Fastmail:AccountId` (env: `Fastmail__AccountId`) enables a `webmailUrl` field on `search_emails`, `get_email`, and `get_thread` responses (and the equivalent CLI line). The current implementation uses Fastmail's `msgid:` search-URL syntax — zero JMAP calls, zero auth, but the user lands on a search-results pane and clicks once more to open the conversation. The "proper" upgrade (JMAP `Email/query` to resolve RFC Message-ID → JMAP Email-id and emit a direct `mail/Inbox/<thread>.<email>?u=...` link) needs a Fastmail API token and two new nullable columns on `messages` to cache results — see `WebmailLinkBuilder` for where to swap the URL shape. JMAP Email-ids are stable across mailbox moves (RFC 8621 §4.1.1), so caching is safe forever once resolved. **Don't** confuse the JMAP Email-id with the IMAP UID baked into Maildir filenames — they're different IDs in different namespaces.
- **`SchemaMigrator.EnsureUpToDate()` runs at MCP startup.** If the configured `DatabasePath` doesn't exist, the migrator silently creates a fresh empty schema there. Caught me during smoke testing — search returned zero results because the DB had been recreated empty rather than pointing at the populated one. When debugging "no results", first check `mailvec status` to confirm message counts on the same path the MCP server resolved.
