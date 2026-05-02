# Mailvec

Local-first IMAP archive with keyword (FTS5) and semantic (sqlite-vec) search, exposed to Claude over MCP. Single-account, single-machine, designed to run unattended on a Mac mini. See [`mailvec-project-scope.md`](mailvec-project-scope.md) for the full design and [`docs/glossary.md`](docs/glossary.md) for unfamiliar terms (FTS5, RRF, MCP, Maildir, etc.).

Sync is done by [`mbsync`](https://isync.sourceforge.io/), so any IMAP server works — Fastmail, iCloud, Gmail (with an app password), self-hosted Dovecot, etc. The shipped `ops/mbsyncrc.example` and reference design use Fastmail because that's the author's setup; swap the `Host` / `User` / `PassCmd` lines and the rest of the pipeline (Maildir → SQLite → embeddings → MCP) is unchanged.

## Layout

```
src/
  Mailvec.Core      shared types, SQLite + Ollama clients, hybrid search
  Mailvec.Indexer   BackgroundService: Maildir -> SQLite (no embeddings)
  Mailvec.Embedder  BackgroundService: SQLite rows -> Ollama embeddings
  Mailvec.Mcp       AspNetCore MCP server (HTTP on :3333, or stdio for MCPB)
  Mailvec.Cli       admin commands (status, search, reindex, rebuild-fts)
tests/
  Mailvec.{Core,Indexer,Mcp}.Tests
schema/
  001_initial.sql   tables, FTS5 triggers, vec0 vector index
ops/
  mbsyncrc.example     IMAP sync config template
  launchd/             plist templates for mbsync + 3 .NET services
  install.sh           Phase 4 installer (stub)
  fetch-sqlite-vec.sh  one-shot: pulls vec0.dylib from upstream releases
  build-mcpb.sh        packages a .mcpb bundle into dist/ for Claude Desktop
  dev-fetch-imap.py    dev-only: pulls last N days of mail without mbsync
runtimes/
  osx-arm64/native  sqlite-vec native binary lands here
manifest.json       Claude Desktop MCPB manifest (binary entry + user_config)
```

## Setup

Requires .NET 10 SDK and (for semantic / hybrid search only) a local Ollama server.

### Build

```sh
./ops/fetch-sqlite-vec.sh   # downloads vec0.dylib (run once)
dotnet build
dotnet test
```

The `sqlite-vec` extension is loaded at runtime from `runtimes/<rid>/native/vec0.dylib`. The fetch script grabs the latest release from the upstream GitHub repo; pin a version with `SQLITE_VEC_VERSION=0.1.9 ./ops/fetch-sqlite-vec.sh`.

Central package management lives in `Directory.Packages.props`. Shared MSBuild settings (target framework, nullable, warnings-as-errors) live in `Directory.Build.props`.

### Ollama

The embedder talks to a local Ollama server. One-time setup on macOS:

```sh
brew install ollama
brew services start ollama           # run as a launchd background service
ollama pull mxbai-embed-large        # 1024-dim model used by default
```

**Run Ollama as a service, not in a foreground shell.** `brew services start ollama` registers it as a launchd agent — it survives reboots and writes its own logs to `~/Library/Logs/Homebrew/ollama/*.log`. If you instead run `ollama serve` in a terminal, its `[GIN]` request log lines and `level=INFO source=server.go:...` notices will interleave with the embedder/indexer output. `brew services list` should show `ollama started` once the service is up.

Caveat: `brew services` runs Ollama under a launchd plist that doesn't load your shell config, so any `OLLAMA_*` env vars you set interactively won't apply. To override defaults (e.g. `OLLAMA_KEEP_ALIVE`), edit `~/Library/LaunchAgents/homebrew.mxcl.ollama.plist` under `<EnvironmentVariables>` and `brew services restart ollama`.

You don't need Ollama running to build, run the indexer, or use keyword search — only the embedder, semantic search, and hybrid search depend on it.

The configured model is recorded in the SQLite `metadata` table on first embed. If you change `Ollama:EmbeddingModel` later, the embedder refuses to start until you run `mailvec reindex --all` to clear the existing vectors. Mixing vector spaces silently corrupts results, so this guard is intentional.

### mbsync (IMAP sync)

Pulls IMAP into a local Maildir the indexer watches. Read-only — changes flow Fastmail → local, never the other way.

```sh
brew install isync                     # the binary is `mbsync`; Homebrew names the formula after the suite
mkdir -p ~/Mail/Fastmail
```

**Stash your IMAP password in the macOS Keychain** so it's never on disk. For Fastmail, generate an app-specific password at <https://app.fastmail.com/settings/security/devicekeys> (the password is shown once and can't be retrieved later — only revoked). Add it under service name `mbsync`:

