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

Phase 0 scaffold only. `Program.cs` files in each service wire up the host and bind options but have `TODO Phase N:` markers where real services will register. The schema, ops files, and configuration shape are real — the runtime behavior is not. When implementing a phase, check the design doc's "Phased build plan" section for the intended order and exit criteria.
