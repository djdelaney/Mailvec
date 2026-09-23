# Connect a local MCP client

These steps apply after the [macOS installation](../getting-started-macos.md). They use the archive and user settings installed on that same Mac. A Docker deployment behind Cloudflare uses [one remote connector](../remote-access-cloudflare.md) instead.

| Client | Connection | Guide |
| --- | --- | --- |
| Claude Desktop | Local MCPB bundle (stdio) | [Claude Desktop](claude-desktop.md) |
| Claude Code | Local HTTP service at `127.0.0.1:3333` | [Claude Code](claude-code.md) |
| Other local MCP clients | Generic stdio launcher or local HTTP | See below |

## Other local clients

For a client that accepts a local HTTP MCP server, use `http://127.0.0.1:3333`. For a client that needs stdio, publish the generic launcher once:

```sh
ops/install-stdio-launcher.sh
~/.local/bin/mailvec-mcp-stdio
```

The second command should log to stderr, then wait quietly for JSON-RPC on stdin; press Ctrl-C to stop it. Do not redirect stdin from `/dev/null`, which makes it exit immediately. Configure the client to spawn the **absolute path** `~/.local/bin/mailvec-mcp-stdio` (expanded to your home directory). The launcher reads the shared Mailvec settings written during installation and handles the restricted `PATH` and `DOTNET_ROOT` common in client child processes.

After connecting, ask the client to search for an email you know exists and check that it calls `search_emails`. Client-specific setup notes belong in this directory only when a client has a genuine spawning or configuration quirk.
