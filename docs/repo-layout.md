# Repo layout

```
src/
  Mailvec.Core      shared types, SQLite + Ollama clients, hybrid search, /tray/* services
  Mailvec.Indexer   BackgroundService: Maildir -> SQLite (no embeddings)
  Mailvec.Embedder  BackgroundService: SQLite rows -> Ollama embeddings
  Mailvec.Mcp       AspNetCore MCP server (HTTP on :3333, or stdio for MCPB) + /tray/* REST
  Mailvec.Cli       admin commands (status, doctor, search, get, reindex, rebuild-fts, rebuild-bodies, purge-deleted, checkpoint, audit-embeddings, extract-attachments, eval*)
  Mailvec.Tray      SwiftUI menu-bar app — dashboard, search popover, preferences
tests/
  Mailvec.{Core,Indexer,Mcp}.Tests
schema/
  001_initial.sql   tables, FTS5 triggers, vec0 vector index
  migrations/       in-place upgrade scripts for older DBs (002–005)
ops/
  mbsyncrc.example       IMAP sync config template
  launchd/               plist templates for mbsync + 3 .NET services
  install-all.sh             single-command bootstrap: fetch + install services + install tray
  install.sh                 Phase 4 installer: publishes services + CLI, renders plists, bootstraps
  install-tray.sh            builds tray .app via build-tray.sh, copies to /Applications, launches
  build-tray.sh              XcodeGen + xcodebuild archive (ad-hoc signed)
  redeploy.sh                republish + kickstart launchd agents after a code change
  stop.sh                    bootout the launchd agents without uninstalling
  fetch-sqlite-vec.sh        one-shot: pulls vec0.dylib + VERSION sidecar from upstream releases
  build-mcpb.sh              packages a .mcpb bundle into dist/ for Claude Desktop
  install-stdio-launcher.sh  publishes the stdio MCP binary + writes ~/.local/bin/mailvec-mcp-stdio (Phase 5)
  publish-mcp-stdio.sh       publish-only: refreshes ~/.local/share/mailvec/mcp/ without touching the launcher
  run-mcp.sh                 dev launcher for the HTTP MCP server
  run-mcp-stdio.sh           dev launcher for the stdio MCP server (Claude Desktop)
  coverage.sh                runs the test suite with coverage; HTML in coverage/
  dev-fetch-imap.py          dev-only: pulls last N days of mail without mbsync
docs/
  glossary.md                FTS5 / RRF / MCP / Maildir / etc.
  clients/                   per-client wiring snippets (Claude Desktop, Claude Code, Phase 5)
  dev-walkthrough.md         manual end-to-end test against a real IMAP account
  imap-setup.md              mbsync config, Keychain, first-sync, folder-filtering gotchas
  claude-desktop.md          MCPB bundle build / install / update
  tray.md                    menu-bar app
  attachments.md             how `get_attachment` works + filesystem-MCP wiring
  fastmail-deep-links.md     optional `webmailUrl` field on tool responses
  logs.md                    log paths, rotation, dev overrides
baselines/
  README.md                  eval-baseline workflow; commit one before any retrieval-affecting change
runtimes/
  osx-arm64/native  sqlite-vec native binary lands here
manifest.json       Claude Desktop MCPB manifest (binary entry + user_config)
```

A higher-level architectural map (project responsibilities, data-flow diagram, build conventions, schema invariants) lives in [`CLAUDE.md`](../CLAUDE.md) — that's the contributor-facing entry point.
