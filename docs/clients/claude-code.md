# Claude Code

**Transport**: HTTP (Streamable HTTP), against the launchd-managed MCP server on `127.0.0.1:3333`.

Claude Code can talk to local HTTP MCP servers directly — no stdio launcher, no bundle. The launchd service installed by `ops/install.sh` keeps the server running across reboots.

## Install the HTTP service (one-time)

```sh
ops/install.sh
```

The installer publishes the indexer / embedder / MCP server to `~/.local/share/mailvec/<svc>/` and bootstraps four launchd agents. Verify the MCP server is reachable:

```sh
curl -s http://127.0.0.1:3333/health | jq .
```

A 200 with `{"status":"ok",...}` means you're ready to wire the client up.

## Wire Claude Code

Add the server with:

```sh
claude mcp add --transport http mailvec http://127.0.0.1:3333
```

(or edit your project's `.mcp.json` / global Claude Code config to add a `mailvec` HTTP entry pointing at `http://127.0.0.1:3333`.)

## Update to a new build

```sh
ops/redeploy.sh mcp     # republishes Mailvec.Mcp and kickstarts the launchd agent
```

`redeploy.sh` polls `/health` after kickstart, so a successful run means the new binary is serving. macOS's mmap inode-pinning means the kickstart is what actually takes effect — without it the running process keeps serving the old code.

## Logs

`~/Library/Logs/Mailvec/mailvec-mcp-<date>.log`. Daily rolling, 10 MB cap per file, 14 retained. Tail with:

```sh
tail -F ~/Library/Logs/Mailvec/mailvec-mcp-$(date +%Y%m%d).log
```

The launchd plist sets `MAILVEC_LAUNCHD=1` to suppress Serilog's Console sink in production, so launchd's own stdout/stderr capture stays small. If you ever need to revert that for debugging, edit `~/Library/LaunchAgents/com.mailvec.mcp.plist` and `launchctl bootout && launchctl bootstrap`.

## Known quirks

- **HTTP transport requires a session.** Streamable HTTP clients must `initialize` first, capture the `Mcp-Session-Id` response header, send `notifications/initialized`, and include the session header on every subsequent call. Claude Code handles this automatically, but if you ever `curl` the endpoint directly, calls without the session header silently 404.
- **No auth, 127.0.0.1 only.** Anything running as your user on the same machine can call any tool. That's the threat model — see [`docs/security.md`](../security.md) (and [`docs/future-ideas.md`](../future-ideas.md) for the cloud-access framing).

## Verifying

In a fresh Claude Code session: `mcp list` should show `mailvec` with the version string from `manifest.json` / `Mailvec.Mcp.csproj <Version>`. Asking the agent something archive-specific should trigger a `search_emails` call.
