# Tray app (menu-bar UI)

Optional. A SwiftUI menu-bar app at [`src/Mailvec.Tray/`](../src/Mailvec.Tray/) that surfaces a status dashboard, a ⌘⇧M search popover, and a Preferences window. It's a thin client over the same `127.0.0.1:3333` MCP server the launchd agents run — talks to a dedicated `/tray/*` REST surface (`/tray/status`, `/tray/system`, `/tray/search`, `/tray/control`, `/tray/folders`, `/tray/email/{id}`, `/tray/attachment`) so it never depends on the LLM-facing MCP framing.

```sh
brew install xcodegen           # required (XcodeGen generates the .xcodeproj from project.yml)
./ops/install-tray.sh           # builds + signs ad-hoc + installs to /Applications + launches
```

## What it gives you

- **Dashboard** with live message / chunk / DB-size counters, embedding-coverage ring, service-state tiles (mbsync / indexer / embedder / mcp / ollama), a 30-minute throughput sparkline, and a recent-activity timeline.
- **Search popover** (⌘⇧M) with hybrid / keyword / semantic chips, folder + date filters, inline message-body preview, and one-click "Open in Fastmail".
- **Preferences** for launch-at-login, notifications (Ollama-unreachable / sync-failure / archive-complete banners), Fastmail webmail-link config, and a set of CLI-equivalent buttons (Doctor, Rebuild FTS5, Checkpoint WAL, Audit embeddings, Purge soft-deleted, Reindex all). Long-running commands open in Terminal so you see streaming output.

## Build chain

[`project.yml`](../src/Mailvec.Tray/project.yml) (XcodeGen spec — checked in) → `.xcodeproj` (regenerated each build, gitignored) → `xcodebuild archive` with ad-hoc signing → `build/Mailvec.Tray.app`. The ad-hoc signature matters — macOS rejects `UNUserNotificationCenter.requestAuthorization` for fully-unsigned bundles, so notifications need at least `codesign -s -`. No Apple Developer Program account required.

The CLI buttons spawn `~/.local/bin/mailvec` (installed by `ops/install.sh` — a small shim that execs `dotnet ~/.local/share/mailvec/cli/Mailvec.Cli.dll`). After CLI source changes, re-run `ops/redeploy.sh cli` to refresh.
