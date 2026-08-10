# Wiring Mailvec into local MCP-capable agents

> **Scope: local, single-machine installs.** If you're running the Docker
> deployment behind a Cloudflare tunnel
> ([`docs/deploy-docker.md`](../deploy-docker.md),
> [`docs/remote-access-cloudflare.md`](../remote-access-cloudflare.md)), none of
> this applies — every client instead registers **one remote connector** at the
> public hostname and authenticates through Cloudflare Access. That includes
> Claude Desktop, which no longer uses the MCPB bundle in that setup. The pages
> here remain correct for a loopback install on a single Mac.

The Mailvec MCP server runs on your machine in two transports:

- **stdio** — the agent spawns the server as a child process and talks JSON-RPC over its stdin/stdout. Standard for Claude Desktop, Gemini CLI, Codex CLI.
- **HTTP** (Streamable HTTP, `127.0.0.1:3333`) — the agent dials a long-running launchd service. Standard for Claude Code.

Pick the transport your agent supports, then follow the per-client snippet below.

## Install the stdio launcher (one-time, prerequisite for stdio clients)

```sh
ops/install-stdio-launcher.sh
```

This publishes `~/.local/share/mailvec/mcp/Mailvec.Mcp.dll` and writes a launcher at `~/.local/bin/mailvec-mcp-stdio`. The launcher bakes in the env workarounds (sanitized PATH, missing `DOTNET_ROOT`) that bite when an agent spawns it as a child. User config (DB path, Maildir root, Ollama URL, Fastmail account id) comes from the shared `~/Library/Application Support/Mailvec/appsettings.Local.json` written by `ops/install.sh` — the launcher doesn't set those env vars itself, so a client doesn't need to either.

Smoke-test it before pasting the path into a config:

```sh
~/.local/bin/mailvec-mcp-stdio
```

You should see a few stderr log lines, then the process waits for JSON-RPC on stdin — that quiet hang is success; ^C to exit. (Don't redirect stdin from `/dev/null` — the server exits immediately on stdin EOF, which looks like a crash.) If it actually crashes, fix that before debugging a client config — the client's logs will be vague compared to running the launcher directly.

## Install the HTTP service (one-time, prerequisite for HTTP clients)

```sh
ops/install.sh
```

Boots launchd agents for the indexer / embedder / mbsync / MCP HTTP server. The MCP server binds to `127.0.0.1:3333` and is reachable from any local process.

## Per-client snippets

| Client | Transport | Status | Snippet |
| --- | --- | --- | --- |
| Claude Desktop | stdio | ✅ supported | [`claude-desktop.md`](claude-desktop.md) — install via MCPB bundle (`ops/build-mcpb.sh`), not the stdio launcher |
| Claude Code | HTTP | ✅ supported | [`claude-code.md`](claude-code.md) — points at `http://127.0.0.1:3333` |
| Any other MCP client | stdio or HTTP | ⚪ no snippet planned | Point it at `~/.local/bin/mailvec-mcp-stdio` or `http://127.0.0.1:3333` — see below |

Gemini CLI / Codex CLI / ChatGPT desktop rows lived here as Phase 5 placeholders
and were removed with that phase (CHANGELOG, 2026-08-10). Nothing about them was
blocked: they need no protocol work, and the stdio launcher plus the HTTP URL
already cover them. Writing a per-provider page per client is the part that
stopped earning its keep once one OAuth-gated endpoint served every client. The
authoring guide below is still here for anyone who hits a client-specific
spawning quirk worth recording.

## Authoring a new client snippet

When adding support for a client not in the table:

1. Confirm the client supports MCP and pick a transport (stdio if available — fewer moving parts).
2. Find the client's MCP-server config location (settings file path) and shape (JSON / TOML / GUI).
3. Write a snippet that uses `~/.local/bin/mailvec-mcp-stdio` (or the HTTP URL) **by absolute path** — relative paths break when the agent's CWD differs from yours.
4. Capture any spawning quirks (sanitized env vars, restricted PATH, sandbox boundaries) in the per-client doc — the equivalent of the Claude Desktop "PATH excludes /usr/local/share/dotnet" finding in CLAUDE.md.
5. Add a smoke-test recipe: ask the agent something like "use mailvec to find emails about lease renewal" and verify it returns hits.

Cross-link the new file from this README's table.
