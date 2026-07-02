# Claude Desktop

**Transport**: stdio, via the MCPB bundle (NOT the generic stdio launcher).

Claude Desktop's Custom Connectors UI accepts an MCPB bundle directly, which packages a self-contained binary and the `vec0.dylib` in one file. The MCPB path is preferred over `~/.local/bin/mailvec-mcp-stdio` here because:

- The bundle is self-contained — bakes in the .NET runtime and `vec0.dylib`, so it works on a fresh machine without a separate Mailvec install on PATH.
- The bundle's binary lives at `~/Library/Application Support/Claude/extensions/<id>/`, which is a non-TCC location — Claude Desktop's spawned children can't read `~/Documents`, so a launcher there would silently fail.

## Install

```sh
ops/fetch-sqlite-vec.sh   # one-time, downloads vec0.dylib
ops/install.sh            # writes shared user-config; required before MCPB
ops/build-mcpb.sh         # produces dist/mailvec-<version>.mcpb
open dist/mailvec-*.mcpb  # hands it to Claude Desktop
```

`ops/install.sh` writes `~/Library/Application Support/Mailvec/appsettings.Local.json` with your DB path, Maildir root, Ollama endpoint, and Fastmail account id. The bundled MCP reads from the same file, so the only setting in Claude Desktop's install dialog is the **Log tool calls** debug toggle — everything else flows from the shared config and reinstalling the bundle never asks you to re-enter your account id.

## Update to a new build

```sh
ops/build-mcpb.sh --bump   # patch-bumps manifest.json AND Mailvec.Mcp.csproj <Version>, builds, opens
```

In Claude Desktop: Settings → Extensions → Mailvec → toggle off, accept the install prompt, quit and relaunch. Claude Desktop ignores re-installs of the same version, so the bump is what makes the new binary actually take effect. User config lives in the shared `appsettings.Local.json`, not in Claude Desktop's per-extension storage, so the upgrade carries forward whether you toggle off or uninstall.

## Logs

Claude Desktop captures the bundle's stderr to `~/Library/Logs/Claude/mcp-server-mailvec.log` — that's the log for this client. The bundle runs in stdio mode, which **disables** the Serilog rolling file (`~/Library/Logs/Mailvec/mailvec-mcp-<date>.log`) to avoid multiple stdio children racing on it, so don't look there for the bundle's output — that file only reflects the separate launchd HTTP MCP service used by Claude Code.

## Known quirks

- **`~/Documents` is unreadable** by Claude Desktop's spawned children even with Full Disk Access. The bundle avoids this by extracting to `~/Library/Application Support/Claude/extensions/`. If you ever see "file not found" for a path that obviously exists, this is why — keep your archive out of `~/Documents`.
- **PATH excludes `/usr/local/share/dotnet`.** The bundle dodges this by being self-contained (the .NET runtime is baked in). The generic stdio launcher (`~/.local/bin/mailvec-mcp-stdio`) handles the same issue by exporting `DOTNET_ROOT`, which is why we don't use the launcher here — the bundle is simpler.
- **Don't use `dotnet run` or any wrapper script that builds at start time** — its build chatter goes to stdout, which is the JSON-RPC channel. The bundle exec-s the compiled binary directly.

## Verifying

After install, ask Claude something archive-specific in a fresh chat. Expected: it calls `search_emails`, returns hits, and the connector picker shows "mailvec" with the version string from `manifest.json`. If the connector is missing, `~/Library/Logs/Claude/mcp-server-mailvec.log` is the first place to look.
