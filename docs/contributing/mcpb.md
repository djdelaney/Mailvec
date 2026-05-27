# MCPB bundle — contributor notes

Release/build steps live in [`ops/mcpb-release.md`](../../ops/mcpb-release.md). User-facing install docs live in [`docs/clients/claude-desktop.md`](../clients/claude-desktop.md). This page is the code-edit gotcha collection — read it before editing `manifest.json`, `ops/build-mcpb.sh`, or any `[McpServerTool]` whose output crosses into Claude Desktop.

## manifest.json quirks

- **`manifest.json` user_config defaults must use `~/...`, not `${HOME}/...`.** Claude Desktop's MCPB host substitutes its own `${user_config.X}` tokens but passes shell-style `${HOME}` through verbatim. `PathExpansion.Expand` defensively handles `${HOME}` / `$HOME` regardless; tests in `PathExpansionTests.cs`.
- **Don't add `required: true` user_config fields in an upgrade.** The upgrade flow doesn't pre-fill new required fields, so the extension lands disabled and invisible in the connector picker. Make new fields `required: false` with a sensible default.

## Tool-shape quirks

- **Never set `UseStructuredContent = true` on an `[McpServerTool]` whose return type is `CallToolResult`.** The .NET SDK can't infer a meaningful schema from the generic return type and emits an invalid one; Claude Desktop's Zod validator then rejects the *entire extension*. Structured content still flows back at runtime via `CallToolResult.StructuredContent` regardless of the flag. If you need an advertised output schema, return a strongly-typed POCO instead.

## macOS code identity & TCC "data from other apps" prompt

Unsigned end-user installs of the MCPB bundle eventually surface a macOS TCC prompt: *"`mailvec.mcp` would like to access data from other apps."* This is **not** caused by anything Mailvec is doing wrong — it's a side effect of how macOS attributes code identity to unsigned binaries — but it's alarming for end users and needs to go away before a public release.

### Why it happens

- `ops/build-mcpb.sh` produces a `dotnet publish --self-contained` apphost. The resulting `Mailvec.Mcp` is **ad-hoc signed only** — no Developer ID, no `CFBundleIdentifier`, no stable code identity.
- Claude Desktop unpacks the MCPB under `~/Library/Application Support/Claude/Claude Extensions/local.mcpb.<author>.mailvec/server/Mailvec.Mcp`. macOS attributes anonymous binaries living inside another app's bundle/data subtree to that surrounding app — i.e. it treats `Mailvec.Mcp` as a Claude helper (the TCC log records `identifier=apphost`, .NET's native bootstrap name, not `com.mailvec.mcp`).
- The MCP server reads `~/Library/Application Support/Mailvec/archive.sqlite` on every tool call **and** every 60 s from [`TrayEventRecorder`](../../src/Mailvec.Core/Tray/TrayEventRecorder.cs)'s background sampler. macOS sees "Claude-identified helper reading inside `~/Library/Application Support/<a different app>/`" → `kTCCServiceSystemPolicyAppData`.
- TCC caches the decision per-binary. The prompt typically fires not at first install but **30–60 minutes later**, when the cached entry expires while the 60 s sampler is still ticking — which is why this looks like a spontaneous overnight popup rather than something tied to a user action.

The launchd HTTP service does not have this problem even though it reads the same DB, because it runs via Microsoft-signed `/usr/local/share/dotnet/dotnet` and inherits that identity. Only the MCPB self-contained binary is affected.

### Diagnostic recipe

```sh
# 1. Confirm which binary triggered the prompt
log show --predicate 'subsystem == "com.apple.TCC" AND eventMessage CONTAINS[c] "mailvec"' \
  --info --last 24h | grep AUTHREQ_PROMPTING

# 2. Watch the live stdio PIDs to see the file path being touched
sudo fs_usage -w -f filesys <pid> 2>/dev/null \
  | grep -E "archive\.sqlite|Application Support/Mailvec|vec0\.dylib"

# 3. Inspect what TCC has recorded for the binary
sqlite3 ~/Library/Application\ Support/com.apple.TCC/TCC.db \
  "SELECT service, client, auth_value FROM access WHERE client LIKE '%ailvec%';"
```

### Fix path (planned, not yet implemented)

1. **Long-term: Developer ID code signing + notarization** in GitHub Actions. Sign each Mach-O in the publish dir (`Mailvec.Mcp` apphost, `vec0.dylib`, and any self-contained .NET runtime `.dylib`s) with `--options runtime --timestamp --identifier com.mailvec.mcp`. Submit the zipped MCPB via `xcrun notarytool submit --wait`. After this, macOS recognizes the binary as `com.mailvec.mcp` and stops treating reads of `~/Library/Application Support/Mailvec/` as cross-app access.
2. **Interim mitigation: relocate user data** out of `~/Library/Application Support/Mailvec/` to a Unix-y path like `~/.local/share/mailvec/` (the binaries already live there per `ops/install.sh`). TCC's AppData service doesn't gate this location, so the prompt never fires regardless of code identity. Requires a one-shot migration in [`SharedConfig`](../../src/Mailvec.Core/Options/SharedConfig.cs) so existing installs aren't orphaned, plus updates to `ops/install-all.sh` and the MCPB manifest's `user_config` defaults.

Do **not** "fix" this by bundling the SQLite DB inside the MCPB's own `server/` directory. Claude Desktop owns the extension lifecycle and may wipe the directory on reinstall/update.

## Triage

- **First place to look when something's wrong:** `~/Library/Logs/Claude/mcp-server-mailvec.log`.
