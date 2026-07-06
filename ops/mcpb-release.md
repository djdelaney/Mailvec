# MCPB release notes

`ops/build-mcpb.sh` produces `dist/mailvec-<version>.mcpb`. It runs `dotnet publish -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=false`, copies `manifest.json` next to the published `server/` directory, and zips the result. Claude Desktop unpacks the bundle to `~/Library/Application Support/Claude/Claude Extensions/local.mcpb.<author>.mailvec/` (older builds used `.../Connectors/`) — not under `~/Documents`, so it sidesteps the TCC read block (see the MCP transport quirks in `CLAUDE.md`).

## Build choices

- **Single-arch: Apple Silicon only.** The `RID="osx-arm64"` in `build-mcpb.sh` means the bundle (both the apphost and the bundled `vec0.dylib`) is arm64-only. Intel Macs are deliberately unsupported (macOS is dropping Intel in its next release); the build script and all install scripts refuse to run on x86_64. Don't hand the bundle to an Intel-Mac user — it installs fine but the connector never appears, with the only clue buried in `~/Library/Logs/Claude/mcp-server-mailvec.log`.
- **Self-contained, NOT single-file.** `PublishSingleFile=true` would still leave `vec0.dylib` outside the apphost (added via `<None CopyToOutputDirectory>`, not as a managed dep), but turning it off keeps the layout debuggable: `server/Mailvec.Mcp` and `server/runtimes/osx-arm64/native/vec0.dylib` are visibly co-located, and `ConnectionFactory.ResolveVecExtension` resolves the relative path against `AppContext.BaseDirectory` exactly as it does in dev builds. Single-file would also make `xattr`/Gatekeeper triage harder.
- **The bundle is large (tens of MB).** The self-contained .NET 10 runtime is the bulk, and the native PDFtoImage/PDFium + SkiaSharp assets (added for `get_attachment_page_image` and OCR-adjacent rendering) grew it further. Fine for a personal install. Don't switch to framework-dependent — that brings back the `DOTNET_ROOT` / PATH problem the bundle was built to eliminate.
- **The bundle is the read-side only.** Indexer + embedder still run as your own processes against the same DB. Updating the bundle does not require restarting them.

## Updating an installed bundle

```sh
ops/build-mcpb.sh --bump
```

This patch-bumps `manifest.json`, rebuilds, and `open`s the new `.mcpb` (which Claude Desktop intercepts as an install prompt). Then in Settings → Extensions toggle Mailvec off and confirm the install, quit + relaunch.

- Toggling off (vs uninstalling) preserves user_config values across upgrades.
- Without a version bump, Claude Desktop silently ignores the re-install — plain `build-mcpb.sh` is fine for "rebuild and inspect locally" but `--bump` is what you need to actually swap the running binary.

## Manifest authoring

The manifest gotchas that bite at code-edit time (`~/...` defaults, `required: false` on new fields, tool-shape rules that break the install) live in [`docs/contributing/mcpb.md`](../docs/contributing/mcpb.md).
