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
- **The MCP SDK sets a floor on the `Microsoft.Extensions.*` cluster, so the two clusters move together.** MCP 2.0.0 depends on `Microsoft.Extensions.*` / `System.*` at `10.0.10`; bumping MCP alone against a `10.0.9` cluster fails restore with `NU1109` (package downgrade) from an unrelated project — the error names `Mailvec.Cli.Tests` and a transitive `Diagnostics.Abstractions` chain, which points nowhere near the MCP bump that caused it. Bump both in the same commit.
- **MCP 2.0.0 adds `Microsoft.Extensions.AI.Abstractions` to the graph** (a transitive of `ModelContextProtocol.Core`). Nothing references it directly; it matters only because it ships inside the self-contained MCPB bundle.
- `MimeKit` and `AngleSharp` are independent — bump on their own cadence.

### MCP SDK majors

A major MCP SDK bump can change wire behaviour without changing a line of our code — verify the transport, not just the build. Going 1.4.0 → 2.0.0 flipped `HttpServerTransportOptions.Stateless` to true by default (see CLAUDE.md "MCP transport quirks"); we take that default. Post-bump check, with the server running:

```sh
# Sessionless call — must return results with no initialize and no session header.
curl -s -X POST http://127.0.0.1:3333/ -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'

# Down-level handshake — must still return serverInfo + instructions.
curl -s -X POST http://127.0.0.1:3333/ -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":2,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}'
```

Both must pass: the first is what current-revision clients do, the second is what the older Claude surfaces still do, and the SDK serves both off one binary. A `tools/call` that exercises the hybrid (Ollama) path is worth adding — the tool surface can look healthy in `tools/list` while embedding is broken.

#### Flipping stateful → stateless breaks connections that are already open

**Every client connected at the moment of the switch keeps failing until it reconnects.** A client that handshook with the *old* stateful server was issued an `Mcp-Session-Id` and keeps sending it on every subsequent call; the stateless server rejects that header outright:

```
-32000 Bad Request: The Mcp-Session-Id header is not supported in stateless mode
```

