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

Phase 1 complete: schema migrations, MimeKit-based parser, `MaildirScanner` + `MaildirWatcher` + `MessageIngestService`, FTS5/BM25 keyword search, and a working `mailvec status | search | rebuild-fts` CLI. Phases 2 (Ollama embeddings) and 3 (MCP server) are not yet implemented — `Mailvec.Embedder/Program.cs` and `Mailvec.Mcp/Program.cs` are still scaffolds.

## Phase 1 gotchas (worth remembering)

- **MimeKit does not ship `HtmlToText`.** The design doc's reference is wrong. We use `HtmlTokenizer` with a small custom walker (`MessageParser.ConvertHtmlToText`) — quality is "good enough for FTS"; revisit if marketing-email noise hurts embedding quality.
- **`Microsoft.Data.Sqlite.ExecuteNonQuery` silently stops at the first `CREATE TRIGGER ... BEGIN ... END;`.** The internal trigger semicolons confuse its statement iterator. We tokenise scripts ourselves in `SqlScriptSplitter` (BEGIN/END depth-tracking) and execute one statement at a time. Don't replace this with a single multi-statement `ExecuteNonQuery`.
- **`vec0` must load before the schema applies**, because `001_initial.sql` declares `chunk_embeddings` as a `vec0(...)` virtual table. `ConnectionFactory` loads the extension on every `Open()`. The dylib is copied into each project's `bin/.../runtimes/<rid>/native/` by `Directory.Build.props` (with an `Exists` guard, so the build doesn't break if `ops/fetch-sqlite-vec.sh` hasn't run).
- **Rename detection.** When mbsync renames `new/foo` → `cur/foo:2,S` between scans, the same Message-ID appears at a fresh path while the old `sync_state` row still references the old path. The scanner uses `SyncStateRepository.FreshMessageIds(since)` to exclude renamed messages from the soft-delete pass — don't bypass this when changing reconciliation logic.
- **Config env-var convention.** Both indexer and CLI read environment variables with no prefix, so `Archive__MaildirRoot=/path` works for either. The CLI looks for `appsettings.json` in `AppContext.BaseDirectory`, not the current working directory.
