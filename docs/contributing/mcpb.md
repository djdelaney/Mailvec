# MCPB bundle — contributor notes

Release/build steps live in [`ops/mcpb-release.md`](../../ops/mcpb-release.md). User-facing install docs live in [`docs/claude-desktop.md`](../claude-desktop.md). This page is the code-edit gotcha collection — read it before editing `manifest.json`, `ops/build-mcpb.sh`, or any `[McpServerTool]` whose output crosses into Claude Desktop.

## manifest.json quirks

- **`manifest.json` user_config defaults must use `~/...`, not `${HOME}/...`.** Claude Desktop's MCPB host substitutes its own `${user_config.X}` tokens but passes shell-style `${HOME}` through verbatim. `PathExpansion.Expand` defensively handles `${HOME}` / `$HOME` regardless; tests in `PathExpansionTests.cs`.
- **Don't add `required: true` user_config fields in an upgrade.** The upgrade flow doesn't pre-fill new required fields, so the extension lands disabled and invisible in the connector picker. Make new fields `required: false` with a sensible default.

## Tool-shape quirks

- **Never set `UseStructuredContent = true` on an `[McpServerTool]` whose return type is `CallToolResult`.** The .NET SDK can't infer a meaningful schema from the generic return type and emits an invalid one; Claude Desktop's Zod validator then rejects the *entire extension*. Structured content still flows back at runtime via `CallToolResult.StructuredContent` regardless of the flag. If you need an advertised output schema, return a strongly-typed POCO instead.

## Triage

- **First place to look when something's wrong:** `~/Library/Logs/Claude/mcp-server-mailvec.log`.
