# Fastmail webmail deep-links (optional)

Search and get-email tool results can include a `webmailUrl` that opens the message in Fastmail's web UI. The current implementation uses Fastmail's `msgid:<RFC-Message-ID>` search-URL syntax — zero JMAP calls, zero auth, but the user lands on a search-results pane and clicks once more to open the conversation. The feature is **opt-in**: with no account id configured, no link field is emitted.

## Config keys

Section `Fastmail` — bound by the MCP server and CLI:

- `Fastmail:AccountId` — JMAP account id, format `u` followed by 8 hex chars. Find yours by logging into <https://app.fastmail.com> and copying the `?u=...` query param off any URL in the address bar.
- `Fastmail:WebUrl` — defaults to `https://app.fastmail.com`. Override only for self-hosted Fastmail-API-compatible deployments.

## Where to set it

Single source of truth for every Mailvec binary (launchd-installed services, CLI, and the MCPB-bundled MCP that Claude Desktop runs):

```
~/Library/Application Support/Mailvec/appsettings.Local.json
```

`ops/install.sh` writes this file as part of the installer flow — it prompts you for the account id during install and re-uses any value it finds already there or in a legacy plist on reinstall. To change it later, edit the file directly:

```jsonc
{
  "Archive":  { "DatabasePath": "~/Library/Application Support/Mailvec/archive.sqlite" },
  "Ingest":   { "MaildirRoot":  "~/Mail/Fastmail" },
  "Ollama":   { "BaseUrl":      "http://localhost:11434" },
  "Fastmail": { "AccountId":    "u1234abcd" }
}
```

then restart the affected services (`ops/redeploy.sh mcp` or `launchctl kickstart -k gui/$(id -u)/com.mailvec.mcp`). Claude Desktop's bundled MCP picks up the change on its next launch.

Env vars still win as a one-off override (`Fastmail__AccountId=u1234abcd dotnet run --project src/Mailvec.Cli -- search ramen`), useful for development without editing the shared file.

## Future upgrade path

A "proper" upgrade to direct conversation links (resolving RFC Message-ID → JMAP Email-id via `Email/query` and emitting `mail/Inbox/<thread>.<email>?u=...`) needs a Fastmail API token and two new nullable cache columns on `messages` — see `WebmailLinkBuilder` for where to swap the URL shape.
