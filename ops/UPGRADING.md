# UPGRADING

Operator notes for bumping pinned versions. Covers NuGet packages, the .NET SDK, the sqlite-vec dylib, SQLite, and the Ollama floor.

## NuGet packages

Bump versions in `Directory.Packages.props` only — csproj files reference packages without versions (CPM is on). After a bump:

```sh
dotnet restore && dotnet build
```

`TreatWarningsAsErrors=true` means any new deprecation surfaced by a transitive will fail the build, so fix warnings rather than `<NoWarn>`-suppressing them.

**Cluster-pin invariants** (skew between members produces obscure DI/binding errors at runtime, not at build):

- The `Microsoft.Extensions.*` cluster (Hosting, Configuration.Binder, Options, Logging.*, Http) moves together.
- The Serilog cluster (Serilog + Sinks.* + Settings.Configuration + Extensions.Hosting) moves together.
- `ModelContextProtocol` and `ModelContextProtocol.AspNetCore` must stay on the same version; the SDK assumes pinned-pair semantics.
- `MimeKit` and `AngleSharp` are independent — bump on their own cadence.

## .NET SDK / runtime

`net10.0` is declared once in `Directory.Build.props`; that's the only place to change it. There is no `global.json`, so the SDK floats to whatever's installed locally.

A TFM bump (e.g. `net10.0` → `net11.0`) has fanout:

1. Install the matching SDK on the build host — `ops/build-mcpb.sh` runs `dotnet publish -c Release -r osx-arm64 --self-contained true` and ships the runtime inside the bundle.
2. Re-run `ops/publish-mcp-stdio.sh` so the stdio launcher at `~/.local/bin/mailvec-mcp-stdio` (which exports `DOTNET_ROOT`) resolves to a runtime that exists.
3. Rebuild + reinstall the MCPB with `--bump` so end-users' Claude Desktop instances pick up the new self-contained runtime.

Bundle size grows roughly with each runtime major.

## sqlite-vec dylib

Pinned by the `VERSION="..."` default in `ops/fetch-sqlite-vec.sh` (today `0.1.9`). Bump by editing the script. For one-off testing: `SQLITE_VEC_VERSION=x.y.z ./ops/fetch-sqlite-vec.sh`.

After fetching, run a semantic search against the existing DB before committing — vec0's stored format has been stable across 0.1.x but a breaking change would silently corrupt similarity scores rather than fail loudly.

The dylib is also bundled into `dist/mailvec-<version>.mcpb` via `Directory.Build.props`'s `<None>` copy, so a dylib bump requires `ops/build-mcpb.sh --bump` to ship to Claude Desktop users.

**Don't switch to the NuGet wrapper** (`sqlite-vec` 0.1.7-alpha.2.1, prerelease for over a year, lags upstream).

## SQLite itself

Ships inside `SQLitePCLRaw.bundle_e_sqlite3` — bump the bundle to bump SQLite. WAL mode and FTS5 syntax are stable across SQLite versions. On a major bump, verify:

- `vec0` still loads (a startup failure shows up immediately as `mailvec status` erroring on connection open).
- `PRAGMA journal_mode = WAL` via `ExecuteScalar` still flips the file: read header bytes 18-19 — `02 02` for WAL, `01 01` for journal.

## Ollama and the embedding model

- **Install the cask, not the formula.** Use `brew install --cask ollama-app` (Ollama's own prebuilt app), not `brew install ollama`. The Homebrew *formula* bottle has shipped incomplete on arm64 — bundling only the MLX runner and no `llama-server`, so GGML models like `mxbai-embed-large` fail with `llama-server binary not found`. Symptom: Ollama answers `GET /api/tags` (200) but every `/api/embed` hangs to timeout, so the embedder stalls while liveness checks look green. Mailvec's readiness probe (`OllamaClient.PingAsync` does a real embed, not a `/api/tags` ping) catches this and flips `/health` to degraded. If a `brew upgrade` ever pulls a broken formula bottle: `brew services stop ollama && brew uninstall ollama`, then install the cask. Verify a fix with `curl -s http://localhost:11434/api/embed -d '{"model":"mxbai-embed-large","input":"hi"}'` — it must return a vector, not hang.
- **Ollama floor:** ≥ 0.21.2, for `truncate: true` on batched `/api/embed`. Older versions ignore the flag and produce HTTP 400s on overflow, which is when the client-side fallback in `OllamaClient.EmbedAsync` kicks in (see Phase 2 gotchas in `CLAUDE.md`).
- **Embedding model:** schema-coupled to a fixed `FLOAT[1024]` dimension. Switching it requires a schema edit + full reindex (`mailvec reindex --all` followed by re-embedding). See `Data model invariants` in `CLAUDE.md`.