```sh
security add-generic-password -a you@fastmail.com -s mbsync -w
```

The `-a` (account) and `-s` (service) values must match the `PassCmd` line in `~/.mbsyncrc`. The `-w` flag prompts for the password without echoing.

**Configure mbsync** by copying the example and replacing `you@fastmail.com` on both the `User` and `PassCmd` lines:

```sh
cp ops/mbsyncrc.example ~/.mbsyncrc
chmod 600 ~/.mbsyncrc
# then edit User + PassCmd in ~/.mbsyncrc
```

For non-Fastmail IMAP, swap the `Host` line; Gmail and iCloud both require an app-specific password issued from their respective account-security UIs. See `man mbsync` for fancier auth (XOAUTH2, etc.).

**First sync.** Run it once manually before scheduling — a multi-year archive can take hours, and you want to see any auth or TLS errors live:

```sh
mbsync -aV       # -a = all channels, -V = verbose
```

Run inside `tmux` / `screen` for a big archive so a closed terminal doesn't kill it. Subsequent syncs are incremental and finish in seconds. The indexer (and embedder) can start against `~/Mail/Fastmail/` while mbsync is still working — they'll pick up new messages as they land.

**Folder filtering (Fastmail labels gotcha).** Fastmail exposes labels as IMAP folders, so a message with two labels lives in two folders. With the example's `Patterns *`, mbsync downloads both copies and the indexer logs spurious `Content changed; cleared embeddings` warnings on the second one — Fastmail's IMAP serializer regenerates the multipart boundary string per folder, so the body bytes hash differently across copies despite being the same email. Final search results are correct, just at the cost of re-embedding every multi-labelled message. To avoid it, narrow `Patterns` (e.g. `Patterns INBOX`); the [example](ops/mbsyncrc.example) shows the common forms.

**Scheduling** is part of Phase 4. The plist at [`ops/launchd/com.mailvec.mbsync.plist`](ops/launchd/com.mailvec.mbsync.plist) runs `mbsync -a` every 5 minutes once `ops/install.sh` (currently a stub) wires it in. To install it manually now: `cp` it to `~/Library/LaunchAgents/`, replace `__LOG_DIR__` with `~/Library/Logs/Mailvec` (and `mkdir -p` that dir), then `launchctl load <plist>`.

## Trying it end-to-end

Two kinds of testing: the automated unit/integration suite, and a manual walkthrough against real mail. Both bypass mbsync; mbsync is the production sync path but isn't required to exercise the rest of the pipeline.

### 1. Automated tests

```sh
dotnet test
```

~100 tests across `Mailvec.Core.Tests` and `Mailvec.Indexer.Tests` — parser fixtures, schema migrations, repositories, FTS5 search, vector search (with hand-injected vectors), RRF fusion, the Ollama HTTP client (stubbed), chunking, attachment extraction, path expansion. No live Ollama or IMAP needed. (`Mailvec.Mcp.Tests` exists as a project but has no cases yet — the MCP layer is exercised through Core and via manual smoke tests.)

### 2. Get a Fastmail app password

Visit <https://app.fastmail.com/settings/security/devicekeys>, click **New App Password**, name it `mailvec-test`, copy the password somewhere safe. Revoke it when you're done.

### 3. Fetch the last 7 days of mail

`ops/dev-fetch-imap.py` writes each message as an `.eml` file into a Maildir layout the indexer understands. Filenames use the IMAP UID, so re-running is idempotent — already-fetched messages are skipped.

```sh
export FASTMAIL_USER=you@example.com
export FASTMAIL_APP_PASSWORD='<paste-here>'    # quote it; contains spaces

./ops/dev-fetch-imap.py
```

Optional overrides: `MAILDIR_ROOT` (default `~/mailvec-test/Mail`), `SINCE_DAYS` (default 7), `IMAP_HOST`, `IMAP_FOLDER`. To pull from a different IMAP provider, set `IMAP_HOST` accordingly.

### 4. Run the indexer

Point the env vars at your test Maildir + a fresh DB path:

