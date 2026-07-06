# Mailvec

Local-first IMAP archive with keyword (FTS5) and semantic (sqlite-vec) search, exposed to Claude over MCP. Single-account, single-machine, designed to run unattended on a Mac mini.

Sync is done by [`mbsync`](https://isync.sourceforge.io/), so any IMAP server works — Fastmail, iCloud, Gmail (with an app password), self-hosted Dovecot, etc. The shipped `ops/mbsyncrc.example` and reference design use Fastmail; swap the `Host` / `User` / `PassCmd` lines and the rest of the pipeline is unchanged.

<table>
  <tr>
    <td align="center" width="33%"><img src="assets/screenshots/tray-dashboard.png" alt="Tray dashboard" width="320"/><br/><em>Tray dashboard</em></td>
    <td align="center" width="33%"><img src="assets/screenshots/search-popover.png" alt="Inline semantic search popover" width="320"/><br/><em>Inline search popover</em></td>
    <td align="center" width="33%"><img src="assets/screenshots/claude-desktop-answer.png" alt="Claude Desktop answering from the archive" width="320"/><br/><em>Claude Desktop using Mailvec</em></td>
  </tr>
</table>

<sub>Screenshots use a synthetic demo archive — no real mail.</sub>

## What you get

- **A local searchable archive** of your IMAP account on disk — keyword (FTS5/BM25), semantic (sqlite-vec, mxbai-embed-large), and hybrid (RRF fusion) search.
- **An MCP server** Claude Desktop, Claude Code, and other local agents can call to search your mail, fetch threads, and read attachments — the raw file, its extracted text, or a rendered page image.
- **A menu-bar app** for live status, inline search, and one-click ops tasks. Optional — the whole pipeline works headless.

## Architecture

Four .NET services + a SwiftUI tray app, communicating only through the filesystem (Maildir) and the SQLite database.

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
                           ▲                       │
                           │ writes chunks         │ /tray/* (REST)
                           │ + vectors             ▼
        Mailvec.Embedder  ──► Ollama         Mailvec.Tray (SwiftUI menu-bar app)
                              (localhost:11434)
```

## Quickstart

Requires macOS 14+ on **Apple Silicon** (Intel Macs are not supported — macOS is dropping Intel in its next release, and the install scripts refuse to run on x86_64), the .NET 10 SDK, and a few brews. Embeddings are local-only via Ollama. Building the menu-bar tray additionally needs a full **Xcode** (not just the Command Line Tools) for `xcodebuild` — if you don't have it, run `./ops/install-all.sh --no-tray` for the headless pipeline (it skips the tray with a clear message rather than failing).

```sh
# 1. Prereqs
brew install --cask dotnet-sdk   # the .NET 10 SDK (the cask; see the dotnet note below)
brew install isync xcodegen jq   # jq is only used by the validation snippets below
brew install --cask ollama-app   # the cask, NOT `brew install ollama` — see note below
open -a Ollama                   # launch once; enable "Open at Login" to survive reboot
ollama pull mxbai-embed-large    # needs Ollama ≥ 0.21.2 (the cask auto-updates; see ops/UPGRADING.md)
ollama pull qwen2.5vl:7b         # vision model for OCR'ing scanned PDFs (~6 GB; Embedder:OcrEnabled, on by default)

# 2. Configure mbsync — full walkthrough in docs/imap-setup.md; the short version:
mkdir -p ~/Mail/Fastmail                             # the Maildir mbsync fills
security add-generic-password -a you@fastmail.com -s mbsync -w   # stash the IMAP app password in the Keychain
cp ops/mbsyncrc.example ~/.mbsyncrc && chmod 600 ~/.mbsyncrc
$EDITOR ~/.mbsyncrc                              # set User + PassCmd to the same account you used above
mbsync -aV                                       # first sync — may take hours for a big archive

# 3. Build + install everything (fetches vec0.dylib itself; --no-fetch to skip)
./ops/install-all.sh                             # services + tray app, launchd-managed
```

> **Fastmail/Gmail/iCloud all need an app-specific password**, not your account password — [docs/imap-setup.md](docs/imap-setup.md) has the per-provider pointers and explains the `-a`/`-s` values, which must match your `PassCmd` line exactly. Skipping the `security add-generic-password` step is the #1 way to make `mbsync -aV` fail on first contact.

> **Install the .NET SDK via the cask (`dotnet-sdk`)**, which puts the runtime at `/usr/local/share/dotnet` — the path the CLI shim and stdio launcher default to. The `dotnet` *formula* installs under `/opt/homebrew` and may not track .NET 10; it mostly works via PATH fallbacks, but the cask is the tested path.

> **Install Ollama via the cask (`ollama-app`), not the `ollama` formula.** The Homebrew *formula* bottle has shipped incomplete builds that bundle only the MLX runner and no `llama-server`, so GGML models like `mxbai-embed-large` fail to load (`llama-server binary not found`) — Ollama answers HTTP but every `/api/embed` hangs. The cask is Ollama's own complete prebuilt app, auto-updates, keeps the `ollama` CLI on your PATH, and is what the tray's "Start Ollama" button launches. If you previously installed the formula: `brew services stop ollama && brew uninstall ollama`, then install the cask.

`install-all.sh` orchestrates three scripts and prompts for site-specific values (Maildir root, DB path, Ollama URL, optional Fastmail account id). Use `--no-tray` to skip the SwiftUI build.

Then connect Claude Desktop:

```sh
./ops/build-mcpb.sh                              # writes dist/mailvec-<version>.mcpb
open dist/mailvec-*.mcpb                         # one-click install into Claude Desktop
```

> The MCPB bundle is built self-contained for **Apple Silicon** (`RID="osx-arm64"` in `ops/build-mcpb.sh`). Intel Macs are not supported — the script refuses to run on x86_64.

### What to expect on first run

- **The first embed pass takes hours to days** on a multi-year archive — every message is chunked and run through Ollama locally. `mailvec status` shows embedding coverage ticking up; the archive is keyword-searchable long before vector coverage completes, and the tray's coverage ring tracks progress.
- **Scanned-PDF OCR runs after that**, also locally, via the ~6 GB vision model. It loads on demand, so expect Ollama memory spikes during OCR cycles.
- **Disk**: plan on roughly 4–5 GB of `archive.sqlite` per ~75k messages (vectors dominate), on top of the Maildir itself.

Nothing is stuck if the numbers are still moving — `mailvec status` is the progress bar.

## Validating the install

The installer drops the `mailvec` CLI shim at `~/.local/bin/mailvec`. That directory isn't on the default macOS `PATH`, so add it if `mailvec` isn't found (e.g. `echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.zshrc` then restart the shell).

```sh
mailvec doctor          # one-stop preflight: DB, schema, vec0, Maildir, Ollama, launchd, /health
mailvec status          # message count, embedding coverage, schema/model match
curl -s http://127.0.0.1:3333/health | jq .
```

`doctor` rolls all of the above into one checklist, returning exit 1 if any check fails. `--no-net` skips Ollama and HTTP probes for offline diagnosis; `--json` produces a machine-readable dump for bug reports.

## Backup & moving machines

The expensive part of the archive is the *derived* data — OCR'd text and embeddings that took hours to compute. Two scripts move it safely (a raw `cp` of a live SQLite file, or copying the `-wal` sidecar, can corrupt the copy):

```sh
ops/export-db.sh                      # consistent snapshot → ~/mailvec-archive-snapshot.sqlite
ops/export-db.sh --to you@newmac      # ...and scp it over
ops/import-db.sh /path/snapshot.sqlite  # on the destination, AFTER install-all.sh + mbsync there
```

Both scripts pause the writers, checkpoint the WAL, and restart the services when done; their header comments document the ordering requirements (on the destination: install first, sync mail first, then import). The Maildir itself is not backed up by these — mbsync can always re-pull it from the server.

## Uninstall

```sh
ops/stop.sh                  # just stop the launchd agents (keeps everything installed)
ops/install.sh --uninstall   # boot out the agents and remove their plists
```

`--uninstall` intentionally preserves the published binaries, your database, and the logs. For full removal afterwards:

```sh
rm -rf ~/.local/share/mailvec                          # published binaries
rm -f  ~/.local/bin/mailvec ~/.local/bin/mailvec-mcp-stdio   # CLI + stdio-launcher shims
rm -rf "$HOME/Library/Application Support/Mailvec"     # archive.sqlite + shared config + eval queries
rm -rf ~/Library/Logs/Mailvec                          # logs
# plus your Maildir (~/Mail/...) and ~/.mbsyncrc if you're done with mbsync too
```

The tray app, if installed, is a normal app bundle: quit it and delete `/Applications/Mailvec.Tray.app`. The Claude Desktop extension is removed from Claude Desktop's Settings → Extensions.

## Documentation

Operations and dev:

- **[docs/imap-setup.md](docs/imap-setup.md)** — mbsync config, Keychain, first-sync, Fastmail label-filtering gotcha
- **[docs/dev-walkthrough.md](docs/dev-walkthrough.md)** — point the pipeline at a throwaway DB for debugging without touching production
- **[docs/logs.md](docs/logs.md)** — log paths, rotation, dev overrides
- **[ops/export-db.sh](ops/export-db.sh)** / **[ops/import-db.sh](ops/import-db.sh)** — consistent archive snapshots for backup / machine migration (see "Backup & moving machines" above)

Client wiring:

- **[docs/clients/](docs/clients/)** — per-client setup: Claude Desktop and Claude Code today (Gemini CLI / Codex CLI / ChatGPT desktop are Phase 5 placeholders, not yet written)
- **[docs/tray.md](docs/tray.md)** — menu-bar app
- **[docs/attachments.md](docs/attachments.md)** — reading attachments three ways (`get_attachment` file, `get_attachment_text`, `get_attachment_page_image`) + filesystem-MCP wiring
- **[docs/fastmail-deep-links.md](docs/fastmail-deep-links.md)** — optional `webmailUrl` field
- **[docs/security.md](docs/security.md)** — threat model: what's exposed, what's accepted, what's out of scope
- **[docs/future-ideas.md](docs/future-ideas.md)** — deferred work (cloud-LLM access, tailnet/remote access)

Project:

- **[CHANGELOG.md](CHANGELOG.md)** — phase-by-phase build history
- **[CLAUDE.md](CLAUDE.md)** — contributor-facing architectural map, build conventions, gotchas
- **[ops/UPGRADING.md](ops/UPGRADING.md)** — bumping NuGet packages, the .NET SDK, sqlite-vec, SQLite, Ollama floor
- **[ops/mcpb-release.md](ops/mcpb-release.md)** — building and shipping the MCPB bundle

## Security model

Single-user, single-Mac. The macOS user account is the trust boundary; inside it any local process can call any tool, outside it Mailvec is unreachable. The MCP HTTP server binds `127.0.0.1`, all seven tools are read-only against the database, and the only filesystem writes are `get_attachment` / `get_attachment_page_image` dropping a sanitized, path-contained copy into `~/Downloads/mailvec/`. There's no authentication, no rate limiting, and `Mcp:LogToolCalls=false` by default — turning it on writes query strings into the rolling log files. Full discussion (what's accepted, what's out of scope, why Phase 5 doesn't change the model) lives in [`docs/security.md`](docs/security.md). Read it before changing the bind address, adding a mutating tool, or pointing the server at anything other than loopback.

## Status

End-to-end working with Claude Desktop (MCPB stdio) and Claude Code (HTTP); Phase 5 (other local agents — Gemini CLI, Codex CLI, ChatGPT desktop) not yet started. See [CHANGELOG.md](CHANGELOG.md) for the phase-by-phase history.
