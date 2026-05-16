# Connecting to Claude Desktop

The MCP server is shipped as an [MCPB bundle](https://blog.modelcontextprotocol.io/posts/2025-11-20-adopting-mcpb/) — a single `.mcpb` file that Claude Desktop installs with one drag-and-drop. The bundle contains a self-contained .NET binary plus the `vec0.dylib`, so the user doesn't need a .NET SDK installed.

## Build the bundle

```sh
./ops/fetch-sqlite-vec.sh   # if you haven't yet
./ops/build-mcpb.sh         # writes dist/mailvec-<version>.mcpb (~50 MB)
```

## Install

Drag `dist/mailvec-<version>.mcpb` onto Claude Desktop, or `open dist/mailvec-<version>.mcpb`. The install dialog prompts for these values (defined in `manifest.json`):

- **Database path** — SQLite archive. Default `~/Library/Application Support/Mailvec/archive.sqlite`. Created if it doesn't exist (empty schema).
- **Maildir root** — directory containing your Maildir folders, where mbsync syncs your mail. Default `~/Mail/Fastmail`. Used by `get_attachment` to read attachment bytes from the original `.eml` files at extract time. Should match the path the indexer is configured with.
- **Ollama endpoint** — default `http://localhost:11434`. Only used to embed search queries; the indexer/embedder are separate processes.
- **Fastmail account id (optional)** — enables webmail deep-links on search results. See [docs/fastmail-deep-links.md](fastmail-deep-links.md).
- **Log tool calls (debug)** — when on, the server logs each tool invocation to `~/Library/Logs/Claude/mcp-server-mailvec.log`. Off by default.

The bundle extracts to `~/Library/Application Support/Claude/extensions/<id>/` — a non-TCC location, which avoids the `~/Documents` read block we hit during early dev.

## Updating to a new build

```sh
./ops/build-mcpb.sh --bump   # patch-bumps manifest.json, builds, opens the result
```

Then in Claude Desktop: Settings → Extensions → Mailvec → toggle off, accept the install prompt, quit + relaunch. Toggling off (vs uninstalling) preserves your `user_config` values across the upgrade. Claude Desktop ignores re-installs of the same version, so the bump is what makes the new binary actually take effect — without it, the install prompt is a no-op.

To bump manually instead (e.g. for a minor or major version), edit `manifest.json` and run `./ops/build-mcpb.sh` without the flag.

The indexer and embedder run as your own processes outside the bundle — they keep going across updates and don't need to be restarted when you ship a new MCP build.

## Notes

- The bundled binary is `osx-arm64` only (declared in `manifest.json` `compatibility.platforms`). Add `osx-x64` to `build-mcpb.sh` if you need it.
- The binary is unsigned. macOS Gatekeeper may prompt the first time Claude Desktop spawns it; allow once and it's fine. If it gets quarantined silently, `xattr -dr com.apple.quarantine "$HOME/Library/Application Support/Claude/extensions/"` clears it.
- The bundled MCP server writes the same Serilog rolling file as the standalone build (`~/Library/Logs/Mailvec/mailvec-mcp-<date>.log` — see [docs/logs.md](logs.md)). It also emits to stderr, which Claude Desktop captures into `~/Library/Logs/Claude/mcp-server-mailvec.log`. Either is fine for triage; the Mailvec rolling file is more durable across days, the Claude one is what Anthropic Support will ask for.

Release-engineering details (signing, distribution, version bumps) live in [`ops/mcpb-release.md`](../ops/mcpb-release.md).
