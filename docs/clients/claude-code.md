# Claude Code

**Transport**: HTTP (Streamable HTTP), against the launchd-managed MCP server on `127.0.0.1:3333`. Complete the [macOS installation](../getting-started-macos.md) first.

Claude Code can talk to local HTTP MCP servers directly — no stdio launcher, no bundle. The launchd service installed by `ops/install.sh` keeps the server running across reboots.

## Wire Claude Code

Add the server with:

```sh
claude mcp add --transport http mailvec http://127.0.0.1:3333
```

(or edit your project's `.mcp.json` / global Claude Code config to add a `mailvec` HTTP entry pointing at `http://127.0.0.1:3333`.)

For service checks and updates, use the [macOS operations guide](../install-macos.md); for log paths and retention, see [Logs](../logs.md).

## Known quirks

- **HTTP transport is sessionless.** As of MCP SDK 2.0 the Streamable HTTP transport is stateless by default, so there's no `initialize` handshake to complete and no `Mcp-Session-Id` to carry. `curl`-ing the endpoint directly works in one shot:

  ```bash
  curl -s -X POST http://127.0.0.1:3333/ -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
  ```

  Clients still on an older protocol revision may open with `initialize` instead; that path is answered normally, it just returns no session header.
- **No auth, 127.0.0.1 only.** Anything running on the same machine — under any local account — can call any tool. That's the threat model — see [`docs/security.md`](../security.md) (and [`docs/future-ideas.md`](../future-ideas.md) for the cloud-access framing).

## Verifying

Run `claude mcp list` in a shell (or `/mcp` inside a Claude Code session): `mailvec` should be listed as connected, with the version string from `manifest.json` / the repo-wide `<Version>` in `Directory.Build.props`. Asking the agent something archive-specific should trigger a `search_emails` call.
