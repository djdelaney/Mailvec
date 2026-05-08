# Claude Desktop

**Transport**: stdio, via the MCPB bundle (NOT the generic stdio launcher).

Claude Desktop's Custom Connectors UI accepts an MCPB bundle directly, which packages a self-contained binary, the `vec0.dylib`, and the user-config schema in one file. The MCPB path is preferred over `~/.local/bin/mailvec-mcp-stdio` here because:

- The bundle prompts for `Archive__DatabasePath` / `Ingest__MaildirRoot` / `Ollama__BaseUrl` / `Fastmail__AccountId` through Claude Desktop's install dialog rather than asking the user to edit a JSON config.
- The bundle's binary lives at `~/Library/Application Support/Claude/extensions/<id>/`, which is a non-TCC location — Claude Desktop's spawned children can't read `~/Documents`, so a launcher there would silently fail.

## Install

```sh
ops/fetch-sqlite-vec.sh   # one-time, downloads vec0.dylib
ops/build-mcpb.sh         # produces dist/mailvec-<version>.mcpb
open dist/mailvec-*.mcpb  # hands it to Claude Desktop
```

Fill in the install dialog. The defaults match the standard layout (`~/Library/Application Support/Mailvec/archive.sqlite`, `~/Mail/Fastmail`, `http://localhost:11434`, no Fastmail webmail link).

## Update to a new build

```sh
ops/build-mcpb.sh --bump   # patch-bumps manifest.json AND Mailvec.Mcp.csproj <Version>, builds, opens
```

In Claude Desktop: Settings → Extensions → Mailvec → toggle off, accept the install prompt, quit and relaunch. Toggling off (vs uninstalling) preserves the user_config values across the upgrade. Claude Desktop ignores re-installs of the same version, so the bump is what makes the new binary actually take effect.

## Logs

Claude Desktop captures the bundle's stderr to `~/Library/Logs/Claude/mcp-server-mailvec.log`. The bundled binary also writes the same Serilog rolling file as the standalone server (`~/Library/Logs/Mailvec/mailvec-mcp-<date>.log`). Either is fine; the Serilog file is more durable across days.

## Known quirks

- **`~/Documents` is unreadable** by Claude Desktop's spawned children even with Full Disk Access. The bundle avoids this by extracting to `~/Library/Application Support/Claude/extensions/`. If you ever see "file not found" for a path that obviously exists, this is why — keep your archive out of `~/Documents`.
- **PATH excludes `/usr/local/share/dotnet`.** The bundle dodges this by being self-contained (the .NET runtime is baked in). The generic stdio launcher (`~/.local/bin/mailvec-mcp-stdio`) handles the same issue by exporting `DOTNET_ROOT`, which is why we don't use the launcher here — the bundle is simpler.
- **Don't use `dotnet run` or any wrapper script that builds at start time** — its build chatter goes to stdout, which is the JSON-RPC channel. The bundle exec-s the compiled binary directly.

## Verifying

After install, ask Claude something archive-specific in a fresh chat. Expected: it calls `search_emails`, returns hits, and the connector picker shows "mailvec" with the version string from `manifest.json`. If the connector is missing, `~/Library/Logs/Claude/mcp-server-mailvec.log` is the first place to look.
