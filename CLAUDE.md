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

- **Mailvec.Core** — shared library. Domain models, SQLite access (with `sqlite-vec` extension loading), Ollama HTTP client, hybrid (FTS+vector RRF) search logic, and `Options/` POCOs (`ArchiveOptions`, `OllamaOptions`, `IndexerOptions`, `EmbedderOptions`, `McpOptions`). All four executables reference Core.
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

## sqlite-vec — not a NuGet dependency

The `sqlite-vec` extension is fetched as a prebuilt `vec0.dylib` by `ops/fetch-sqlite-vec.sh` and loaded at runtime via `Microsoft.Data.Sqlite`'s extension-loading API. The path comes from `Archive:SqliteVecExtensionPath` in config. There is a NuGet wrapper (`sqlite-vec` 0.1.7-alpha.2.1) but it has been a prerelease for over a year and lags upstream — do not add it back. The fetch script supports `osx-arm64` and `osx-x64`; add other RIDs there if needed.

## Current status

Phases 1 and 2 complete. The indexer ingests, the embedder embeds, keyword/semantic/hybrid search all work. Phase 3 (MCP server) is not yet implemented — `Mailvec.Mcp/Program.cs` is still a scaffold with `/health` only.

## Phase 1 gotchas (worth remembering)

- **MimeKit does not ship `HtmlToText`.** The design doc's reference is wrong. We use `HtmlTokenizer` with a small custom walker (`MessageParser.ConvertHtmlToText`) — quality is "good enough for FTS"; revisit if marketing-email noise hurts embedding quality.
- **`Microsoft.Data.Sqlite.ExecuteNonQuery` silently stops at the first `CREATE TRIGGER ... BEGIN ... END;`.** The internal trigger semicolons confuse its statement iterator. We tokenise scripts ourselves in `SqlScriptSplitter` (BEGIN/END depth-tracking) and execute one statement at a time. Don't replace this with a single multi-statement `ExecuteNonQuery`.
- **`vec0` must load before the schema applies**, because `001_initial.sql` declares `chunk_embeddings` as a `vec0(...)` virtual table. `ConnectionFactory` loads the extension on every `Open()`. The dylib is copied into each project's `bin/.../runtimes/<rid>/native/` by `Directory.Build.props` (with an `Exists` guard, so the build doesn't break if `ops/fetch-sqlite-vec.sh` hasn't run).
- **Rename detection.** When mbsync renames `new/foo` → `cur/foo:2,S` between scans, the same Message-ID appears at a fresh path while the old `sync_state` row still references the old path. The scanner uses `SyncStateRepository.FreshMessageIds(since)` to exclude renamed messages from the soft-delete pass — don't bypass this when changing reconciliation logic.
- **Config env-var convention.** Both indexer and CLI read environment variables with no prefix, so `Archive__MaildirRoot=/path` works for either. The CLI looks for `appsettings.json` in `AppContext.BaseDirectory`, not the current working directory.

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
