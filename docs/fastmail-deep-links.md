# Fastmail webmail deep-links (optional)

Search and get-email tool results can include a `webmailUrl` that opens the message in Fastmail's web UI. The current implementation uses Fastmail's `msgid:<RFC-Message-ID>` search-URL syntax — zero JMAP calls, zero auth, but the user lands on a search-results pane and clicks once more to open the conversation. The feature is **opt-in**: with no account id configured, no link field is emitted.

## Config keys

Section `Fastmail` — bound by the MCP server and CLI:

- `Fastmail:AccountId` (env: `Fastmail__AccountId`) — JMAP account id, format `u` followed by 8 hex chars. Find yours by logging into <https://app.fastmail.com> and copying the `?u=...` query param off any URL in the address bar.
- `Fastmail:WebUrl` (env: `Fastmail__WebUrl`) — defaults to `https://app.fastmail.com`. Override only for self-hosted Fastmail-API-compatible deployments.

## Enabling for the CLI / HTTP MCP server

Drop the value into `appsettings.Local.json` next to the executable, or export the env var:

```sh
export Fastmail__AccountId=u1234abcd
dotnet run --project src/Mailvec.Mcp           # HTTP transport
dotnet run --project src/Mailvec.Cli -- search "ramen"
```

## Enabling for the Claude Desktop MCPB bundle

The install dialog has a **Fastmail account id (optional)** field — paste your `u…` id and you're done. Leaving it blank disables webmail links, which is the default. The value persists across future `--bump` upgrades as long as you toggle the extension off (vs uninstall) before re-installing. To change it later: Settings → Extensions → Mailvec → Configure.

## Future upgrade path

A "proper" upgrade to direct conversation links (resolving RFC Message-ID → JMAP Email-id via `Email/query` and emitting `mail/Inbox/<thread>.<email>?u=...`) needs a Fastmail API token and two new nullable cache columns on `messages` — see `WebmailLinkBuilder` for where to swap the URL shape.
