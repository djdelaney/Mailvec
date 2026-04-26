# Mailvec

Local-first IMAP archive with keyword (FTS5) and semantic (sqlite-vec) search, exposed to Claude over MCP. Single-account, single-machine, designed to run unattended on a Mac mini. See [`mailvec-project-scope.md`](mailvec-project-scope.md) for the full design.

Sync is done by [`mbsync`](https://isync.sourceforge.io/), so any IMAP server works — Fastmail, iCloud, Gmail (with an app password), self-hosted Dovecot, etc. The shipped `ops/mbsyncrc.example` and reference design use Fastmail because that's the author's setup; swap the `Host` / `User` / `PassCmd` lines and the rest of the pipeline (Maildir → SQLite → embeddings → MCP) is unchanged.

## Layout

```
src/
  Mailvec.Core      shared types, SQLite + Ollama clients, hybrid search
  Mailvec.Indexer   BackgroundService: Maildir -> SQLite (no embeddings)
  Mailvec.Embedder  BackgroundService: SQLite rows -> Ollama embeddings
  Mailvec.Mcp       AspNetCore MCP server (HTTP, localhost:3333)
  Mailvec.Cli       admin commands (status, search, reindex, rebuild-fts)
tests/
  Mailvec.{Core,Indexer,Mcp}.Tests
schema/
  001_initial.sql   tables, FTS5 triggers, vec0 vector index
ops/
  mbsyncrc.example  IMAP sync config template
  launchd/          plist templates for mbsync + 3 .NET services
  install.sh        Phase 4 installer (stub)
runtimes/
  osx-arm64/native  sqlite-vec native binary lands here
```

## Build

Requires .NET 10 SDK.

```sh
./ops/fetch-sqlite-vec.sh   # downloads vec0.dylib (run once)
dotnet build
dotnet test
```

The `sqlite-vec` extension is loaded at runtime from `runtimes/<rid>/native/vec0.dylib`. The fetch script grabs the latest release from the upstream GitHub repo; pin a version with `SQLITE_VEC_VERSION=0.1.9 ./ops/fetch-sqlite-vec.sh`.

Central package management lives in `Directory.Packages.props`. Shared MSBuild settings (target framework, nullable, warnings-as-errors) live in `Directory.Build.props`.

## Status

Phase 0 scaffold only. No working ingest, embedding, or MCP server yet — see the phased build plan in the design doc.