This does **not** self-heal within the connection. The SDK returns a hard JSON-RPC error rather than a re-initialize nudge, so the client has no protocol-level signal to drop the now-invalid session and start over — it just keeps replaying the dead header. The fix is entirely client-side: reconnect the connector (reload it in the client's settings, or restart the session). Newly opened connections were never affected — they open against the stateless server, are issued no session id, and therefore send no session header.

Observed for real on the 0.1.33 → 0.1.34 deploy (2026-07-30): one Claude connector that had been connected across the upgrade failed every call with the above, while a second connector entry against the same server worked normally — which is what isolates it to a stale client session rather than a server or protocol fault.

Practical consequences when planning the deploy:

- **The server is fine.** Don't debug the deployment on this symptom. The error text names the cause exactly, and `/health` plus a fresh client will both be green while an old connection is still failing.
- **Warn anyone using a shared remote deployment**, since their connectors break at a moment they didn't choose. A single-user homelab is a "reconnect and move on"; a shared one is a support ticket per user.
- It applies to the reverse flip too (stateless → stateful): a 2026-07-28 client sending a session id to a stateful server gets `-32022 UnsupportedProtocolVersion`. Treat any transport-mode change as client-affecting, not purely server-side.

## .NET SDK / runtime

`net10.0` is declared once in `Directory.Build.props`; that's the only place to change it. There is no `global.json`, so the SDK floats to whatever's installed locally.

A TFM bump (e.g. `net10.0` → `net11.0`) has fanout:

1. Install the matching SDK on the build host — `ops/build-mcpb.sh` runs `dotnet publish -c Release -r osx-arm64 --self-contained true` and ships the runtime inside the bundle.
2. Re-run `ops/install-stdio-launcher.sh` so the stdio launcher at `~/.local/bin/mailvec-mcp-stdio` (which exports `DOTNET_ROOT`) resolves to a runtime that exists. (It republishes AND re-signs; `ops/publish-mcp-stdio.sh` alone also works now that it signs, but doesn't rewrite the launcher.)
3. Rebuild + reinstall the MCPB with `--bump` so end-users' Claude Desktop instances pick up the new self-contained runtime.

Bundle size grows roughly with each runtime major.

## sqlite-vec dylib

Pinned by the `VERSION="..."` default in `ops/fetch-sqlite-vec.sh` (today `0.1.9`). Bump by editing the script. For one-off testing: `SQLITE_VEC_VERSION=x.y.z ./ops/fetch-sqlite-vec.sh`.

After fetching, run a semantic search against the existing DB before committing — vec0's stored format has been stable across 0.1.x but a breaking change would silently corrupt similarity scores rather than fail loudly.

The dylib is also bundled into `dist/mailvec-<version>.mcpb` via `Directory.Build.props`'s `<None>` copy, so a dylib bump requires `ops/build-mcpb.sh --bump` to ship to Claude Desktop users.

**Don't switch to the NuGet wrapper** (`sqlite-vec` 0.1.7-alpha.2.1, prerelease for over a year, lags upstream).

## PDF rendering (PDFtoImage / PDFium)

The `get_attachment_page_image` MCP tool rasterises PDF pages via `PDFtoImage` (pinned in `Directory.Packages.props`), which wraps Google's PDFium plus SkiaSharp — both **native**. Unlike `vec0.dylib` these arrive through NuGet, not a fetch script: bumping `PDFtoImage` pulls matching `bblanchon.PDFium.*` and `SkiaSharp.NativeAssets.*` transitively for `osx-arm64` / `osx-x64` / `linux-x64` / `linux-arm64`.

- The `PackageReference` lives in the `Mailvec.Pdf` wrapper project, which only `Mailvec.Mcp` (`get_attachment_page_image`, `view_attachment` image normalisation) and `Mailvec.Embedder` (the scanned-PDF / image OCR pass) reference — keep Core / Indexer / Cli native-dep-free. The MCP test project also references `SkiaSharp` directly to decode rendered images.
- After a bump: `dotnet test tests/Mailvec.Mcp.Tests` — the page-image tests do a real PDFium render, so they fail loudly if the native lib doesn't load on the current RID. Then rebuild the MCPB; the natives ship inside the self-contained bundle and grow it by a few MB.
- The Linux assets come via `SkiaSharp.NativeAssets.Linux.NoDependencies` (transitive), so no system `fontconfig`/`freetype` would be needed on a headless box. (Load-bearing for the Docker/Linux deployment — `ops/fetch-sqlite-vec.sh` fetches a Linux `vec0.so` for `linux-x64`/`linux-arm64`, and `docs/deploy-docker.md` is the non-launchd service story.) Don't add the plain `SkiaSharp.NativeAssets.Linux` (it pulls those system deps) and keep its version matched to the resolved `SkiaSharp`.
- `PdfRenderer` is `[SupportedOSPlatform]`-gated to macOS/Linux/Windows; if a bump changes those annotations, the build fails with CA1416 (warnings-as-errors).

## SQLite itself

Ships inside `SQLitePCLRaw.bundle_e_sqlite3` — bump the bundle to bump SQLite. WAL mode and FTS5 syntax are stable across SQLite versions. On a major bump, verify:

- `vec0` still loads (a startup failure shows up immediately as `mailvec status` erroring on connection open).
- `PRAGMA journal_mode = WAL` via `ExecuteScalar` still flips the file: read header bytes 18-19 — `02 02` for WAL, `01 01` for journal.

## Ollama and the embedding model

- **Install the cask, not the formula.** Use `brew install --cask ollama-app` (Ollama's own prebuilt app), not `brew install ollama`. The Homebrew *formula* bottle has shipped incomplete on arm64 — bundling only the MLX runner and no `llama-server`, so GGML models like `mxbai-embed-large` fail with `llama-server binary not found`. Symptom: Ollama answers `GET /api/tags` (200) but every `/api/embed` hangs to timeout, so the embedder stalls while liveness checks look green. Mailvec's readiness probe (`OllamaClient.PingAsync` does a real embed, not a `/api/tags` ping) catches this and flips `/health` to degraded. If a `brew upgrade` ever pulls a broken formula bottle: `brew services stop ollama && brew uninstall ollama`, then install the cask. Verify a fix with `curl -s http://localhost:11434/api/embed -d '{"model":"mxbai-embed-large","input":"hi"}'` — it must return a vector, not hang.
- **Ollama floor:** ≥ 0.21.2, for `truncate: true` on batched `/api/embed`. Older versions ignore the flag and produce HTTP 400s on overflow, which is when the client-side fallback in `OllamaClient.EmbedAsync` kicks in (see Phase 2 gotchas in `CLAUDE.md`).
- **Embedding model:** schema-coupled to a fixed per-DB `FLOAT[N]` dimension (1024 with the default mxbai model — `N` is substituted from `Ollama:EmbeddingDimensions` at fresh-DB creation). **Switch it only with `mailvec switch-model --model <name> --dims <n>`** — it rebuilds the `chunk_embeddings` vec0 table at the new dimension, clears chunks, re-queues every message, and updates `metadata` in one transaction. Do **not** hand-edit the schema or manually reindex: mixing vector spaces silently corrupts similarity scores in ways that look plausible. See the embedding-model invariant in `CLAUDE.md` and `docs/contributing/embedding-experiments.md`.
- **Vision model (OCR):** `Ollama:VisionModel`, default `qwen2.5vl:7b`, used by the embedder's scanned-PDF OCR pass (`Embedder:OcrEnabled`, on by default). Pull it with `ollama pull qwen2.5vl:7b`. Unlike the embedding model it is **not** schema-coupled — swap it freely (no reindex); only newly-OCR'd PDFs use the new model, and you can re-run OCR on existing ones by resetting their `extraction_status` from `ocr` back to `no_text`. If it isn't pulled, OCR logs a warning and skips (scanned PDFs stay `no_text`); `mailvec doctor` flags it. Loaded on demand, not pinned — see the OCR design doc. There is no hard version floor today; `/api/generate` with `images` is long-standing.