```sh
export Ingest__MaildirRoot=~/mailvec-test/Mail
export Archive__DatabasePath=~/mailvec-test/archive.sqlite
export Logging__LogLevel__Default=Information

dotnet run --project src/Mailvec.Indexer
```

Look for a single line like `MaildirScanner: seen=N upserted=N parseFailed=K softDeleted=0`. The watcher then idles until new files arrive; ^C once the initial scan is done. Any `parseFailed > 0` count means real-world headers tripped the parser — capture the offending file (the warning prints the full path) and we can add a fixture.

### 5. Run the embedder (optional, for semantic/hybrid)

Make sure Ollama is running and `mxbai-embed-large` is pulled (see [Setup → Ollama](#ollama)), then in a second terminal with the same env vars:

```sh
dotnet run --project src/Mailvec.Embedder
```

Watch for `Embedded N messages (M chunks) in <ms>` lines. The first call is slow (model load); subsequent batches are fast. ^C when `mailvec status` shows full coverage.

For a small test archive this finishes in minutes. A full archive (tens of thousands of messages) is overnight territory on Apple Silicon without a dedicated GPU — the embedder works through it in the background and you can keep using `mailvec status` / `search` against partial coverage at any time. After a bulk run, `mailvec checkpoint` truncates the SQLite WAL file, which can grow to multiple GB during long write sessions:

```sh
dotnet run --project src/Mailvec.Cli -- checkpoint
```

This is a one-shot cleanup. SQLite auto-checkpoints during normal operation, so day-to-day you don't need to think about it.

### 6. Search

Same env vars, third terminal:

```sh
dotnet run --project src/Mailvec.Cli -- status
dotnet run --project src/Mailvec.Cli -- search "ramen"                  # keyword (FTS5/BM25)
dotnet run --project src/Mailvec.Cli -- search --semantic "vacation plans"
dotnet run --project src/Mailvec.Cli -- search --hybrid "tree quote"
dotnet run --project src/Mailvec.Cli -- search "lunch AND friday" -n 10  # boolean, custom limit
dotnet run --project src/Mailvec.Cli -- search '"exact phrase"'          # phrase
dotnet run --project src/Mailvec.Cli -- audit-embeddings                # sanity-check vector index
dotnet run --project src/Mailvec.Cli -- checkpoint                      # truncate the SQLite WAL
```

`status` prints message counts, embedding coverage, and warns if the schema's recorded embedding model disagrees with config. `audit-embeddings` sweeps the vector index for zero / NaN / abnormal-norm vectors — useful right after a large reindex or an Ollama upgrade.

The Phase 2 exit criterion is "semantic queries return relevant results the FTS layer would have missed" — a quality call that needs your eyes on a real archive. Useful comparison queries are paraphrases ("trip planning" vs `vacation`), synonyms (`bill` vs `invoice`), and topic-level recall (`subscription renewal`, `house repairs`).

### 7. Cleanup

```sh
rm -rf ~/mailvec-test
unset FASTMAIL_USER FASTMAIL_APP_PASSWORD Ingest__MaildirRoot Archive__DatabasePath
# then revoke the app password at https://app.fastmail.com/settings/security/devicekeys
```

## Logs

The three .NET services (indexer, embedder, MCP server) write rolling daily log files to:

```
~/Library/Logs/Mailvec/mailvec-<service>-<YYYYMMDD>.log
```

Daily rolling, 10 MB cap per file, 14 most recent files kept. Implementation is Serilog's File sink wired through [Mailvec.Core/Logging/SerilogSetup.cs](src/Mailvec.Core/Logging/SerilogSetup.cs); rotation happens in-process so there's nothing to cron.

When you run a service via `dotnet run` in a terminal, log lines also stream to stdout for live visibility. Under launchd (production), the plists set `MAILVEC_LAUNCHD=1` to suppress that — only the rolling file gets written. To override either default during development:

```sh
export MAILVEC_LOG_DIR=/some/other/path   # change the log directory
export MAILVEC_LAUNCHD=1                  # silence stdout, even outside launchd
```

The Claude Desktop MCPB bundle writes to the same rolling file (it's the same binary). It additionally emits to stderr, which Claude Desktop's own log capture preserves at `~/Library/Logs/Claude/mcp-server-mailvec.log` — handy when triaging extension-install issues, since that's the file Claude Desktop's UI will surface in error toasts.

## Connecting to Claude Desktop

The MCP server is shipped as an [MCPB bundle](https://blog.modelcontextprotocol.io/posts/2025-11-20-adopting-mcpb/) — a single `.mcpb` file that Claude Desktop installs with one drag-and-drop. The bundle contains a self-contained .NET binary plus the `vec0.dylib`, so the user doesn't need a .NET SDK installed.

**Build the bundle:**

```sh
./ops/fetch-sqlite-vec.sh   # if you haven't yet
./ops/build-mcpb.sh         # writes dist/mailvec-<version>.mcpb (~50 MB)
```

**Install:** drag `dist/mailvec-<version>.mcpb` onto Claude Desktop, or `open dist/mailvec-<version>.mcpb`. The install dialog prompts for these values (defined in `manifest.json`):

- **Database path** — SQLite archive. Default `~/Library/Application Support/Mailvec/archive.sqlite`. Created if it doesn't exist (empty schema).
- **Maildir root** — directory containing your Maildir folders, where mbsync syncs your mail. Default `~/Mail/Fastmail`. Used by `get_attachment` to read attachment bytes from the original `.eml` files at extract time. Should match the path the indexer is configured with.
- **Ollama endpoint** — default `http://localhost:11434`. Only used to embed search queries; the indexer/embedder are separate processes.
- **Fastmail account id (optional)** — enables webmail deep-links on search results. See [Fastmail webmail deep-links](#fastmail-webmail-deep-links-optional) below.
- **Log tool calls (debug)** — when on, the server logs each tool invocation to `~/Library/Logs/Claude/mcp-server-mailvec.log`. Off by default.

The bundle extracts to `~/Library/Application Support/Claude/extensions/<id>/` — a non-TCC location, which avoids the `~/Documents` read block we hit during early dev.

**Updating to a new build:**

```sh
./ops/build-mcpb.sh --bump   # patch-bumps manifest.json, builds, opens the result
```

Then in Claude Desktop: Settings → Extensions → Mailvec → toggle off, accept the install prompt, quit + relaunch. Toggling off (vs uninstalling) preserves your `user_config` values across the upgrade. Claude Desktop ignores re-installs of the same version, so the bump is what makes the new binary actually take effect — without it, the install prompt is a no-op.

To bump manually instead (e.g. for a minor or major version), edit `manifest.json` and run `./ops/build-mcpb.sh` without the flag.

The indexer and embedder run as your own processes outside the bundle — they keep going across updates and don't need to be restarted when you ship a new MCP build.

**Notes:**

- The bundled binary is `osx-arm64` only (declared in `manifest.json` `compatibility.platforms`). Add `osx-x64` to `build-mcpb.sh` if you need it.
- The binary is unsigned. macOS Gatekeeper may prompt the first time Claude Desktop spawns it; allow once and it's fine. If it gets quarantined silently, `xattr -dr com.apple.quarantine "$HOME/Library/Application Support/Claude/extensions/"` clears it.
- The bundled MCP server writes the same Serilog rolling file as the standalone build (`~/Library/Logs/Mailvec/mailvec-mcp-<date>.log` — see [Logs](#logs)). It also emits to stderr, which Claude Desktop captures into `~/Library/Logs/Claude/mcp-server-mailvec.log`. Either is fine for triage; the Mailvec rolling file is more durable across days, the Claude one is what Anthropic Support will ask for.

## Reading attachments

`get_attachment` extracts a single email attachment to `~/Downloads/mailvec/` (configurable via `Mcp:AttachmentDownloadDir`) and returns the absolute path. It deliberately does **not** try to ship the bytes back through MCP — Claude.ai's MCP bridge currently mishandles non-image binary blobs and rejects them as "unsupported image format". Putting the file on disk delegates the "interpret bytes by file type" job to whichever tool is best at it.

How Claude actually reads the file depends on the client:

- **Claude Code** — the built-in `Read` tool can open the saved path directly and handles PDFs, text, images, etc. natively. Nothing extra to install.
- **Claude.ai web / Claude Desktop** — Claude can't read arbitrary local paths. To make `get_attachment` useful end-to-end, **install a filesystem MCP server** alongside Mailvec. The official one is [`@modelcontextprotocol/server-filesystem`](https://github.com/modelcontextprotocol/servers/tree/main/src/filesystem). Point it at `~/Downloads/mailvec/`, then Claude can call its `read_text_file` / `read_media_file` tools on the path Mailvec just returned. Without a filesystem MCP, `get_attachment` still works — Claude just tells you "I saved it to /Users/.../Downloads/mailvec/foo.pdf" and you open it yourself in Finder.

For convenience, two cases are also inlined as native MCP content blocks regardless of client:

- **Image attachments** are inlined as `ImageContentBlock` so Claude vision can describe / OCR them in one round trip.
- **Small text-ish files** (`text/*`, `application/json`, `application/xml`, etc., under `Mcp:AttachmentInlineTextMaxBytes` — default 256 KB) have their decoded UTF-8 text included as an additional text block.

The file is also always saved to disk in those cases, so a downstream tool can still pick it up.

## Fastmail webmail deep-links (optional)

Search and get-email tool results can include a `webmailUrl` that opens the message in Fastmail's web UI. The current implementation uses Fastmail's `msgid:<RFC-Message-ID>` search-URL syntax — zero JMAP calls, zero auth, but the user lands on a search-results pane and clicks once more to open the conversation. The feature is **opt-in**: with no account id configured, no link field is emitted.

**Config keys** (section `Fastmail` — bound by the MCP server and CLI):

- `Fastmail:AccountId` (env: `Fastmail__AccountId`) — JMAP account id, format `u` followed by 8 hex chars. Find yours by logging into <https://app.fastmail.com> and copying the `?u=...` query param off any URL in the address bar.
- `Fastmail:WebUrl` (env: `Fastmail__WebUrl`) — defaults to `https://app.fastmail.com`. Override only for self-hosted Fastmail-API-compatible deployments.

**Enabling for the CLI / HTTP MCP server:** drop the value into `appsettings.Local.json` next to the executable, or export the env var:

```sh
export Fastmail__AccountId=u1234abcd
dotnet run --project src/Mailvec.Mcp           # HTTP transport
dotnet run --project src/Mailvec.Cli -- search "ramen"
```

**Enabling for the Claude Desktop MCPB bundle:** the install dialog has a **Fastmail account id (optional)** field — paste your `u…` id and you're done. Leaving it blank disables webmail links, which is the default. The value persists across future `--bump` upgrades as long as you toggle the extension off (vs uninstall) before re-installing. To change it later: Settings → Extensions → Mailvec → Configure.

A "proper" upgrade to direct conversation links (resolving RFC Message-ID → JMAP Email-id via `Email/query` and emitting `mail/Inbox/<thread>.<email>?u=...`) needs a Fastmail API token and two new nullable cache columns on `messages` — see `WebmailLinkBuilder` for where to swap the URL shape.

## Status

Built in phases per the [design doc](mailvec-project-scope.md#8-phased-build-plan). Each phase has a hard exit criterion and ships standalone value before the next begins.

### ✅ Phase 0 — Repo scaffold

Solution, projects, central package management, shared MSBuild settings, ops + schema folders, gitignore, README.

### ✅ Phase 1 — Ingest pipeline

A searchable local archive. **Exit criterion met:** point the indexer at a Maildir and `mailvec search "<query>"` returns BM25-ranked hits with snippets.

- Schema migration runner (`Mailvec.Core.Data.SchemaMigrator`) over an embedded `001_initial.sql`.
- MimeKit-based parser with fixture-driven tests (plain text, multipart, html-only, unicode headers, attachments).
- `MaildirScanner` — recursive full scan, soft-delete reconciliation, mbsync `new/`→`cur/` rename detection.
- `MaildirWatcher` — debounced `FileSystemWatcher` with `tmp/` filtering.
- `MessageIngestService` — `BackgroundService` that runs an initial scan, then reacts to watcher pulses + a periodic safety-net rescan.
- FTS5 with BM25 ordering and bracketed snippets.
- `mailvec status | search | rebuild-fts` CLI.

### ✅ Phase 2 — Semantic layer

Adds locally-generated embeddings and hybrid (FTS + vector) search. **Exit criterion met:** semantic + hybrid search return chunk-level results from sqlite-vec; behaviour validated against hand-injected vectors in tests. Real-world quality validation is on you once you have an embedded archive.

- `OllamaClient` — typed `HttpClient` against `POST /api/embed` with `Microsoft.Extensions.Http.Resilience` retry/circuit-breaker.
- `ChunkingService` — paragraph-aware splitter (~4 chars/token heuristic) with configurable size + overlap; hard-splits unbroken blocks.
- `ChunkRepository` — atomic chunk + vector writes (so a message is never half-embedded).
- `EmbeddingWorker` — `BackgroundService` that polls `messages WHERE embedded_at IS NULL`, prepends subject to body, batches Ollama calls, and refuses to start if `metadata.embedding_model` disagrees with config.
- `VectorSearchService` — sqlite-vec `MATCH/k` query, returns one row per message (best-matching chunk), filters soft-deleted.
- `HybridSearchService` — Reciprocal Rank Fusion (k=60) over BM25 + vector legs.
- CLI: `search --semantic`, `search --hybrid`, `reindex --all | --folder=NAME`. `status` now surfaces embedding coverage, chunk count, and schema/config model mismatches.

### ✅ Phase 3 — MCP exposure

Wires the archive up to Claude. **Exit criterion met.**

- `Mailvec.Mcp` runs in two transports sharing the same Core wiring:
  - **HTTP** (default) on `127.0.0.1:3333` for Claude Code and the smoke tests.
  - **stdio** (`--stdio` flag) for Claude Desktop, packaged as an `.mcpb` bundle (see [Connecting to Claude Desktop](#connecting-to-claude-desktop)).
- Tools: `search_emails` (keyword / semantic / hybrid with folder/date/sender filters), `get_email`, `get_thread`, `list_folders`, `get_attachment`. The original 6-tool design merged to 4 search/fetch tools — `recent_emails` is `search_emails` with `query` omitted, and `find_by_sender` is `search_emails` with `fromExact` — plus `get_attachment` added later for attachment delivery (see [Reading attachments](#reading-attachments)).
- Hybrid search reused from Phase 2.
- Attachment indexing: filenames are stored in the `attachments` table and surfaced through FTS5 (so a query like `"mortgage statement"` matches an email whose only mention is in `mortgage_statement_2024.pdf`). Per-attachment metadata (filename, content type, size, partIndex) is returned by `get_email`.

### 🟡 Phase 4 — Operationalization

Makes the system survive reboots unattended. In progress.

- launchd plist *templates* exist in `ops/launchd/` for mbsync + indexer + embedder + MCP HTTP server. Not yet wired up by `install.sh`.
- `ops/install.sh` — currently a stub. Will publish services, rewrite the `__INSTALL_PREFIX__` and `__LOG_DIR__` placeholders, load the agents, verify health.
- ✅ `/health` endpoint on the MCP server — `GET /health` returns DB / embedding / Ollama status (200 healthy, 503 degraded). HTTP-mode only; stdio sees failures via the MCP `initialize` handshake.
- ✅ Log rotation — handled in-process by Serilog. The three .NET services write rolling daily files to `~/Library/Logs/Mailvec/mailvec-<service>-<YYYYMMDD>.log` (10 MB per-file cap, 14 files retained). Wiring lives in [src/Mailvec.Core/Logging/SerilogSetup.cs](src/Mailvec.Core/Logging/SerilogSetup.cs); the launchd plist sets `MAILVEC_LAUNCHD=1` to suppress the Console sink in production. mbsync (the only non-.NET service) writes to small launchd-captured files that don't need rotation.
- ✅ `mailvec status` already surfaces message count, embedding coverage, schema/config model match.

**Exit criterion:** reboot the Mac mini; everything comes back without intervention.

### ⬜ Phase 5 — Cross-vendor MCP access

The MCPB bundle is Claude Desktop only. Stdio works for any local-spawn client (Claude Code) but not for cloud LLMs. To reach **ChatGPT Connectors, Gemini, and Claude.ai Custom Connectors**, the HTTP transport needs HTTPS + OAuth on top:

- Public reachability over HTTPS via Cloudflare Tunnel or Tailscale **Funnel** (the public variant — ordinary tailnet doesn't reach those clients).
- MCP OAuth 2.1 (PKCE) — the .NET SDK has scaffolding; choosing an issuer (self-hosted, Cloudflare Access, or Tailscale identity) is the open call.
- Single-user authorization model: any authenticated caller can use any read-only tool. Revisit if mutating tools land later.

See [`mailvec-project-scope.md`](mailvec-project-scope.md) §8 Phase 5 for sequencing.

### Out of scope (per design doc §11)

Sending mail, modifying Fastmail state, multi-account support, calendar/contacts/files, web UI, real-time push. Attachment **filenames** are indexed (FTS5) and attachments themselves are extractable to disk via `get_attachment`; per-format **content** indexing (PDF text, DOCX text, OCR for image-only PDFs) is still out of scope — let downstream tools (Claude Code's `Read`, a filesystem MCP, your existing PDF skills) interpret the bytes.
