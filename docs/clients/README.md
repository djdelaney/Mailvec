# Connect a local MCP client

**Docker on a Mac** (the supported local setup): connect Claude Code to `http://127.0.0.1:3333`, and Claude Desktop through `docker exec` over stdio, as in [Get started with Docker → On a Mac](../getting-started-docker.md#on-a-mac), step 4. Any other client that accepts a local HTTP MCP server uses the same URL. A Docker deployment on a Linux host behind Cloudflare uses [one remote connector](../remote-access-cloudflare.md) instead.

## Native macOS install (deprecated)

> **Deprecated.** The native launchd install runs every mail parser (MIME, PDF, Office, image, including native PDFium) inside the services that hold your archive and any API key, with no sandbox or memory limit around them. Run Mailvec with [Docker on the Mac](../getting-started-docker.md#on-a-mac) instead, where parsing is isolated in its own container; an existing install can [move across](../deploy-docker.md#moving-from-the-native-install). This page stays for existing installs until the native install is removed.

The rest of this page applies after the [native macOS installation](../getting-started-macos.md). It uses the archive and user settings installed on that same Mac.

| Client | Connection | Guide |
| --- | --- | --- |
| Claude Desktop | Local MCPB bundle (stdio) | [Claude Desktop](claude-desktop.md) |
| Claude Code | Local HTTP service at `127.0.0.1:3333` | [Claude Code](claude-code.md) |
| Other local MCP clients | Generic stdio launcher or local HTTP | See below |

### Other local clients

For a client that accepts a local HTTP MCP server, use `http://127.0.0.1:3333`. For a client that needs stdio, publish the generic launcher once:

```sh
ops/install-stdio-launcher.sh
~/.local/bin/mailvec-mcp-stdio
```

The second command should log to stderr, then wait quietly for JSON-RPC on stdin; press Ctrl-C to stop it. Do not redirect stdin from `/dev/null`, which makes it exit immediately. Configure the client to spawn the **absolute path** `~/.local/bin/mailvec-mcp-stdio` (expanded to your home directory). The launcher reads the shared Mailvec settings written during installation and handles the restricted `PATH` and `DOTNET_ROOT` common in client child processes.

After connecting, ask the client to search for an email you know exists and check that it calls `search_emails`. Client-specific setup notes belong in this directory only when a client has a genuine spawning or configuration quirk.
